using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace MaskedDalamud;

/// <summary>
/// Method A 作り直し版「最終提示スクラブ ＋ 独立 UI リプレイ」。
///
/// 旧 Method A は Dalamud の Dx11Renderer.mainViewport の RT/swapChain を
/// reflection でフィールド差し替えして状態を破壊し、device-removed 等で
/// ゲームごとクラッシュした。本方式は Dalamud のレンダラ状態に一切触れない:
///
///  1. RunBeforeImGuiRender: ゲーム backbuffer (UI 合成前=クリーン) を
///     自前 saveTex へ純 CopyResource で退避。
///  2. Dalamud は通常どおり backbuffer へ ImGui を描画 (無改変)。
///  3. RunAfterImGuiRender (UI 描画後・フリップ前):
///     a. Dalamud の RenderDrawDataInternal を **純粋な関数として** 呼び、
///        同じ DrawData を我々の透明オーバーレイ RT へ描く (RT は引数で渡す
///        だけ。mainViewport のフィールドも swapChain も ResetBuffers も
///        一切触らない = 状態破壊なし)。
///     b. saveTex を backbuffer へ書き戻し UI をピクセル消去 (スクラブ)。
///     c. オーバーレイ (WDA 除外・透明・クリックスルー) を更新 → 手元にだけ UI。
///
/// → 配信 (ゲームキャプチャ/Discord) はクリーンなゲーム、手元は今までどおり。
///   入力/ホバー/ImGui 状態は完全に元のまま (最終ピクセルのみ操作)。
/// </summary>
internal sealed unsafe class ScrubOverlay : IDisposable
{
    private readonly Plugin _plugin;
    private readonly DalamudImGuiInternals _internals = new();
    private MethodAOverlay? _overlay;        // 表示面 (WDA除外/透明/クリックスルー) を再利用

    private volatile bool _active;


    private ID3D11Device* _dev;
    private ID3D11DeviceContext* _ctx;
    private IDXGISwapChain* _sc;
    private ID3D11Texture2D* _saveTex;       // クリーン backbuffer 退避先 (game device)
    private uint _w, _h;
    private DXGI_FORMAT _fmt;
    private uint _svW, _svH;            // 現 saveTex の寸法/形式 (変化検知用)
    private DXGI_FORMAT _svFmt;

    private MethodInfo? _runBefore, _runAfter, _renderDrawDataInternal;
    private Type[]? _rddiParamTypes;
    private object? _renderer;
    private Action? _beforeCb, _afterCb;
    private int _clientX, _clientY;
    private int _frame;
    // FPS 計測 (基本タブ「現在の状態」FPS 表示用)。
    private double _fpsEma = 60;
    private readonly System.Diagnostics.Stopwatch _frameSw = System.Diagnostics.Stopwatch.StartNew();

    public bool IsActive => _active;
    public string? LastError { get; private set; }
    public double LastFpsEma => _fpsEma;

    public ScrubOverlay(Plugin plugin) => _plugin = plugin;

    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private static void RunOnGameThread(Action a)
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
            _sc = (IDXGISwapChain*)_internals.GameSwapChainPtr;
            if (_dev == null || _ctx == null || _sc == null) { LastError = "device/context/swapchain が null"; return false; }

            DXGI_SWAP_CHAIN_DESC d;
            if (_sc->GetDesc(&d) != 0) { LastError = "swapchain GetDesc 失敗"; return false; }
            _w = d.BufferDesc.Width; _h = d.BufferDesc.Height; _fmt = d.BufferDesc.Format;
            if (_w < 16 || _h < 16) { LastError = $"swapchain サイズ異常 {_w}x{_h}"; return false; }

            if (!CreateSaveTex()) { LastError = "saveTex 生成失敗"; return false; }
            _svW = _w; _svH = _h; _svFmt = _fmt;

