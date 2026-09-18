using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace MaskedDalamud;

/// <summary>
/// 【推奨】DComp Composition Swapchain ─ DirectComposition Visual + DXGI Composition
/// Swapchain (premultiplied alpha) で UI を表示し、UpdateLayeredWindow + DWM の
/// per-pixel alpha 合成コストを撤廃した低負荷方式。
///
/// フロー:
///   Before: ゲーム backbuffer (UI を描く前) を _saveTex に save
///     ↓ Dalamud がメインレンダリング (backbuffer に UI を描く)
///   After:  Dalamud の RenderDrawDataInternal を中間 RT (_rt) に純関数呼びして UI を
///           再描画 → backbuffer を _saveTex から復元 (配信非表示) → 中間 RT を
///           composition swapchain にコピー → Present(1,0) で表示
///
/// 入力安全: WS_EX_NOREDIRECTIONBITMAP + WS_EX_TRANSPARENT + WM_NCHITTEST=HTTRANSPARENT。
/// 独立 DXGI factory (CreateDXGIFactory2) でゲーム swapchain と分離しモード遷移衝突を回避。
/// Present は sync interval=1 (vsync 同期)。DO_NOT_WAIT は GShade(ReShade) 環境で
/// composition が表示されない不具合があったため使わない (実機確認済)。
/// </summary>
internal sealed unsafe class DCompOverlay : IDisposable
{
    public readonly struct DiagSnapshot
    {
        public readonly double FpsEma;
        public readonly long FrameCount;
        public readonly double AvgFrameMs;
        public readonly int Width, Height;
        public readonly bool DCompOk;
        public DiagSnapshot(double fps, long frames, double avgMs, int w, int h, bool ok)
        { FpsEma = fps; FrameCount = frames; AvgFrameMs = avgMs; Width = w; Height = h; DCompOk = ok; }
    }

    private const string ClassName = "MaskedDalamudDComp";

    private readonly Plugin _plugin;
    private readonly DalamudImGuiInternals _internals = new();

    private ID3D11Device* _dev;
    private ID3D11DeviceContext* _ctx;

    private ID3D11Texture2D* _saveTex;          // backbuffer save (UI 消去用)
    private ID3D11ShaderResourceView* _saveSrv; // 同上の SRV (差分キャプチャ用)
    private uint _svW, _svH;
    private DXGI_FORMAT _svFmt;

    // [試験] capture mode 1/2 用: UI 描画「後」の backbuffer コピー。
    // mode 1 (全画面ミラー) では中間 RT へ直接コピーするため未使用、mode 2 (差分) で SRV として使う。
    private ID3D11Texture2D* _curTex;
    private ID3D11ShaderResourceView* _curSrv;
    private readonly DCompDiffCapture _diff = new();
    private bool _diffWarnOnce;

    // [試験] KamiToolKit ミラーの厳密描画 (Atk の合成式をそのまま実装する専用シェーダ)。
    private readonly AtkQuadRenderer _quads = new();
    private readonly System.Collections.Generic.List<AtkQuadRenderer.Quad> _quadList = new();
    private bool _quadWarnOnce;
    public string KamiStatus { get; private set; } = "未使用";

    /// <summary>厳密ミラーを「今フレーム実行できる」か。KamiMirror.Draw が
    /// **同一フレーム内で**参照するための判定 (前フレームの結果を使うと、落ちたフレームに
    /// ImGui もシェーダも描かず手元から UI が消える)。
    ///
    /// **「一度失敗したら以後ずっと false」にしてはいけない。**
    /// 一時的な失敗でフラグが立つと、シェーダ側は動いているのに ImGui 側が近似で描き始め、
    /// **画像が二重描画**されて色が飛ぶ (実機で exact=False / ImGui描画=15 として確認)。
    /// シェーダの生成状態 (_quads.Ready) をそのまま見る = 実体と常に一致する。</summary>
    public bool ExactMirrorReady => _active && _saveTex != null && _quads.Ready;

    private ID3D11Texture2D* _rt;               // 中間 RT (Dalamud の UI 再描画先)
    private ID3D11RenderTargetView* _rtv;
    private uint _w, _h;
    private DXGI_FORMAT _fmt;

    private IDXGISwapChain1* _compSwap;
    private IDCompositionDevice* _dcompDev;
    private IDCompositionTarget* _dcompTarget;
    private IDCompositionVisual* _dcompVisual;

