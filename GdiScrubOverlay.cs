using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace MaskedDalamud;

/// <summary>
/// 【新方式】GPU-GDI レイヤードスクラブ ─ 既存スクラブ/GpuScrub とは完全独立。
///
/// 入力が確実に効く窓モデルは既に判明している:
///   既存 CPU スクラブ (MethodAOverlay) の WS_EX_LAYERED|WS_EX_TRANSPARENT
///   トップレベル窓は WindowFromPoint から除外され前面も奪わない
///   (= マウス/UI 入力が正常。公開版 0.1.4.0 で実証済)。
///
/// CPU スクラブの重さの本体は ②GPU→CPU 読み戻し Map ストール ＋ ③CPU
/// ピクセル変換ループ。本方式はこの 2 つを撤廃する:
///   - B8G8R8A8 の RT|SRV|GDI_COMPATIBLE テクスチャを 1 枚作る。
///   - Dalamud の RenderDrawDataInternal を **そのテクスチャへ直接** 純関数
///     呼びする (透明クリア → premultiplied。RenderDrawDataInternal は
///     渡した RT の Format でブラー中間も作るので B8G8R8A8 で内部一貫し
///     device-removed しない。RT|SRV なのでブラー SRV も満たす)。
///   - そのテクスチャの IDXGISurface1::GetDC を UpdateLayeredWindow へ
///     直接渡す → ReleaseDC。Map 読み戻しも CPU ループも一切なし。
///
/// DComp を使わないので前面/マウス問題は構造的に発生しない。
/// 既定 OFF・他のキャプチャ除外方式とは排他・試験。
/// </summary>
internal sealed unsafe class GdiScrubOverlay : IDisposable
{
    private const string ClassName = "MaskedDalamudGdiScrub";

    private readonly Plugin _plugin;
    private readonly DalamudImGuiInternals _internals = new();

    private ID3D11Device* _dev;
    private ID3D11DeviceContext* _ctx;
    private ID3D11Texture2D* _saveTex;
    private ID3D11ShaderResourceView* _saveSrv;   // 差分の「クリーン画」
    private ID3D11Texture2D* _curTex;             // UI 後 backbuffer の複製
    private ID3D11ShaderResourceView* _curSrv;
    private readonly DCompDiffCapture _diff = new();
    private bool _diffWarnOnce;

    private ID3D11Texture2D* _gdiTex;          // B8G8R8A8 RT|SRV|GDI_COMPATIBLE
    private ID3D11RenderTargetView* _rtv;
    private IDXGISurface1* _surf;              // _gdiTex の IDXGISurface1
    private uint _w, _h;
    private DXGI_FORMAT _fmt;                  // ゲーム swapchain 形式 (saveTex 用)
    private uint _svW, _svH;
    private DXGI_FORMAT _svFmt;

    private IntPtr _hwnd;
    private bool _classReg;
    private WndProc? _wndProc;
    private IntPtr _hInst;

    private MethodInfo? _runBefore, _runAfter, _rddi;
    private Type[]? _rddiParams;
    private object? _renderer;
    private Action? _beforeCb, _afterCb;
    private volatile bool _active;
    private bool _shown;
    // ホットリロード孤児判定用の世代トークン ([[OverlayGeneration]])。
    private string? _genToken;
    private bool _exclusiveFs;   // 排他フルスクリーン中 (DWM 合成されず overlay 不可視)
    private int _frame, _diag;
    private int _posX = int.MinValue, _posY = int.MinValue;
    // FPS 計測 (基本タブ「現在の状態」の FPS 表示用)。
    private double _fpsEma = 60;
    private readonly System.Diagnostics.Stopwatch _frameSw = System.Diagnostics.Stopwatch.StartNew();
    // 案 B: 直前フレームの vtx が 0 だったか (連続空なら UpdateLayeredWindow を省略可)。
    private bool _lastWasEmpty;
    private bool _overlayHasContent;       // 1 回でも非空を描いたか (初回 ULW を保証するため)

    public bool IsActive => _active;

    // [試験] KamiToolKit ミラーの厳密描画。GPU-GDI はオーバーレイを ImGui のリプレイで作るため、
    // backbuffer へ描いても乗らない。**オーバーレイ用テクスチャへ直接描く**必要がある。
    private readonly AtkQuadRenderer _quads = new();
    private readonly System.Collections.Generic.List<AtkQuadRenderer.Quad> _quadList = new();
    public string KamiStatus { get; private set; } = "未使用";

    /// <summary>厳密ミラーを今フレーム実行できるか (KamiMirror.Draw が同一フレームで参照)。</summary>
    public bool ExactMirrorReady => _active && _gdiTex != null && _rtv != null && !_quadFailed
                                 && (_diff.Ready || _saveTex != null);
    private bool _quadFailed;