            // Dalamud の RenderDrawDataInternal を「純関数」として解決 (状態は触らない)
            _renderer = _internals.Renderer;
            var rt = _renderer?.GetType();
            _renderDrawDataInternal = rt?.GetMethod("RenderDrawDataInternal",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (_renderDrawDataInternal == null) { LastError = "RenderDrawDataInternal 未解決"; CleanupGpuRetain(); return false; }
            _rddiParamTypes = Array.ConvertAll(_renderDrawDataInternal.GetParameters(), p => p.ParameterType);
            if (_rddiParamTypes.Length != 4) { LastError = "RenderDrawDataInternal 引数数 異常"; CleanupGpuRetain(); return false; }

            var imType = _internals.InterfaceManager!.GetType();
            _runBefore = imType.GetMethod("RunBeforeImGuiRender",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Action) }, null);
            _runAfter = imType.GetMethod("RunAfterImGuiRender",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Action) }, null);
            if (_runBefore == null || _runAfter == null) { LastError = "RunBefore/AfterImGuiRender 未解決"; CleanupGpuRetain(); return false; }

            // 表示面 (WDA除外/透明/クリックスルー窓 + 同フォーマット RT) を生成
            if (!GetClientRect(out int gx, out int gy, out uint gw, out uint gh))
            { LastError = "FFXIV クライアント矩形取得失敗"; CleanupGpuRetain(); return false; }
            var ov = new MethodAOverlay();
            bool created = false;
            RunOnGameThread(() => created = ov.Create(_internals.GameDevicePtr, _internals.GameDeviceContextPtr,
                _fmt, GetOwnFfxivHwnd(), gx, gy, gw, gh));
            if (!created || !ov.IsReady || ov.RenderTargetViewPtr == IntPtr.Zero)
            { LastError = "オーバーレイ生成失敗"; RunOnGameThread(ov.DisposeWindowOnly); CleanupGpuRetain(); return false; }
            _overlay = ov;
            _clientX = gx; _clientY = gy;

            _beforeCb ??= Before;
            _afterCb ??= After;
            _active = true;
            ArmBefore();
            StartPosTimer();
            Plugin.Log.Info("[Scrub] 開始 (スクラブ＋独立リプレイ / Dalamud 無改変)");
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Enable 例外: {ex.GetType().Name}: {ex.Message}";
            Plugin.Log.Error($"[Scrub] Enable 例外: {ex}");
            try { TeardownSafe(); } catch { }
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
            BindFlags = 0, CPUAccessFlags = 0, MiscFlags = 0,
        };
        ID3D11Texture2D* t;
        if (_dev->CreateTexture2D(&td, null, &t) != 0 || t == null)
        { Plugin.Log.Error("[Scrub] saveTex CreateTexture2D 失敗"); return false; }
        _saveTex = t;
        return true;
    }

    /// <summary>その時点で「生きている」ゲーム swapchain を返す。表示モード変更で
    /// FFXIV が swapchain を作り直しても追従できるよう、キャッシュせず毎回取得。</summary>
    private IDXGISwapChain* LiveSwapChain()
    {
        var p = _internals.GetLiveGameSwapChain();
        if (p == IntPtr.Zero) p = _internals.GameSwapChainPtr;
        return (IDXGISwapChain*)p;
    }

    /// <summary>backbuffer のサイズ/フォーマットが変わっていたら saveTex を作り直す
    /// (表示モード/解像度変更で swapchain が再生成された場合の追従)。旧 saveTex は
    /// use-after-free 回避のため解放せず保持 (有界・稀)。</summary>
    private void EnsureSaveTex(IDXGISwapChain* sc)
    {
        if (sc == null) return;
        DXGI_SWAP_CHAIN_DESC d;
        if (sc->GetDesc(&d) != 0) return;
        uint w = d.BufferDesc.Width, h = d.BufferDesc.Height;
        var fmt = d.BufferDesc.Format;
        if (w < 16 || h < 16) return;
        if (_saveTex != null && w == _svW && h == _svH && fmt == _svFmt) return;
        _w = w; _h = h; _fmt = fmt;
        _saveTex = null;           // 旧 saveTex は意図的に保持(リーク)。device 破棄で回収
        if (CreateSaveTex()) { _svW = w; _svH = h; _svFmt = fmt; }
    }

    private ID3D11Texture2D* GetBackbuffer(IDXGISwapChain* sc)
    {
        if (sc == null) return null;
        ID3D11Texture2D* bb;
        var iid = IID_ID3D11Texture2D;
        if (sc->GetBuffer(0, &iid, (void**)&bb) != 0 || bb == null) return null;
        return bb;
    }

    private void ArmBefore()
    {
        if (!_active) return;
        try { _runBefore!.Invoke(_internals.InterfaceManager, new object[] { _beforeCb! }); }
        catch (Exception ex) { Plugin.Log.Error($"[Scrub] ArmBefore 失敗 → 停止: {ex.Message}"); _active = false; }
    }

    // UI 描画前: クリーン backbuffer を退避
    private void Before()
    {
        if (!_active) return;
        try
        {
            var sc = LiveSwapChain();
            EnsureSaveTex(sc);                 // 表示モード変更/解像度変更に追従
            var bb = GetBackbuffer(sc);
            if (bb != null && _saveTex != null)
            {
                try { _ctx->CopyResource((ID3D11Resource*)_saveTex, (ID3D11Resource*)bb); }
                finally { bb->Release(); }
            }
            _runAfter!.Invoke(_internals.InterfaceManager, new object[] { _afterCb! });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Scrub] Before 例外 → 停止: {ex.Message}");
            _active = false;
        }
    }

    // UI 描画後・フリップ前: ①UI を自前 RT に複製描画 ②backbuffer をクリーンへ戻す ③オーバーレイ更新
    private void After()
    {
        if (!_active || _overlay == null) return;
        try
        {
            var dd = ImGui.GetDrawData();

            // ── 負荷軽減 (P1/P5/X/Y) : 実効更新間隔を算出 ──
            // base + P5(軽量) を下限とする。X/Y 自動軽量化は 0.1.5.3 で削除済。
            int interval = Math.Max(1, _plugin.cfg.methodAUpdateIntervalFrames);
            if (_plugin.cfg.scrubLightweight) interval = Math.Max(interval, 4);

            // FPS 計測 (基本タブ「現在の状態」FPS 表示用)。
            double ms = _frameSw.Elapsed.TotalMilliseconds;
            _frameSw.Restart();
            if (ms > 0.1) _fpsEma = _fpsEma * 0.9 + (1000.0 / ms) * 0.1;

            _frame++;
            bool refreshFull = _frame % interval == 0;
            // P1: リプレイ間引き ON なら interval 周のみ全体描画。OFF なら毎フレーム全体描画。
            bool doFullReplay = _plugin.cfg.scrubThrottleReplay ? refreshFull : true;

            // ① Dalamud のレンダラを純関数呼び (フィールド非改変)。RT は引数で渡す。
            if (doFullReplay)
            {
                try
                {
                    if (!dd.IsNull && dd.Valid && _overlay.IsReady
                        && _overlay.RenderTargetPtr != IntPtr.Zero && _overlay.RenderTargetViewPtr != IntPtr.Zero)
                    {
                        var args = new object[4];
                        args[0] = System.Reflection.Pointer.Box((void*)_overlay.RenderTargetPtr, _rddiParamTypes![0]);
                        args[1] = System.Reflection.Pointer.Box((void*)_overlay.RenderTargetViewPtr, _rddiParamTypes![1]);
                        args[2] = dd;       // ImDrawDataPtr (struct) をそのまま
                        args[3] = true;     // clearRenderTarget = true → 透明クリアして描画
                        _renderDrawDataInternal!.Invoke(_renderer, args);
                    }
                }
                catch (Exception ex) { Plugin.Log.Warning($"[Scrub] リプレイ例外: {ex.Message}"); }
            }

            // ② backbuffer を UI 合成前へ戻す (最終提示から UI をピクセル消去)
            //    クリーン配信のため**毎フレーム必須** (間引き対象外)。
            var sc = LiveSwapChain();
            var bb = GetBackbuffer(sc);
            if (bb != null && _saveTex != null)
            {
                try { _ctx->CopyResource((ID3D11Resource*)bb, (ID3D11Resource*)_saveTex); }
                finally { bb->Release(); }
            }

            // ③ オーバーレイへ転送・前面/サイズ追従 (位置追従は軽量・毎フレーム)
            if (GetClientRect(out int gx, out int gy, out uint gw, out uint gh))
            {
                _clientX = gx; _clientY = gy;
                var fg = GetForegroundWindow();
                var ffxiv = GetOwnFfxivHwnd();
                _overlay.SetVisibleAndSize(ffxiv != IntPtr.Zero && fg == ffxiv, gw, gh);
                // 位置追従は内容更新スロットルと分離し毎フレーム即時反映
                // (ウィンドウ移動時のカクつき防止)。
                _overlay.SyncPosition(gx, gy);
            }
            // 読み戻し+CPU処理+UpdateLayeredWindow は interval 周回のみ。
            if (refreshFull)
            {
                _overlay.ParallelCopy = _plugin.cfg.scrubParallelCopy;
                // Z [試験]: UI 使用矩形を算出し読み戻し/CPU 処理を限定。
                int rx = -1, ry = -1, rw = -1, rh = -1;
                if (_plugin.cfg.scrubRegionLimit && !dd.IsNull)
                    ComputeUiBBox(dd, out rx, out ry, out rw, out rh);
                _overlay.UpdateLayered(_clientX, _clientY, rx, ry, rw, rh);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Scrub] After 例外 → 停止: {ex.Message}");
            _active = false;
        }
        if (_active) ArmBefore();
    }

    // ── 位置追従専用タイマー (Present/Framework 周期から独立) ──
    // ウィンドウドラッグ中はモーダル移動ループで Present が不規則化し、
    // Present 駆動の After での位置同期がカクつく。独立した高頻度タイマーで
    // オーバーレイ位置のみ更新する (内容転送は従来どおり After/スロットル)。
    private System.Threading.Timer? _posTimer;

    private void StartPosTimer()
    {
        try
        {
            _posTimer?.Dispose();
            _posTimer = new System.Threading.Timer(_ => PosTick(), null, 8, 8); // ≈120Hz
        }
        catch (Exception ex) { Plugin.Log.Warning($"[Scrub] posTimer 起動失敗: {ex.Message}"); }
    }

    private void PosTick()
    {
        if (!_active) return;
        var ov = _overlay;
        if (ov == null || !ov.IsReady) return;
        try
        {
            var fg = GetForegroundWindow();
            var ffxiv = GetOwnFfxivHwnd();
            if (ffxiv == IntPtr.Zero || fg != ffxiv) return; // 前面時のみ
            if (GetClientRect(out int gx, out int gy, out _, out _))
                ov.SyncPosition(gx, gy);   // 軽量 SetWindowPos のみ (内容は触らない)
        }
        catch { /* 位置追従の失敗は致命的でない */ }
    }

    public void Disable()
    {
        _active = false;
        TeardownSafe();
        Plugin.Log.Info("[Scrub] 停止 (通常表示へ)");
    }

    /// <summary>安全停止。Dalamud の状態は元々触っていないので戻すものは無い。
    /// 自前 GPU リソースは use-after-free 回避のため解放せず保持 (有界リーク)。
    /// ウィンドウのみ破棄。</summary>
    private void TeardownSafe()
    {
        _active = false;
        try { _posTimer?.Dispose(); _posTimer = null; } catch { }
        var ov = _overlay; _overlay = null;
        if (ov != null)
        {
            try
            {
                if (Plugin.Framework.IsInFrameworkUpdateThread) ov.DisposeWindowOnly();
                else ov.RequestCloseNonBlocking();
            }
            catch { }
        }
        // _saveTex は意図的に解放しない (残コールバックの CopyResource 先が
        // 解放済みになる use-after-free を構造的に防ぐ。device 破棄で回収)。
        _saveTex = null;
        _dev = null; _ctx = null; _sc = null;
    }

    private void CleanupGpuRetain()
    {
        // Enable 失敗時: まだリダイレクト/コールバック未武装 = 安全に解放可
        try { if (_saveTex != null) { _saveTex->Release(); _saveTex = null; } } catch { }
    }

    public void Dispose() { _active = false; TeardownSafe(); }

    public void FrameworkTick()
    {
        if (!_active) return;
        try
        {
            var ov = _overlay;
            if (ov == null || !ov.IsReady) return;
            var fg = GetForegroundWindow();
            var ffxiv = GetOwnFfxivHwnd();
            ov.SetShown(ffxiv != IntPtr.Zero && fg == ffxiv);
        }
        catch { }
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
        _cachedHwnd = found;
        return found;
    }

    /// <summary>Z [試験]: ImDrawData の各描画コマンド ClipRect を合算して
    /// UI が実際に使っている矩形 (オーバーレイ画素座標) を求める。
    /// rw/rh = 0 は「UI 無し(全消去)」、rx&lt;0 は算出失敗(=full-frame)。</summary>
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
        catch { rx = ry = rw = rh = -1; }   // 異常時は full-frame に委ねる
    }

    private bool GetClientRect(out int x, out int y, out uint w, out uint h)
    {
        x = y = 0; w = h = 0;
        var hwnd = GetOwnFfxivHwnd();
        if (hwnd == IntPtr.Zero) return false;
        if (!GetClientRect(hwnd, out RECT rc)) return false;
        int cw = rc.right - rc.left, ch = rc.bottom - rc.top;
        if (cw < 16 || ch < 16) return false;
        var tl = new POINT { X = rc.left, Y = rc.top };
        if (!ClientToScreen(hwnd, ref tl)) return false;
        x = tl.X; y = tl.Y; w = (uint)cw; h = (uint)ch;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsProc f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
}