    // [試験] ReShade 迂回: 本物 System32\dxgi.dll の CreateDXGIFactory2 を直接呼ぶための関数ポインタ。
    // ReShade のプロキシ dxgi を通さず factory を作ることで、REST 等のアドオンが我々の合成
    // スワップチェインをラップ・追跡しないようにする狙い。
    private delegate* unmanaged<uint, Guid*, void**, int> _realCreateFactory2;
    private bool _bypassResolved;
    public string BypassStatus { get; private set; } = "未使用";

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
    // このインスタンスを生成した Plugin ロードの世代トークン。最新でなくなったら
    // (= ホットリロードで新ロードが来たら) 孤児として自己停止する ([[OverlayGeneration]])。
    private string? _genToken;
    private bool _exclusiveFs;
    // 排他フルスクリーン中も DComp の手元表示を試みる。実機検証で「フルスクリーンでも
    // 手元 UI が表示される (効果あり)」と確認できたため既定 true。DComp は DirectComposition
    // 経路で FFXIV の (flip-model) フルスクリーンでも DWM 合成に乗り表示できる。
    // 万一映らない環境でも表示経路のみのため無害 (D3D 寿命に非干渉)。
    private bool _tryShowInFullscreen = true;
    private int _frame, _diag;
    private int _posX = int.MinValue, _posY = int.MinValue;
    private bool _pendingRebuild;               // Present 異常時に次フレームで再生成

    private double _fpsEma = 60;
    private double _frameMsEma;
    private long _frameCount;
    private DiagSnapshot _lastDiag;
    private readonly System.Diagnostics.Stopwatch _frameSw = System.Diagnostics.Stopwatch.StartNew();

    public bool IsActive => _active;
    public string? LastError { get; private set; }
    public DiagSnapshot LastDiag => _lastDiag;

    public DCompOverlay(Plugin plugin) => _plugin = plugin;

    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IID_IDXGIDevice    = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IID_IDXGIFactory2  = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
    private static readonly Guid IID_IDCompositionDevice = new("C37EA93A-E7AA-450D-B16F-9746CB0407F3");

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
            if (!CreateRt()) { LastError ??= "中間 RT 生成失敗"; return false; }

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
            OnGameThread(() => win = CreateDcompWindow(gx, gy));
            if (!win) { LastError ??= "DComp 窓 / 合成生成失敗"; OnGameThread(DestroyWindowOnly); return false; }