    /// <summary>ミラーを backbuffer へ描く (差分キャプチャ経路用)。
    /// 描いた画素は After() の復元で必ず消えるので配信へは漏れない。</summary>
    private void DrawKamiMirrorToBackbuffer(ID3D11Texture2D* bb)
    {
        var km = _plugin.kamiMirror;
        if (km == null || !km.Active) { KamiStatus = "未使用"; return; }
        if (!_plugin.cfg.kamiExactColor) { KamiStatus = "近似 (厳密モード OFF)"; return; }
        if (bb == null || _saveTex == null || _dev == null || _ctx == null)
        { KamiStatus = "近似 (復元元なし)"; return; }
        if (!_diff.Ready) { KamiStatus = "近似 (差分キャプチャ未準備)"; return; }

        try
        {
            if (!_quads.Ensure(_dev)) { _quadFailed = true; KamiStatus = $"近似 ({_quads.LastError})"; return; }
            int n = km.SnapshotQuads(_quadList, _plugin.cfg.kamiAdditiveOnAdd);
            if (n == 0) { KamiStatus = "厳密モード (対象なし)"; return; }

            ID3D11RenderTargetView* rtv;
            if (_dev->CreateRenderTargetView((ID3D11Resource*)bb, null, &rtv) != 0 || rtv == null)
            { KamiStatus = "近似 (backbuffer RTV 生成失敗)"; return; }
            try { _quads.Execute(_ctx, rtv, _w, _h, _quadList); }
            finally { rtv->Release(); }
            KamiStatus = $"厳密モード OK クアッド={n}";
        }
        catch (Exception ex) { KamiStatus = $"厳密モード例外: {ex.Message}"; }
    }

    /// <summary>[退避経路] 差分キャプチャが使えない環境で、オーバーレイ用テクスチャへ直接描く。
    ///
    /// ImGui のリプレイ**より前**に描き、リプレイ側のクリアを無効にすることで
    /// 「ネイティブ UI がプラグイン窓の下に潜る」正しい前後関係になる。
    /// 戻り値: 描いたのでリプレイ側でクリアしてはいけない場合 true。</summary>
    private bool DrawKamiMirrorExact()
    {
        var km = _plugin.kamiMirror;
        if (km == null || !km.Active || !_plugin.cfg.kamiExactColor)
        { KamiStatus = km is { Active: true } ? "近似 (厳密モード OFF)" : "未使用"; return false; }
        if (_gdiTex == null || _rtv == null || _dev == null || _ctx == null)
        { KamiStatus = "近似 (描画先なし)"; return false; }

        try
        {
            if (!_quads.Ensure(_dev))
            {
                _quadFailed = true;
                KamiStatus = $"近似 ({_quads.LastError})";
                return false;
            }

            int n = km.SnapshotQuads(_quadList, _plugin.cfg.kamiAdditiveOnAdd);

            // リプレイのクリアを肩代わりする (透明で初期化)。
            var clear = stackalloc float[4] { 0f, 0f, 0f, 0f };
            _ctx->ClearRenderTargetView(_rtv, clear);

            if (n > 0) _quads.Execute(_ctx, _rtv, _w, _h, _quadList);
            KamiStatus = n > 0 ? $"厳密モード OK クアッド={n}" : "厳密モード (対象なし)";
            return true;
        }
        catch (Exception ex)
        {
            KamiStatus = $"厳密モード例外: {ex.Message}";
            return false;
        }
    }
    public string? LastError { get; private set; }
    public double LastFpsEma => _fpsEma;

    public GdiScrubOverlay(Plugin plugin) => _plugin = plugin;

    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IID_IDXGISurface1 = new("4AE63092-6327-4c1b-80AE-BFE12EA32B86");

    private static void OnGameThread(Action a)
    {
        if (Plugin.Framework.IsInFrameworkUpdateThread) { a(); return; }
        try { Plugin.Framework.RunOnFrameworkThread(a).GetAwaiter().GetResult(); } catch { }
    }

    public bool Enable()
    {
        if (_active) return true;
        LastError = null;
        try
        {
            _internals.Resolve();
            if (!_internals.Available) { LastError = $"reflection 解決失敗: {_internals.FailReason}"; return false; }

            _dev = (ID3D11Device*)_internals.GameDevicePtr;
            _ctx = (ID3D11DeviceContext*)_internals.GameDeviceContextPtr;
            var sc0 = (IDXGISwapChain*)_internals.GameSwapChainPtr;
            if (_dev == null || _ctx == null || sc0 == null) { LastError = "device/ctx/swapchain null"; return false; }

            DXGI_SWAP_CHAIN_DESC d;
            if (sc0->GetDesc(&d) != 0) { LastError = "swapchain GetDesc 失敗"; return false; }
            _w = d.BufferDesc.Width; _h = d.BufferDesc.Height; _fmt = d.BufferDesc.Format;
            if (_w < 16 || _h < 16) { LastError = $"swapchain サイズ異常 {_w}x{_h}"; return false; }

            if (!CreateSaveTex()) { LastError = "saveTex 生成失敗"; return false; }
            _svW = _w; _svH = _h; _svFmt = _fmt;
            if (!CreateGdiTex()) { LastError ??= "GDI テクスチャ生成失敗"; return false; }

            _renderer = _internals.Renderer;
            _rddi = _renderer?.GetType().GetMethod("RenderDrawDataInternal",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (_rddi == null) { LastError = "RenderDrawDataInternal 未解決"; return false; }
            _rddiParams = Array.ConvertAll(_rddi.GetParameters(), p => p.ParameterType);
            if (_rddiParams.Length != 4) { LastError = "RDDI 引数数 異常"; return false; }

            var imt = _internals.InterfaceManager!.GetType();
            _runBefore = imt.GetMethod("RunBeforeImGuiRender",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Action) }, null);
            _runAfter = imt.GetMethod("RunAfterImGuiRender",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Action) }, null);
            if (_runBefore == null || _runAfter == null) { LastError = "RunBefore/After 未解決"; return false; }

            if (!GetClientRect(out int gx, out int gy, out _, out _))
            { LastError = "クライアント矩形取得失敗"; return false; }

            bool win = false;
            OnGameThread(() => win = CreateLayeredWindow(gx, gy));
            if (!win) { LastError ??= "レイヤード窓生成失敗"; OnGameThread(DestroyWindowOnly); return false; }

            _beforeCb ??= Before;
            _afterCb ??= After;
            _genToken = _plugin.OverlayGen;   // 自分の世代を記録 (孤児判定用)
            _active = true;
            ArmBefore();
            StartPosTimer();
            Plugin.Log.Info("[GdiScrub] 開始 (GPU-GDI レイヤード・読み戻し撤廃)");
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Enable 例外: {ex.GetType().Name}: {ex.Message}";
            Plugin.Log.Error($"[GdiScrub] Enable 例外: {ex}");
            try { Teardown(); } catch { }
            return false;
        }
    }

    private bool CreateSaveTex()
    {
        var td = new D3D11_TEXTURE2D_DESC
        {
            Width = _w, Height = _h, MipLevels = 1, ArraySize = 1, Format = _fmt,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            // 差分キャプチャで「クリーン画」としてサンプリングするため SRV が要る。
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            CPUAccessFlags = 0, MiscFlags = 0,
        };
        ID3D11Texture2D* t;
        if (_dev->CreateTexture2D(&td, null, &t) != 0 || t == null) return false;
        _saveTex = t;

        _saveSrv = null;
        ID3D11ShaderResourceView* srv;
        if (_dev->CreateShaderResourceView((ID3D11Resource*)_saveTex, null, &srv) == 0 && srv != null)
            _saveSrv = srv;

        // UI を描き終わった backbuffer の複製 (差分の「現在画」)。
        _curTex = null; _curSrv = null;
        ID3D11Texture2D* c;
        if (_dev->CreateTexture2D(&td, null, &c) == 0 && c != null)
        {
            _curTex = c;
            ID3D11ShaderResourceView* csrv;
            if (_dev->CreateShaderResourceView((ID3D11Resource*)_curTex, null, &csrv) == 0 && csrv != null)
                _curSrv = csrv;
        }
        return true;
    }

    private bool CreateGdiTex()
    {
        // UpdateLayeredWindow/GetDC は B8G8R8A8_UNORM 必須。RT|SRV (ブラー用)、
        // GDI_COMPATIBLE (GetDC 用)。RenderDrawDataInternal は渡した RT の
        // Format でブラー中間も作る (decompile L122-123) ため B8G8R8A8 で
        // 内部一貫し device-removed しない。
        var td = new D3D11_TEXTURE2D_DESC
        {
            Width = _w, Height = _h, MipLevels = 1, ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET
                             | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE),
            CPUAccessFlags = 0,
            MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_GDI_COMPATIBLE,
        };
        ID3D11Texture2D* t;
        if (_dev->CreateTexture2D(&td, null, &t) != 0 || t == null)
        { LastError = "GDI テクスチャ CreateTexture2D 失敗"; return false; }
        _gdiTex = t;
        ID3D11RenderTargetView* rtv;
        if (_dev->CreateRenderTargetView((ID3D11Resource*)_gdiTex, null, &rtv) != 0 || rtv == null)
        { LastError = "RTV 生成失敗"; return false; }
        _rtv = rtv;
        IDXGISurface1* surf;
        fixed (Guid* g = &IID_IDXGISurface1)
            if (((ID3D11Texture2D*)_gdiTex)->QueryInterface(g, (void**)&surf).FAILED || surf == null)
            { LastError = "IDXGISurface1 QI 失敗"; return false; }
        _surf = surf;
        return true;
    }

    private IDXGISwapChain* LiveSwapChain()
    {
        var p = _internals.GetLiveGameSwapChain();
        if (p == IntPtr.Zero) p = _internals.GameSwapChainPtr;
        return (IDXGISwapChain*)p;
    }

    private void EnsureTextures(IDXGISwapChain* sc)
    {
        if (sc == null) return;
        DXGI_SWAP_CHAIN_DESC d;
        if (sc->GetDesc(&d) != 0) return;
        // 排他フルスクリーンを検知: WS_EX_LAYERED は DWM 合成されないため
        // overlay が手元に映らない。位置不整合で入力にも影響しうるので、
        // 排他中はオーバーレイを自動で隠す (scrub の backbuffer 消去は継続)。
        bool fs = d.Windowed == 0;
        if (fs != _exclusiveFs)
        {
            _exclusiveFs = fs;
            Plugin.Log.Info($"[GdiScrub] 排他フルスクリーン: {(fs ? "ON (overlay 自動 hide / scrub 継続)" : "OFF (overlay 復帰)")}");
        }
        uint w = d.BufferDesc.Width, h = d.BufferDesc.Height;
        var fmt = d.BufferDesc.Format;
        if (w < 16 || h < 16) return;
        if (_saveTex != null && w == _svW && h == _svH && fmt == _svFmt) return;
        _w = w; _h = h; _fmt = fmt;
        LogBackbufferRefs("resize前");
        // 旧 GPU は解放せず保持 (use-after-free 回避・モード変更は稀)。
        _saveTex = null; _saveSrv = null; _curTex = null; _curSrv = null;
        if (CreateSaveTex()) { _svW = w; _svH = h; _svFmt = fmt; }
        _gdiTex = null; _rtv = null; _surf = null;
        if (!CreateGdiTex()) { _svW = 0; return; } // 次フレーム再試行
        LogBackbufferRefs("resize後");
        _posX = _posY = int.MinValue;              // 窓サイズ再適用
        Plugin.Log.Info($"[GdiScrub] テクスチャ再生成 {_w}x{_h} (窓は維持)");
    }

    private ID3D11Texture2D* GetBackbuffer(IDXGISwapChain* sc)
    {
        if (sc == null) return null;
        ID3D11Texture2D* bb; var iid = IID_ID3D11Texture2D;
        if (sc->GetBuffer(0, &iid, (void**)&bb) != 0 || bb == null) return null;
        return bb;
    }

    // ── 診断: ゲーム backbuffer の残存参照数を計測 (resizeBuffers 失敗原因の切り分け) ──
    // GetBuffer の +1 を Release() すると「解放後の残存参照数」が返る。enable/disable/
    // resize 跨ぎで増え続けるなら我々のリークが backbuffer を握っている証拠。
    private void LogBackbufferRefs(string tag)
    {
        if (!_plugin.cfg.debugEnabled) return;   // 診断ログはデバッグ有効時のみ
        try
        {
            var sc = LiveSwapChain();
            if (sc == null) { Plugin.Log.Info($"[GdiScrub][diag] {tag}: swapchain=null"); return; }
            ID3D11Texture2D* bb; var iid = IID_ID3D11Texture2D;
            if (sc->GetBuffer(0, &iid, (void**)&bb) == 0 && bb != null)
            {
                uint refs = bb->Release();
                Plugin.Log.Info($"[GdiScrub][diag] {tag}: backbuffer 残存参照={refs} {_w}x{_h}");
            }
            else Plugin.Log.Info($"[GdiScrub][diag] {tag}: GetBuffer 失敗");
        }
        catch (Exception ex) { Plugin.Log.Info($"[GdiScrub][diag] {tag}: 例外 {ex.Message}"); }
    }

    private void ArmBefore()
    {
        if (!_active) return;
        try { _runBefore!.Invoke(_internals.InterfaceManager, new object[] { _beforeCb! }); }
        catch (Exception ex) { Plugin.Log.Error($"[GdiScrub] ArmBefore 失敗→停止: {ex.Message}"); _active = false; }
    }

    private void Before()
    {
        if (!_active) return;
        // ホットリロードで新ロードが所有権を取得していたら孤児 → 即スクラブ停止。
        if (!OverlayGeneration.IsCurrent(_genToken)) { SelfTerminateAsOrphan(); return; }
        try
        {
            var sc = LiveSwapChain();
            EnsureTextures(sc);
            var bb = GetBackbuffer(sc);
            if (bb != null && _saveTex != null)
            {
                try
                {
                    _ctx->CopyResource((ID3D11Resource*)_saveTex, (ID3D11Resource*)bb);
                    // [試験] ミラーを backbuffer へ描く。差分キャプチャが拾い、
                    // After() 末尾の復元で配信からは消える (DComp と同じ経路)。
                    DrawKamiMirrorToBackbuffer(bb);
                }
                finally { bb->Release(); }
            }
            _runAfter!.Invoke(_internals.InterfaceManager, new object[] { _afterCb! });
        }
        catch (Exception ex) { Plugin.Log.Error($"[GdiScrub] Before 例外→停止: {ex.Message}"); _active = false; }
    }

    private void After()
    {
        if (!_active) return;
        try
        {
            var dd = ImGui.GetDrawData();
            int vtx = (!dd.IsNull && dd.Valid) ? dd.TotalVtxCount : 0;

            // 実効更新間隔 (共通設定。自動軽量化 X/Y は 0.1.5.3 で削除)。
            int interval = Math.Max(1, _plugin.cfg.methodAUpdateIntervalFrames);

            // 実効 FPS の計測 (基本タブ「現在の状態」表示用)。
            double ms = _frameSw.Elapsed.TotalMilliseconds;
            _frameSw.Restart();
            if (ms > 0.1) _fpsEma = _fpsEma * 0.9 + (1000.0 / ms) * 0.1;

            _frame++;
            bool refreshFull = _frame % interval == 0;

            // 排他フルスクリーン中はオーバーレイが表示されないため
            // replay/GetDC/UpdateLayeredWindow をスキップ (CPU/GPU 節約)。
            // scrub の backbuffer 消去は配信非表示維持のため毎フレーム続行。
            // ① UI を GDI テクスチャへ直接複製描画 (透明クリア→premultiplied B8G8R8A8)
            // ── 差分キャプチャ方式 ──
            // 以前は ImGui の頂点データを透明なテクスチャへリプレイしていたが、
            // Dalamud のレンダラは**描画先をサンプリングして背景ぼかしを作る**ため、
            // 空のテクスチャへ描くとぼかす対象が無く**ウィンドウ背景が真っ黒**になっていた。
            // また ImDrawList.AddCallback によるネイティブ直描き (BossModReborn 等) も拾えない。
            //
            // 「描き終わった backbuffer」と「描く前のクリーン画」を突き合わせ、
            // **変化した画素だけ**をオーバーレイへ書く方式に変更する。誰がどう描いたかを問わない。
            bool didReplay = false;
            if (refreshFull && !_exclusiveFs && _gdiTex != null && _rtv != null)
            {
                try
                {
                    bool ready = _curTex != null && _curSrv != null && _saveSrv != null && _diff.Ensure(_dev);
                    if (ready)
                    {
                        var sc0 = LiveSwapChain();
                        var bb0 = GetBackbuffer(sc0);
                        if (bb0 != null)
                        {
                            try
                            {
                                _ctx->CopyResource((ID3D11Resource*)_curTex, (ID3D11Resource*)bb0);
                                _diff.Execute(_ctx, _rtv, _curSrv, _saveSrv, _w, _h);
                                didReplay = true;
                            }
                            finally { bb0->Release(); }
                        }
                    }
                    else if (!_diffWarnOnce)
                    {
                        _diffWarnOnce = true;
                        Plugin.Log.Warning($"[GdiScrub] 差分キャプチャ準備不可 → 従来のリプレイへ: {_diff.LastError}");
                    }

                    // 差分が使えない環境は従来のリプレイへ落とす (何も出ないより良い)。
                    if (!didReplay && !dd.IsNull && dd.Valid)
                    {
                        bool mirrored = DrawKamiMirrorExact();
                        var args = new object[4];
                        args[0] = System.Reflection.Pointer.Box((void*)_gdiTex, _rddiParams![0]);
                        args[1] = System.Reflection.Pointer.Box((void*)_rtv, _rddiParams![1]);
                        args[2] = dd;
                        args[3] = !mirrored;
                        _rddi!.Invoke(_renderer, args);
                        didReplay = true;
                    }
                }
                catch (Exception ex) { Plugin.Log.Warning($"[GdiScrub] オーバーレイ生成例外: {ex.Message}"); }
            }

            // ② backbuffer をクリーンへ戻す (スクラブ・毎フレーム必須)
            var sc = LiveSwapChain();
            var bb = GetBackbuffer(sc);
            if (bb != null && _saveTex != null)
            {
                try { _ctx->CopyResource((ID3D11Resource*)bb, (ID3D11Resource*)_saveTex); }
                finally { bb->Release(); }
            }

            // ── 案 B: 空 DrawData フレーム は UpdateLayeredWindow をスキップ ──
            // 直前も今回も vtx=0 = オーバーレイは既に「透明クリア状態」なので
            // ULW を呼ぶ意味がない (GetDC 同期 + GDI/DWM 経路コストを丸ごと節約)。
            // 初回 (まだ何も描いていない) も同様にスキップ。
            // 差分方式は ImGui の頂点数と無関係に「変化した画素」を拾うため、
            // vtx=0 でもネイティブ直描きが入っていることがある。空判定は使わない。
            bool isEmptyNow = vtx == 0 && !_diff.Ready;
            bool skipUlw = isEmptyNow && _lastWasEmpty && !_overlayHasContent;
            if (didReplay && !isEmptyNow) _overlayHasContent = true;

            // ③ GetDC → UpdateLayeredWindow (Map 読み戻しも CPU ループも無し)
            if (refreshFull && didReplay && _shown && !_exclusiveFs && !skipUlw && _surf != null && GetClientRect(out int sx, out int sy, out _, out _))
            {
                // [試験] 領域限定 ULW: ImDrawData の触れた矩形 (UI BBox) を計算し、
                // UpdateLayeredWindowIndirect の prcDirty に渡して DWM の per-pixel
                // alpha 再合成を UI 領域だけに絞る軽量化。半透明 UI が画面の一部しか
                // 占めないシーン (通常 HUD) で大きな効果。Splatoon 等フル画面半透明
                // UI では効果限定。配信側は不変 (backbuffer save/restore は維持)。
                int dx = 0, dy = 0, dw = 0, dh = 0;
                bool useDirty = false;
                if (_plugin.cfg.gdiScrubRegionLimit && !dd.IsNull && dd.Valid)
                {
                    ComputeUiBBox(dd, out int rx, out int ry, out int rw, out int rh);
                    if (rx >= 0 && rw > 0 && rh > 0)
                    {
                        // クライアント領域内にクランプ (オーバーフロー保険)。
                        dx = Math.Max(0, rx);
                        dy = Math.Max(0, ry);
                        dw = Math.Min(rw, (int)_w - dx);
                        dh = Math.Min(rh, (int)_h - dy);
                        if (dw > 0 && dh > 0) useDirty = true;
                    }
                }

                // GetDC は対象サーフェスが RT バインド中だと失敗するため解除。
                _ctx->OMSetRenderTargets(0, null, null);
                HDC hdc;
                if (_surf->GetDC(false, &hdc).SUCCEEDED && hdc != HDC.NULL)
                {
                    try
                    {
                        var size = new SIZE { cx = (int)_w, cy = (int)_h };
                        var ptDst = new POINT { x = sx, y = sy };
                        var ptSrc = new POINT { x = 0, y = 0 };
                        var blend = new BLENDFUNCTION
                        {
                            BlendOp = 0, BlendFlags = 0,
                            SourceConstantAlpha = 255, AlphaFormat = 1, // AC_SRC_ALPHA
                        };

                        if (useDirty)
                        {
                            // UpdateLayeredWindowIndirect で prcDirty 指定。
                            var dirty = new RECT { left = dx, top = dy, right = dx + dw, bottom = dy + dh };
                            var info = new UPDATELAYEREDWINDOWINFO
                            {
                                cbSize = (uint)Marshal.SizeOf<UPDATELAYEREDWINDOWINFO>(),
                                hdcDst = IntPtr.Zero,
                                pptDst = &ptDst,
                                psize = &size,
                                hdcSrc = (IntPtr)hdc.Value,
                                pptSrc = &ptSrc,
                                crKey = 0,
                                pblend = &blend,
                                dwFlags = ULW_ALPHA,
                                prcDirty = &dirty,
                            };
                            UpdateLayeredWindowIndirect(_hwnd, ref info);
                        }
                        else
                        {
                            UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref ptDst, ref size,
                                (IntPtr)hdc.Value, ref ptSrc, 0, ref blend, ULW_ALPHA);
                        }
                    }
                    finally { _surf->ReleaseDC(null); }
                }
            }

            // 案 B: 空フレーム状態を次フレームへ持ち越し。
            _lastWasEmpty = isEmptyNow;

            if (++_diag % 180 == 1)
            {
                Plugin.Log.Info($"[GdiScrub] diag vtx={vtx} shown={_shown} gdi={(_gdiTex != null)} "
                    + $"surf={(_surf != null)} {_w}x{_h} interval={interval} fps={_fpsEma:F1} "
                    + $"refresh={(refreshFull ? "Y" : "N")} ulwSkip={(skipUlw ? "Y" : "N")}");
            }

            // ④ 位置/前面追従 (レイヤード窓は前面を奪わない=既存CPUスクラブで実証)
            if (GetClientRect(out int gx, out int gy, out uint gw, out uint gh))
            {
                bool fg = GetForegroundWindow() == GetOwnFfxivHwnd();
                SetShown(fg && !_exclusiveFs, gw, gh);
                SyncPosition(gx, gy);
            }
        }
        catch (Exception ex) { Plugin.Log.Error($"[GdiScrub] After 例外→停止: {ex.Message}"); _active = false; }
        if (_active) ArmBefore();
    }

    private System.Threading.Timer? _posTimer;
    private void StartPosTimer()
    {
        try { _posTimer?.Dispose(); _posTimer = new System.Threading.Timer(_ => PosTick(), null, 8, 8); }
        catch (Exception ex) { Plugin.Log.Warning($"[GdiScrub] posTimer 失敗: {ex.Message}"); }
    }
    private void PosTick()
    {
        if (!_active || _hwnd == IntPtr.Zero) return;
        try
        {
            if (GetForegroundWindow() != GetOwnFfxivHwnd()) return;
            if (GetClientRect(out int gx, out int gy, out _, out _)) SyncPosition(gx, gy);
        }
        catch { }
    }

    private void SyncPosition(int x, int y)
    {
        if (_hwnd == IntPtr.Zero || !_shown) return;
        if (x == _posX && y == _posY) return;
        _posX = x; _posY = y;
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, (int)_w, (int)_h, SWP_NOACTIVATE);
    }

    private void SetShown(bool show, uint w, uint h)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (show && !_shown) { ShowWindow(_hwnd, SW_SHOWNOACTIVATE); _shown = true; }
        else if (!show && _shown) { ShowWindow(_hwnd, SW_HIDE); _shown = false; }
    }

    public void FrameworkTick()
    {
        if (!_active || _hwnd == IntPtr.Zero) return;
        try { SetShown(GetForegroundWindow() == GetOwnFfxivHwnd() && !_exclusiveFs, _w, _h); }
        catch { }
    }

    // 孤児の自己停止 (Before/描画スレッドから)。スクラブ停止 + 窓を隠す/破棄のみ。
    // D3D には触れない (teardown D3D は AV クラッシュ源・実証済)。GPU は bounded leak 容認。
    private void SelfTerminateAsOrphan()
    {
        Plugin.Log.Info("[GdiScrub] 新ロード検出 → 孤児として自己停止 (scrub 停止・窓破棄)");
        _active = false;
        try { _posTimer?.Dispose(); _posTimer = null; } catch { }
        try { if (_hwnd != IntPtr.Zero) { ShowWindow(_hwnd, SW_HIDE); DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; } } catch { }
        _shown = false;
    }

    public void Disable()
    {
        _active = false;
        Teardown();
        Plugin.Log.Info("[GdiScrub] 停止");
    }

    private void Teardown()
    {
        _active = false;
        try { _posTimer?.Dispose(); _posTimer = null; } catch { }
        OnGameThread(DestroyWindowOnly);
        // GPU は use-after-free 回避のため解放せず保持 (有界リーク)。
        // ※ teardown では D3D context を触らない (Dispose 中の Flush/OMSet は AV クラッシュ・実証済)。
        try { _quads.Dispose(); } catch { }
        try { _diff.Dispose(); } catch { }
        _saveTex = null; _saveSrv = null; _curTex = null; _curSrv = null;
        _gdiTex = null; _rtv = null; _surf = null;
        _dev = null; _ctx = null;
    }

    private void DestroyWindowOnly()
    {
        try { if (_hwnd != IntPtr.Zero) { ShowWindow(_hwnd, SW_HIDE); DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; } } catch { }
        _shown = false;
    }

    public void Dispose() { _active = false; Teardown(); }

    private bool CreateLayeredWindow(int x, int y)
    {
        try
        {
            _hInst = GetModuleHandleW(null);
            if (!RegisterClassIfNeeded()) { LastError = "RegisterClass 失敗"; return false; }
            // 既存 CPU スクラブと同一の入力安全な窓。
            _hwnd = CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                ClassName, "MaskedDalamud GDI Scrub", WS_POPUP,
                x, y, (int)_w, (int)_h, IntPtr.Zero, IntPtr.Zero, _hInst, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) { LastError = $"CreateWindowExW 失敗 {Marshal.GetLastWin32Error()}"; return false; }
            if (!SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE))
                Plugin.Log.Warning($"[GdiScrub] WDA 失敗 {Marshal.GetLastWin32Error()} (続行)");
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            _shown = true;
            Plugin.Log.Info($"[GdiScrub] レイヤード窓生成 OK {_w}x{_h} fmt={_fmt}");
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"窓生成例外: {ex.Message}";
            Plugin.Log.Error($"[GdiScrub] CreateLayeredWindow 例外: {ex}");
            return false;
        }
    }

    private bool RegisterClassIfNeeded()
    {
        if (_classReg) return true;
        _wndProc = DefWnd;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInst, lpszClassName = ClassName,
        };
        if (RegisterClassExW(ref wc) == 0)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != 1410) return false;
        }
        _classReg = true; return true;
    }
    // WS_EX_LAYERED|WS_EX_TRANSPARENT が OS レベルでクリックスルー+
    // WindowFromPoint 除外を担うため素の DefWindowProc でよい。
    private static IntPtr DefWnd(IntPtr h, uint m, IntPtr w, IntPtr l) => DefWindowProcW(h, m, w, l);

    private IntPtr _cachedHwnd;
    private EnumWindowsProc? _enumProc;
    private IntPtr GetOwnFfxivHwnd()
    {
        if (_cachedHwnd != IntPtr.Zero && IsWindow(_cachedHwnd)) return _cachedHwnd;
        uint myPid = GetCurrentProcessId();
        IntPtr found = IntPtr.Zero;
        var sb = new System.Text.StringBuilder(64);
        _enumProc = (h, _) =>
        {
            try
            {
                sb.Clear();
                if (GetClassNameW(h, sb, sb.Capacity) > 0 && sb.ToString() == "FFXIVGAME")
                {
                    GetWindowThreadProcessId(h, out uint pid);
                    if (pid == myPid) { found = h; return false; }
                }
            }
            catch { }
            return true;
        };
        try { EnumWindows(_enumProc, IntPtr.Zero); } catch { }
        _cachedHwnd = found; return found;
    }

    /// <summary>[試験] ImDrawData の各描画コマンド ClipRect を合算して
    /// UI が実際に使っている矩形 (オーバーレイ画素座標) を求める。
    /// rw/rh = 0 は「UI 無し」、rx&lt;0 は算出失敗 (=full-frame fallback)。
    /// ScrubOverlay の同名メソッドを GdiScrub に独立移植。</summary>
    private void ComputeUiBBox(ImDrawDataPtr dd, out int rx, out int ry, out int rw, out int rh)
    {
        rx = ry = rw = rh = -1;
        try
        {
            float dpx = dd.DisplayPos.X, dpy = dd.DisplayPos.Y;
            float dsw = dd.DisplaySize.X, dsh = dd.DisplaySize.Y;
            if (dsw <= 0 || dsh <= 0) return;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = -1f, maxY = -1f;
            int n = dd.CmdListsCount;
            for (int i = 0; i < n; i++)
            {
                ImDrawList* cl = dd.CmdLists[i];
                if (cl == null) continue;
                ref var buf = ref cl->CmdBuffer;
                int cc = buf.Size;
                for (int c = 0; c < cc; c++)
                {
                    var cmd = buf[c];
                    if (cmd.ElemCount == 0) continue;
                    var r = cmd.ClipRect;          // 画面座標 (x0,y0,x1,y1)
                    if (r.Z <= r.X || r.W <= r.Y) continue;
                    if (r.X < minX) minX = r.X;
                    if (r.Y < minY) minY = r.Y;
                    if (r.Z > maxX) maxX = r.Z;
                    if (r.W > maxY) maxY = r.W;
                }
            }
            if (maxX <= 0f || maxY <= 0f || maxX <= minX || maxY <= minY)
            { rx = ry = 0; rw = rh = 0; return; }   // UI 無し
            int x0 = Math.Clamp((int)Math.Floor(minX - dpx), 0, (int)dsw);
            int y0 = Math.Clamp((int)Math.Floor(minY - dpy), 0, (int)dsh);
            int x1 = Math.Clamp((int)Math.Ceiling(maxX - dpx), 0, (int)dsw);
            int y1 = Math.Clamp((int)Math.Ceiling(maxY - dpy), 0, (int)dsh);
            rx = x0; ry = y0; rw = Math.Max(0, x1 - x0); rh = Math.Max(0, y1 - y0);
        }
        catch { rx = ry = rw = rh = -1; }
    }

    private bool GetClientRect(out int x, out int y, out uint w, out uint h)
    {
        x = y = 0; w = h = 0;
        var hwnd = GetOwnFfxivHwnd();
        if (hwnd == IntPtr.Zero) return false;
        if (!GetClientRect(hwnd, out RECT rc)) return false;
        int cw = rc.right - rc.left, ch = rc.bottom - rc.top;
        if (cw < 16 || ch < 16) return false;
        var tl = new POINT { x = rc.left, y = rc.top };
        if (!ClientToScreen(hwnd, ref tl)) return false;
        x = tl.x; y = tl.y; w = (uint)cw; h = (uint)ch;
        return true;
    }

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint ULW_ALPHA = 0x00000002;

    private delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? n);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr par, IntPtr menu, IntPtr inst, IntPtr p);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr h, uint aff);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
        ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    // UpdateLayeredWindowIndirect: prcDirty (dirty rect) を指定して DWM 再合成
    // 領域を絞れる ULW の indirect 版。Win32 UPDATELAYEREDWINDOWINFO 構造体を
    // 受け取る (cbSize 必須・null ポインタ可)。本実装では cfg.gdiScrubRegionLimit
    // ON 時に ImDrawData ベースの UI BBox を prcDirty に渡して軽量化する。
    [StructLayout(LayoutKind.Sequential)]
    private struct UPDATELAYEREDWINDOWINFO
    {
        public uint cbSize;
        public IntPtr hdcDst;
        public POINT* pptDst;
        public SIZE* psize;
        public IntPtr hdcSrc;
        public POINT* pptSrc;
        public uint crKey;
        public BLENDFUNCTION* pblend;
        public uint dwFlags;
        public RECT* prcDirty;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindowIndirect(IntPtr hwnd, ref UPDATELAYEREDWINDOWINFO info);
}