            _beforeCb ??= Before;
            _afterCb ??= After;
            _genToken = _plugin.OverlayGen;   // 自分の世代を記録 (孤児判定用)
            _active = true;
            ArmBefore();
            StartPosTimer();
            Plugin.Log.Info($"[DComp] 開始 (Composition Swapchain / vsync 同期 {_w}x{_h} fmt={_fmt})");
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Enable 例外: {ex.GetType().Name}: {ex.Message}";
            Plugin.Log.Error($"[DComp] Enable 例外: {ex}");
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
            // 差分キャプチャ (mode 2) で clean 側として SRV サンプリングするため SHADER_RESOURCE 必須。
            // (これが無いと CreateShaderResourceView が失敗し mode 2 が丸ごと不成立になる)
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            CPUAccessFlags = 0, MiscFlags = 0,
        };
        ID3D11Texture2D* t;
        if (_dev->CreateTexture2D(&td, null, &t) != 0 || t == null) return false;
        _saveTex = t;
        // 差分キャプチャ (mode 2) で clean 側として SRV サンプリングするため SRV も作る。
        // 失敗しても従来方式 (mode 0/1) には影響しないので続行。
        _saveSrv = null;
        ID3D11ShaderResourceView* srv;
        if (_dev->CreateShaderResourceView((ID3D11Resource*)_saveTex, null, &srv) == 0 && srv != null)
            _saveSrv = srv;
        return true;
    }

    /// <summary>[試験] capture mode 2 用: UI 描画後の backbuffer を受けるコピー先 (SRV 付き)。</summary>
    private bool CreateCurTex()
    {
        var td = new D3D11_TEXTURE2D_DESC
        {
            Width = _w, Height = _h, MipLevels = 1, ArraySize = 1, Format = _fmt,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
            CPUAccessFlags = 0, MiscFlags = 0,
        };
        ID3D11Texture2D* t;
        if (_dev->CreateTexture2D(&td, null, &t) != 0 || t == null) { LastError = "curTex 生成失敗"; return false; }
        _curTex = t;
        ID3D11ShaderResourceView* srv;
        if (_dev->CreateShaderResourceView((ID3D11Resource*)_curTex, null, &srv) != 0 || srv == null)
        { LastError = "curSrv 生成失敗"; return false; }
        _curSrv = srv;
        return true;
    }

    private bool CreateRt()
    {
        // 中間 RT: ゲーム backbuffer と同一フォーマット + RT|SRV
        // (Dalamud の Kawase ブラーが RT を SRV サンプリングするため SRV 必須)。
        var td = new D3D11_TEXTURE2D_DESC
        {
            Width = _w, Height = _h, MipLevels = 1, ArraySize = 1, Format = _fmt,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET
                             | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE),
            CPUAccessFlags = 0, MiscFlags = 0,
        };
        ID3D11Texture2D* rt;
        if (_dev->CreateTexture2D(&td, null, &rt) != 0 || rt == null) { LastError = "中間 RT CreateTexture2D 失敗"; return false; }
        _rt = rt;
        ID3D11RenderTargetView* rtv;
        if (_dev->CreateRenderTargetView((ID3D11Resource*)_rt, null, &rtv) != 0 || rtv == null) { LastError = "中間 RTV 生成失敗"; return false; }
        _rtv = rtv;
        return true;
    }

    private bool CreateDcompWindow(int x, int y)
    {
        try
        {
            _hInst = GetModuleHandleW(null);
            if (!RegisterClassIfNeeded()) { LastError = "RegisterClass 失敗"; return false; }

            // 入力安全な窓: WS_EX_NOREDIRECTIONBITMAP (DComp 表示に必須) +
            // WS_EX_TRANSPARENT (クリックスルー) + TOPMOST + NOACTIVATE。
            // owner=FFXIV 窓 (同プロセス・アクティブ化追従)。
            uint ex = WS_EX_NOREDIRECTIONBITMAP | WS_EX_TRANSPARENT
                    | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            IntPtr owner = GetOwnFfxivHwnd();
            _hwnd = CreateWindowExW(
                ex, ClassName, "MaskedDalamud DComp Overlay", WS_POPUP,
                x, y, (int)_w, (int)_h, owner, IntPtr.Zero, _hInst, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) { LastError = $"CreateWindowExW 失敗 {Marshal.GetLastWin32Error()}"; return false; }

            if (!SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE))
                Plugin.Log.Warning($"[DComp] WDA 失敗 {Marshal.GetLastWin32Error()} (続行)");

            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            _shown = true;

            if (!CreateCompositionChain()) return false;
            Plugin.Log.Info($"[DComp] 窓+合成生成 OK {_w}x{_h} fmt={_fmt} ex=0x{ex:X8}");
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"DComp 窓例外: {ex.Message}";
            Plugin.Log.Error($"[DComp] CreateDcompWindow 例外: {ex}");
            return false;
        }
    }

    private bool CreateCompositionChain()
    {
        // 独立 DXGI factory (CreateDXGIFactory2)。ゲーム device 経由で取得した factory を
        // 使うと FFXIV swapchain のモード遷移 (ResizeBuffers/SetFullscreenState) と衝突する
        // ため、独立 factory で composition swapchain を作りゲーム側状態管理から切り離す。
        IDXGIFactory2* factory;
        fixed (Guid* g = &IID_IDXGIFactory2)
        {
            var hr0 = CreateFactory2Maybe(g, (void**)&factory);
            if (hr0 != 0 || factory == null)
            { LastError = $"CreateDXGIFactory2 失敗 0x{hr0:X8}"; return false; }
        }

        var desc = new DXGI_SWAP_CHAIN_DESC1
        {
            Width = _w, Height = _h,
            Format = _fmt,
            Stereo = false,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount = 3,                 // フリップキューのブロック緩和
            Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
            SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD,
            AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED,
            Flags = 0,
        };
        IDXGISwapChain1* sc;
        var hr = factory->CreateSwapChainForComposition((IUnknown*)_dev, &desc, null, &sc);
        factory->Release();
        if (hr.FAILED || sc == null) { LastError = $"CreateSwapChainForComposition 失敗 0x{hr.Value:X}"; return false; }
        _compSwap = sc;

        // DComp Device 取得には game device の DXGIDevice が必要 (取得直後 Release)。
        IDXGIDevice* dxgiDev;
        fixed (Guid* g = &IID_IDXGIDevice)
            if (((ID3D11Device*)_dev)->QueryInterface(g, (void**)&dxgiDev).FAILED || dxgiDev == null)
            { LastError = "IDXGIDevice QI 失敗"; return false; }

        IDCompositionDevice* dcomp;
        fixed (Guid* g = &IID_IDCompositionDevice)
            hr = DCompositionCreateDevice(dxgiDev, g, (void**)&dcomp);
        dxgiDev->Release();
        if (hr.FAILED || dcomp == null) { LastError = $"DCompositionCreateDevice 失敗 0x{hr.Value:X}"; return false; }
        _dcompDev = dcomp;

        IDCompositionTarget* target;
        if (_dcompDev->CreateTargetForHwnd((HWND)_hwnd, true, &target).FAILED || target == null)
        { LastError = "CreateTargetForHwnd 失敗"; return false; }
        _dcompTarget = target;

        IDCompositionVisual* visual;
        if (_dcompDev->CreateVisual(&visual).FAILED || visual == null)
        { LastError = "CreateVisual 失敗"; return false; }
        _dcompVisual = visual;

        _dcompVisual->SetContent((IUnknown*)_compSwap);
        _dcompTarget->SetRoot(_dcompVisual);
        _dcompDev->Commit();
        return true;
    }

    /// <summary>composition swapchain を完全再生成し Visual に再 SetContent する。
    /// モード遷移後など swapchain の挙動が壊れている可能性のあるタイミングで呼ぶ。</summary>
    private bool RebuildCompositionSwapchainOnly()
    {
        try
        {
            if (_dcompVisual != null) { _dcompVisual->SetContent(null); }
            if (_compSwap != null) { _compSwap->Release(); _compSwap = null; }

            IDXGIFactory2* factory;
            fixed (Guid* g = &IID_IDXGIFactory2)
            {
                var hr0 = CreateFactory2Maybe(g, (void**)&factory);
                if (hr0 != 0 || factory == null) { Plugin.Log.Warning($"[DComp] Rebuild: CreateDXGIFactory2 失敗 0x{hr0:X8}"); return false; }
            }
            var desc = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = _w, Height = _h, Format = _fmt,
                Stereo = false,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 3,
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD,
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED,
                Flags = 0,
            };
            IDXGISwapChain1* sc;
            var hr = factory->CreateSwapChainForComposition((IUnknown*)_dev, &desc, null, &sc);
            factory->Release();
            if (hr.FAILED || sc == null) { Plugin.Log.Warning($"[DComp] Rebuild: CreateSwapChainForComposition 失敗 0x{hr.Value:X}"); return false; }
            _compSwap = sc;

            if (_dcompVisual != null && _dcompDev != null)
            {
                _dcompVisual->SetContent((IUnknown*)_compSwap);
                _dcompDev->Commit();
            }
            Plugin.Log.Info($"[DComp] composition swapchain 再生成 OK {_w}x{_h}");
            return true;
        }
        catch (Exception ex) { Plugin.Log.Warning($"[DComp] Rebuild 例外: {ex.Message}"); return false; }
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
        if (_pendingRebuild)
        {
            _pendingRebuild = false;
            Plugin.Log.Info("[DComp] 前フレーム Present 異常を受けて swapchain 再生成");
            RebuildCompositionSwapchainOnly();
        }
        DXGI_SWAP_CHAIN_DESC d;
        if (sc->GetDesc(&d) != 0) return;
        bool fs = d.Windowed == 0;
        bool modeChanged = fs != _exclusiveFs;
        if (modeChanged)
        {
            _exclusiveFs = fs;
            Plugin.Log.Info($"[DComp] 排他フルスクリーン: {(fs ? "ON (overlay 自動 hide / scrub 継続)" : "OFF (overlay 復帰)")}");
        }
        uint w = d.BufferDesc.Width, h = d.BufferDesc.Height;
        var fmt = d.BufferDesc.Format;
        if (w < 16 || h < 16) return;
        bool sizeChanged = !(_saveTex != null && w == _svW && h == _svH && fmt == _svFmt);
        if (!sizeChanged && !modeChanged) return;

        _w = w; _h = h; _fmt = fmt;
        if (sizeChanged)
        {
            LogBackbufferRefs("resize前");
            _saveTex = null; _saveSrv = null;
            // [試験] mode 2 用テクスチャもサイズ変更で作り直す (次フレームに遅延生成)。
            // 既存方式と同じく Release せず参照を落とすだけ (use-after-free 回避方針)。
            _curTex = null; _curSrv = null;
            if (CreateSaveTex()) { _svW = w; _svH = h; _svFmt = fmt; }
            _rt = null; _rtv = null;
            if (!CreateRt()) { _svW = 0; return; }
            LogBackbufferRefs("resize後");
        }
        RebuildCompositionSwapchainOnly();
        _posX = _posY = int.MinValue;
        Plugin.Log.Info($"[DComp] テクスチャ/swapchain 再生成 {_w}x{_h} (modeChanged={modeChanged} sizeChanged={sizeChanged})");
    }

    private ID3D11Texture2D* GetBackbuffer(IDXGISwapChain* sc)
    {
        if (sc == null) return null;
        ID3D11Texture2D* bb; var iid = IID_ID3D11Texture2D;
        if (sc->GetBuffer(0, &iid, (void**)&bb) != 0 || bb == null) return null;
        return bb;
    }

    // ── 診断: ゲーム backbuffer の残存参照数を計測する ──
    // GetBuffer で +1 した参照を Release() すると「解放後の残り参照数」が返る。
    // = Dalamud の RTV / DXGI 内部 / 我々のリーク等、他に backbuffer を握っている数。
    // resizeBuffers が DXGI_ERROR_INVALID_CALL になるのは「backbuffer に未解放参照が
    // 残っている」とき。enable/disable/resize 跨ぎでこの数が増え続けるなら我々のリーク。
    private void LogBackbufferRefs(string tag)
    {
        if (!_plugin.cfg.debugEnabled) return;   // 診断ログはデバッグ有効時のみ
        try
        {
            var sc = LiveSwapChain();
            if (sc == null) { Plugin.Log.Info($"[DComp][diag] {tag}: swapchain=null"); return; }
            ID3D11Texture2D* bb; var iid = IID_ID3D11Texture2D;
            if (sc->GetBuffer(0, &iid, (void**)&bb) == 0 && bb != null)
            {
                uint refs = bb->Release();   // 我々の GetBuffer ぶんを返した後の残存参照数
                Plugin.Log.Info($"[DComp][diag] {tag}: backbuffer 残存参照={refs} {_w}x{_h}");
            }
            else Plugin.Log.Info($"[DComp][diag] {tag}: GetBuffer 失敗");
        }
        catch (Exception ex) { Plugin.Log.Info($"[DComp][diag] {tag}: 例外 {ex.Message}"); }
    }

    private void ArmBefore()
    {
        if (!_active) return;
        try { _runBefore!.Invoke(_internals.InterfaceManager, new object[] { _beforeCb! }); }
        catch (Exception ex) { Plugin.Log.Error($"[DComp] ArmBefore 失敗→停止: {ex.Message}"); _active = false; }
    }

    private void Before()
    {
        if (!_active) return;
        // ホットリロードで新ロードが所有権を取得していたら、自分は孤児。
        // 即座にスクラブを止める (= 配信側 backbuffer を汚し続けない)。
        if (!OverlayGeneration.IsCurrent(_genToken)) { SelfTerminateAsOrphan(); return; }
        try
        {
            var sc = LiveSwapChain();
            EnsureTextures(sc);
            // ① ゲーム backbuffer (UI を描く前の絵) を save。
            var bb = GetBackbuffer(sc);
            if (bb != null && _saveTex != null)
            {
                try
                {
                    _ctx->CopyResource((ID3D11Resource*)_saveTex, (ID3D11Resource*)bb);
                    // ② [試験] KamiToolKit ミラーを backbuffer へ厳密描画する。
                    //    save の直後に描くのが要点:
                    //      ・mode 1/2 が「変化した画素」として拾う          → 手元に出る
                    //      ・After() 末尾で backbuffer を save から戻す      → 配信には出ない
                    //      ・Dalamud の ImGui より前なので窓の下に潜る      → 前後関係が正しい
                    DrawKamiMirrorExact(bb);
                }
                finally { bb->Release(); }
            }
            _runAfter!.Invoke(_internals.InterfaceManager, new object[] { _afterCb! });
        }
        catch (Exception ex) { Plugin.Log.Error($"[DComp] Before 例外→停止: {ex.Message}"); _active = false; }
    }

    /// <summary>[試験] KamiToolKit ミラーの画像ノードを Atk の合成式どおりに backbuffer へ描く。
    ///
    /// ここで描いた画素は After() 末尾の「backbuffer を save から復元」で必ず消えるため、
    /// **配信側へ漏れない**。逆に言うと save/復元が成立しない状況では描いてはいけないので、
    /// 復元元 (_saveTex) が無い場合と、オーバーレイが backbuffer 由来でない capture mode 0
    /// では実行しない (mode 0 は従来どおり ImGui 近似で描く)。</summary>
    private void DrawKamiMirrorExact(ID3D11Texture2D* bb)
    {
        var km = _plugin.kamiMirror;
        if (km == null || !km.Active) { KamiStatus = "未使用"; return; }

        // mode 0 (DrawData リプレイ) はオーバーレイが ImGui の頂点データだけで作られるため、
        // backbuffer へ直接描いても手元には出ない。ImGui 近似フォールバックに任せる。
        if (!_plugin.cfg.kamiExactColor) { KamiStatus = "近似 (厳密モード OFF)"; return; }
        if (_plugin.cfg.dcompCaptureMode == 0) { KamiStatus = "近似 (キャプチャ方式が mode 0)"; return; }
        if (bb == null || _saveTex == null || _dev == null || _ctx == null)
        { KamiStatus = "近似 (復元元なし)"; return; }

        try
        {
            if (!_quads.Ensure(_dev))
            {
                // シェーダを作れない環境。以後は恒久的に近似へ倒す
                // (ExactMirrorReady が false になるので ImGui 側が描いてくれる)。
                if (!_quadWarnOnce)
                {
                    _quadWarnOnce = true;
                    Plugin.Log.Warning($"[KamiMirror] 厳密モード準備不可 → 近似へフォールバック: {_quads.LastError}");
                }
                KamiStatus = $"近似 ({_quads.LastError})";
                return;
            }

            int n = km.SnapshotQuads(_quadList, _plugin.cfg.kamiAdditiveOnAdd);
            if (n == 0) { KamiStatus = "厳密モード (対象なし)"; return; }

            ID3D11RenderTargetView* rtv;
            if (_dev->CreateRenderTargetView((ID3D11Resource*)bb, null, &rtv) != 0 || rtv == null)
            {
                Plugin.Log.Warning("[KamiMirror] backbuffer RTV を作れませんでした → このフレームは近似へ");
                KamiStatus = "近似 (backbuffer RTV 生成失敗)";
                return;
            }
            try { _quads.Execute(_ctx, rtv, _w, _h, _quadList); }
            finally { rtv->Release(); }

            KamiStatus = $"厳密モード OK クアッド={n}"
                       + (_plugin.cfg.kamiAdditiveOnAdd ? " (加算合成 ON)" : "");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[KamiMirror] 厳密モード例外 → このフレームは近似へ: {ex.Message}");
            KamiStatus = $"厳密モード例外: {ex.Message}";
        }
    }

    private void After()
    {
        if (!_active) return;
        try
        {
            var dd = ImGui.GetDrawData();

            // ── FPS 計測 ──
            double ms = _frameSw.Elapsed.TotalMilliseconds;
            _frameSw.Restart();
            if (ms > 0.1)
            {
                _frameMsEma = _frameMsEma * 0.9 + ms * 0.1;
                _fpsEma = _fpsEma * 0.9 + (1000.0 / ms) * 0.1;
            }
            _frameCount++;
            bool dcompOk = _compSwap != null && _dcompDev != null && _dcompVisual != null;
            _lastDiag = new DiagSnapshot(_fpsEma, _frameCount, _frameMsEma, (int)_w, (int)_h, dcompOk);
            _frame++;

            // [試験] オーバーレイ表示の間引き。間引き中もスクラブ(③ + Before save)は毎フレーム継続。
            int interval = Math.Max(1, _plugin.cfg.dcompUpdateIntervalFrames);
            bool refreshFull = _frame % interval == 0;

            // ② 中間 RT に UI レイヤーを作る。方式は capture mode で切替 (試験機能)。
            //    0 = 従来: DrawData を RenderDrawDataInternal でリプレイ
            //    1 = 全画面ミラー: UI 描画後の backbuffer を丸ごとコピー
            //    2 = 差分キャプチャ: (UI 後 backbuffer) と (クリーン画) の差分のみ抽出
            //  1/2 は ImGui の頂点データに依存しないため、ImDrawList.AddCallback による
            //  ネイティブ D3D11 直描き (BossModReborn の新 DX11 レーダー等) も取りこぼさない。
            int mode = _plugin.cfg.dcompCaptureMode;
            bool didReplay = false;
            var sc = LiveSwapChain();
            var bb = GetBackbuffer(sc);
            try
            {
                bool wantOverlay = refreshFull && (!_exclusiveFs || _tryShowInFullscreen);

                if (mode != 0 && wantOverlay && bb != null && _rt != null && _rtv != null)
                {
                    // ── mode 1 (全画面ミラー) / mode 2 (差分) 共通経路 ──
                    // どちらも「UI 描画後の backbuffer」を SRV 付きテクスチャへ退避してから
                    // フルスクリーン シェーダで中間 RT を作る。ゲーム backbuffer のアルファは
                    // 不定なため、ミラーでもシェーダで a=1 を強制する (CopyResource 直コピー不可)。
                    try
                    {
                        bool mirror = mode == 1;
                        if (_curTex == null || _curSrv == null) CreateCurTex();
                        bool ready = _curTex != null && _curSrv != null && _diff.Ensure(_dev)
                                  && (mirror || _saveSrv != null);
                        if (ready)
                        {
                            _ctx->CopyResource((ID3D11Resource*)_curTex, (ID3D11Resource*)bb);
                            _diff.Execute(_ctx, _rtv, _curSrv, _saveSrv, _w, _h, mirror);
                            didReplay = true;
                        }
                        else if (!_diffWarnOnce)
                        {
                            _diffWarnOnce = true;
                            Plugin.Log.Warning($"[DComp] キャプチャ mode {mode} 準備不可 → 従来方式へフォールバック: "
                                             + $"{_diff.LastError ?? LastError ?? "リソース未生成"}");
                        }
                    }
                    catch (Exception ex) { Plugin.Log.Warning($"[DComp] キャプチャ mode {mode} 例外: {ex.Message}"); }
                }

                // ── フォールバック: mode 1/2 が使えなかったフレームは従来のリプレイで描く ──
                // シェーダ生成失敗・リソース未生成などで didReplay=false のまま抜けると
                // オーバーレイが更新されず「UI が丸ごと消える」ため、必ず従来方式へ落とす。
                if (!didReplay && wantOverlay && _rt != null && _rtv != null)
                {
                    try
                    {
                        if (!dd.IsNull && dd.Valid)
                        {
                            var args = new object[4];
                            args[0] = System.Reflection.Pointer.Box((void*)_rt, _rddiParams![0]);
                            args[1] = System.Reflection.Pointer.Box((void*)_rtv, _rddiParams![1]);
                            args[2] = dd;
                            args[3] = true;
                            _rddi!.Invoke(_renderer, args);
                            didReplay = true;
                        }
                    }
                    catch (Exception ex) { Plugin.Log.Warning($"[DComp] リプレイ例外: {ex.Message}"); }
                }

                // ③ backbuffer を save 状態に戻す (UI ピクセル消去・配信非表示維持)。
                //    mode 1/2 は上で backbuffer を読み終えてから復元する順序が必須。
                if (bb != null && _saveTex != null)
                {
                    try { _ctx->CopyResource((ID3D11Resource*)bb, (ID3D11Resource*)_saveTex); }
                    catch (Exception ex) { Plugin.Log.Warning($"[DComp] 復元例外: {ex.Message}"); }
                }
            }
            finally { if (bb != null) bb->Release(); }

            // ④ 中間 RT を composition swapchain にコピーして Present (vsync 同期)。
            if (didReplay && _shown && (!_exclusiveFs || _tryShowInFullscreen) && _compSwap != null)
            {
                try
                {
                    ID3D11Texture2D* cbb; var iid = IID_ID3D11Texture2D;
                    if (_compSwap->GetBuffer(0, &iid, (void**)&cbb).SUCCEEDED && cbb != null)
                    {
                        try { _ctx->CopyResource((ID3D11Resource*)cbb, (ID3D11Resource*)_rt); }
                        finally { cbb->Release(); }
                        // [試験] sync interval は設定可能。1=vsync同期(既定・安定だがFPS低下要因)、
                        // 0=非ブロッキング(軽い)。いずれも DO_NOT_WAIT フラグは付けない
                        // (GShade で composition が映らない真因は DO_NOT_WAIT フラグであり sync interval ではない)。
                        // AMD最適化モード: 合成Presentを非ブロッキング(0)に強制し vsync 待ちを外す。
                        // (DO_NOT_WAIT フラグは付けない → GShade 互換維持)
                        uint syncInterval = _plugin.cfg.dcompAmdMode
                            ? 0u
                            : (uint)Math.Clamp(_plugin.cfg.dcompSyncInterval, 0, 4);
                        var phr = _compSwap->Present(syncInterval, 0);
                        uint code = (uint)phr.Value;
                        // DXGI_STATUS_OCCLUDED (0x087A0001) は想定内 (隠れている)。
                        if (phr.FAILED && code != 0x087A0001)
                        {
                            Plugin.Log.Warning($"[DComp] Present 異常 HRESULT 0x{code:X8} → 次フレームで swapchain 再生成予約");
                            _pendingRebuild = true;
                        }
                    }
                }
                catch (Exception ex) { Plugin.Log.Warning($"[DComp] Present 例外: {ex.Message}"); _pendingRebuild = true; }
            }

            if (++_diag % 180 == 1)
            {
                Plugin.Log.Info($"[DComp] diag fps={_fpsEma:F1} frame={_frameCount} "
                    + $"shown={_shown} dcompOk={dcompOk} {_w}x{_h}");
            }

            // ⑤ 位置/前面追従。
            if (GetClientRect(out int gx, out int gy, out uint gw, out uint gh))
            {
                bool fg = GetForegroundWindow() == GetOwnFfxivHwnd();
                SetShown(fg && (!_exclusiveFs || _tryShowInFullscreen), gw, gh);
                SyncPosition(gx, gy);
            }
        }
        catch (Exception ex) { Plugin.Log.Error($"[DComp] After 例外→停止: {ex.Message}"); _active = false; }
        if (_active) ArmBefore();
    }

    private System.Threading.Timer? _posTimer;
    private void StartPosTimer()
    {
        try { _posTimer?.Dispose(); _posTimer = new System.Threading.Timer(_ => PosTick(), null, 8, 8); }
        catch (Exception ex) { Plugin.Log.Warning($"[DComp] posTimer 失敗: {ex.Message}"); }
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
        try { SetShown(GetForegroundWindow() == GetOwnFfxivHwnd() && (!_exclusiveFs || _tryShowInFullscreen), _w, _h); }
        catch { }
    }

    public void Disable()
    {
        _active = false;
        Teardown();
        Plugin.Log.Info("[DComp] 停止");
    }

    // ホットリロードで新ロードに所有権を奪われた孤児の自己停止。
    // Before() (描画スレッド) から呼ばれる。スクラブ停止 + 窓を隠す/破棄するだけで、
    // D3D context/リソースには一切触れない (teardown D3D は AV クラッシュ源・実証済)。
    // 残った GPU リソースは bounded leak として容認 (デバイス破棄で回収)。
    private void SelfTerminateAsOrphan()
    {
        Plugin.Log.Info("[DComp] 新ロード検出 → 孤児として自己停止 (scrub 停止・窓破棄)");
        _active = false;
        try { _posTimer?.Dispose(); _posTimer = null; } catch { }
        try { if (_hwnd != IntPtr.Zero) { ShowWindow(_hwnd, SW_HIDE); DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; } } catch { }
        _shown = false;
    }

    private void Teardown()
    {
        _active = false;
        try { _posTimer?.Dispose(); _posTimer = null; } catch { }

        // DComp / Composition Swapchain を順序立てて Release (HWND 占有を確実に解放)。
        try
        {
            if (_dcompVisual != null)
            {
                try { _dcompVisual->SetContent(null); } catch { }
                if (_dcompTarget != null) { try { _dcompTarget->SetRoot(null); } catch { } }
                if (_dcompDev != null)    { try { _dcompDev->Commit(); } catch { } }
                try { _dcompVisual->Release(); } catch { }
                _dcompVisual = null;
            }
            if (_dcompTarget != null) { try { _dcompTarget->Release(); } catch { } _dcompTarget = null; }
            if (_compSwap != null)    { try { _compSwap->Release(); } catch { } _compSwap = null; }
            if (_dcompDev != null)    { try { _dcompDev->Release(); } catch { } _dcompDev = null; }
        }
        catch (Exception ex) { Plugin.Log.Warning($"[DComp] COM Release 例外: {ex.Message}"); }

        OnGameThread(DestroyWindowOnly);
        // 中間テクスチャは use-after-free 回避のため保持 (Dalamud レンダパイプラインから
        // 参照される可能性があるため・他方式と同方針)。
        // ※ teardown では D3D context を一切触らない (Dispose 中の Flush/OMSet は
        //   Framework 停止 (WaitBeforeDispose) と競合し AV クラッシュするため・実証済 0.1.5.8)。
        _saveTex = null; _saveSrv = null; _rt = null; _rtv = null;
        // [試験] 差分キャプチャのシェーダ類は自前生成なのでここで確実に解放する
        // (テクスチャ類は既存方針どおり参照を落とすのみ = use-after-free 回避)。
        try { _diff.Dispose(); } catch { }
        try { _quads.Dispose(); } catch { }
        _curTex = null; _curSrv = null;
        _dev = null; _ctx = null;
    }

    private void DestroyWindowOnly()
    {
        try { if (_hwnd != IntPtr.Zero) { ShowWindow(_hwnd, SW_HIDE); DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; } } catch { }
        _shown = false;
    }

    public void Dispose() { _active = false; Teardown(); }

    private bool RegisterClassIfNeeded()
    {
        if (_classReg) return true;
        _wndProc = StaticWndProc;
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

    // 入力透過: WS_EX_TRANSPARENT + WM_NCHITTEST=HTTRANSPARENT + WM_MOUSEACTIVATE=NOACTIVATE。
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private static readonly IntPtr HTTRANSPARENT = new(-1);
    private static IntPtr StaticWndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == WM_NCHITTEST) return HTTRANSPARENT;
        if (m == WM_MOUSEACTIVATE) return (IntPtr)MA_NOACTIVATE;
        return DefWindowProcW(h, m, w, l);
    }

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
    private const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint DXGI_USAGE_RENDER_TARGET_OUTPUT = 0x00000020;

    private delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
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
    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory2(uint flags, Guid* riid, void** factory);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string name);

    /// <summary>迂回が成立するか (本物 dxgi を掴めて proxy と別関数か) を返す。
    /// スワップチェインは作らず関数ポインタ解決のみ行う軽量プローブ。オートセレクトが
    /// 「ReShade 環境で DComp を使うか GPU-GDI に逃がすか」を決めるために使う。</summary>
    public bool ProbeBypass()
    {
        EnsureRealFactoryResolved();
        return _realCreateFactory2 != null;
    }

    /// <summary>設定で ReShade 迂回が有効かつ本物 factory を解決済みならそちらで、
    /// でなければ通常の P/Invoke (ReShade プロキシ経由) で CreateDXGIFactory2 を呼ぶ。</summary>
    private int CreateFactory2Maybe(Guid* riid, void** factory)
    {
        if (_plugin.cfg.dcompReshadeBypass)
        {
            EnsureRealFactoryResolved();
            if (_realCreateFactory2 != null)
                return _realCreateFactory2(0, riid, factory);
        }
        return CreateDXGIFactory2(0, riid, factory);
    }

    /// <summary>本物の System32\dxgi.dll (ReShade のプロキシとは別モジュールとして既にロード済み) の
    /// ベースアドレスから CreateDXGIFactory2 を GetProcAddress で解決する。診断のためプロキシ側の
    /// アドレスと比較し、別物か (＝迂回が成立するか) を BypassStatus に記録する。</summary>
    private void EnsureRealFactoryResolved()
    {
        if (_bypassResolved) return;
        _bypassResolved = true;
        try
        {
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.System); // ...\System32
            var realPath = System.IO.Path.Combine(sys, "dxgi.dll").ToLowerInvariant();
            IntPtr realBase = IntPtr.Zero;
            foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
            {
                try
                {
                    if ((m.FileName ?? "").ToLowerInvariant() == realPath) { realBase = m.BaseAddress; break; }
                }
                catch { }
            }
            if (realBase == IntPtr.Zero) { BypassStatus = "System32\\dxgi.dll 未ロード"; return; }

            var realProc = GetProcAddress(realBase, "CreateDXGIFactory2");
            if (realProc == IntPtr.Zero) { BypassStatus = "本物 CreateDXGIFactory2 未解決"; return; }

            // 診断: プロキシ (現在ロード名 "dxgi.dll") 側のアドレスと比較。
            var proxyBase = GetModuleHandleW("dxgi.dll");
            var proxyProc = (proxyBase != IntPtr.Zero) ? GetProcAddress(proxyBase, "CreateDXGIFactory2") : IntPtr.Zero;

            // real==proxy の場合は迂回が成立していない (ReShade がプロキシ形式でない等) ため
            // 迂回ポインタは設定しない。BypassAvailable=false となり、上位で GPU-GDI へ逃がす。
            if (realProc != proxyProc)
            {
                _realCreateFactory2 = (delegate* unmanaged<uint, Guid*, void**, int>)realProc;
                BypassStatus = $"迂回有効 real=0x{realProc.ToInt64():X} proxy=0x{proxyProc.ToInt64():X}";
            }
            else
            {
                BypassStatus = $"迂回不成立 (real==proxy) 0x{realProc.ToInt64():X}";
            }
            Plugin.Log.Info($"[DComp] ReShade 迂回 factory 解決: {BypassStatus}");
        }
        catch (Exception ex) { BypassStatus = $"解決例外: {ex.Message}"; }
    }
    [DllImport("dcomp.dll")]
    private static extern HRESULT DCompositionCreateDevice(IDXGIDevice* dxgiDevice, Guid* iid, void** dcompositionDevice);
}
