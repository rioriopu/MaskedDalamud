using System;
using System.Reflection;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;

namespace MaskedDalamud;

/// <summary>
/// Method A Stage 2 (Layered 方式) のオーケストレータ。
///
/// reflection で Dalamud 15.0.0.7 の <c>Dx11Renderer.mainViewport</c> の
/// <c>renderTarget</c> / <c>renderTargetView</c> を自前のオフスクリーン RT に
/// 差し替え、<c>swapChain</c> を空にする。これにより:
///   - Dalamud は ImGui を自前 RT に描く (ゲーム swapchain には描かれない =
///     OBS ゲームキャプチャに映らない)
///   - <c>swapChain==null</c> なので Dalamud が毎フレーム RT を透明クリアしてくれる
/// 描かれた RT は <see cref="MethodAOverlay"/> が CPU コピー → UpdateLayeredWindow
/// で配信者に表示する。Layered+Transparent 窓は OS のヒットテスト/WindowFromPoint
/// から完全に除外されるため、マウス/UI/ゲーム入力すべてが正常動作する。
///
/// 安全原則:
///   - reflection 解決が 1 段でも失敗したら何もしない (通常描画維持)。
///   - オーバーレイ完全検証まで差替しない。
///   - 差替前後はすべて Dalamud レンダースレッド (RunBefore/AfterImGuiRender)。
///   - 無効化/Dispose で必ず元へ復元してから後始末。
///
/// 参照: memory/reference_dalamud_imgui_pipeline.md
/// </summary>
internal sealed unsafe class MethodARedirect : IDisposable
{
    private readonly Plugin _plugin;
    private readonly DalamudImGuiInternals _internals = new();
    private MethodAOverlay? _overlay;

    private volatile bool _active;
    // Dalamud の mainViewport を実際に差し替えているか。teardown はこれが
    // true の時のみ復元を行う (ON→OFF→プラグイン無効化 等で teardown が
    // 二重に走り、既に正常な Dalamud 状態を破壊して device-removed する
    // のを防ぐ＝厳密冪等化)。
    private bool _swapped;

    // UiBuilder.Draw 駆動。Dispose で -= すれば確定的に切り離せ、無効化後に
    // Dalamud から呼ばれ続けない (キュー型 RunBefore/AfterImGuiRender の
    // 自己再武装デリゲートが無効化後に呼ばれ Framework クラッシュする問題の根絶)。
    private Action? _uiDrawCb;
    private bool _hooked;

    // 復元用に保存した元の ComPtr (boxed コピー)
    private object? _origSwapChain;
    private object? _origRt;
    private object? _origRtv;

    // AfterRender で使うクライアント画面座標 (BeforeRender でキャッシュ)
    private int _clientX, _clientY;

    // 負荷最適化用 (更新間隔)
    private long _frameCount;

    private Configuration Cfg => _plugin.cfg;

    public bool IsActive => _active;
    public string? LastError { get; private set; }

    public MethodARedirect(Plugin plugin) => _plugin = plugin;

    private IntPtr _cachedOwnHwnd;
    private EnumWindowsProc? _enumProc; // GC 防止

    /// <summary>
    /// 「このプラグインが動いている FF14 インスタンス自身」のウィンドウ HWND。
    ///
    /// Plugin.GetFfxivHwnd() はシステム全体の FFXIVGAME 窓の先頭を返すため、
    /// FF14 を 2 つ起動していると別インスタンスの窓を掴んでしまう。
    /// 一方 Process.MainWindowHandle はヒューリスティックで不安定 (毎フレーム
    /// 呼ぶと時々 0 や別窓を返し、オーバーレイが表示↔非表示を繰り返して
    /// ちらつく)。
    ///
    /// そこで「class == FFXIVGAME かつ PID == 自プロセス」の窓を確定的に
    /// 特定し、キャッシュする (毎フレーム列挙しない。窓が無効化されたら再取得 ―
    /// フルスクリーン切替で HWND が作り直される場合に対応)。
    /// </summary>
    private IntPtr GetOwnFfxivHwnd()
    {
        if (_cachedOwnHwnd != IntPtr.Zero && IsWindow(_cachedOwnHwnd))
            return _cachedOwnHwnd;

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
                    if (pid == myPid) { found = h; return false; } // 自プロセス窓発見
                }
            }
            catch { }
            return true;
        };
        try { EnumWindows(_enumProc, IntPtr.Zero); } catch { }
        _cachedOwnHwnd = found;
        return found;
    }

    /// <summary>Action を FFXIV メイン (Framework) スレッドで同期実行。
    /// HWND 生成/破棄は生成スレッド制約があるため必ずこれ経由。</summary>
    private static void RunOnGameThread(Action a)
    {
        if (Plugin.Framework.IsInFrameworkUpdateThread) { a(); return; }
        Plugin.Framework.RunOnFrameworkThread(a).GetAwaiter().GetResult();
    }

    public bool Enable()
    {
        if (_active) return true;
        LastError = null;
        try
        {
            _internals.Resolve();
            if (!_internals.Available)
            {
                LastError = $"reflection 解決失敗: {_internals.FailReason}";
                return false;
            }

            // ゲーム swapchain の format / クライアント矩形
            var gsc = (IDXGISwapChain*)_internals.GameSwapChainPtr;
            DXGI_SWAP_CHAIN_DESC d;
            if (gsc->GetDesc(&d) != 0) { LastError = "ゲーム swapchain GetDesc 失敗"; return false; }
            var fmt = d.BufferDesc.Format;

            if (!GetGameClientScreenRect(out int gx, out int gy, out uint gw, out uint gh))
            {
                LastError = "FFXIV クライアント矩形取得失敗";
                return false;
            }

            // オーバーレイ生成 (FFXIV メインスレッド)
            var ov = new MethodAOverlay();
            bool created = false;
            RunOnGameThread(() =>
            {
                created = ov.Create(_internals.GameDevicePtr, _internals.GameDeviceContextPtr,
                                    fmt, GetOwnFfxivHwnd(), gx, gy, gw, gh);
            });
            if (!created || !ov.IsReady
                || ov.RenderTargetPtr == IntPtr.Zero || ov.RenderTargetViewPtr == IntPtr.Zero)
            {
                LastError = "オーバーレイ生成失敗";
                RunOnGameThread(ov.Dispose);
                return false;
            }
            _overlay = ov;

            // 元の ComPtr を退避 (boxed コピーが元の ptr_ を保持)
            var mv = _internals.MainViewport!;
            _origSwapChain = _internals.MainViewportSwapChainField!.GetValue(mv);
            _origRt = _internals.MainViewportRenderTargetField!.GetValue(mv);
            _origRtv = _internals.MainViewportRenderTargetViewField!.GetValue(mv);

            // swapChain を空に、RT/RTV を自前へ差替
            if (!SetComPtr(_internals.MainViewportSwapChainField!, mv, IntPtr.Zero)
                || !SetComPtr(_internals.MainViewportRenderTargetField!, mv, _overlay.RenderTargetPtr)
                || !SetComPtr(_internals.MainViewportRenderTargetViewField!, mv, _overlay.RenderTargetViewPtr))
            {
                LastError = "ComPtr 差替失敗";
                _swapped = true; // 一部差替済みの可能性 → 復元させる
                RestoreAndTeardownSync();
                return false;
            }
            // ここで初めて「Dalamud の mainViewport を実際に差し替えた」状態。
            // teardown の二重適用 (OFF 後にプラグイン無効化等) で正常状態を
            // 壊さないよう、このフラグが true の時だけ復元処理を行う。
            _swapped = true;
            // ResetBuffers は呼ばない (RT/RTV 非空のため EnsureRenderTarget は
            // early-return し、我々の RT を使い続ける)。

            // フレームループは Dalamud の RunBefore/AfterImGuiRender キュー
            // (自己再武装＝無効化後も Dalamud が呼び続けクラッシュ) をやめ、
            // UiBuilder.Draw イベント駆動にする。Dispose で -= すれば確実に
            // 切り離せ、無効化後にコールバックが呼ばれない (= ON 状態で
            // プラグイン無効化時の Framework クラッシュを構造的に根絶)。
            _uiDrawCb ??= OnUiDraw;
            Plugin.PluginInterface.UiBuilder.Draw += _uiDrawCb;
            _hooked = true;
            _active = true;

            Plugin.Log.Info("[Method A] リダイレクト開始 (Layered 方式 / UiBuilder.Draw 駆動)");
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Enable 例外: {ex.GetType().Name}: {ex.Message}";
            Plugin.Log.Error($"[Method A] Enable 例外: {ex}");
            try { RestoreAndTeardownSync(); } catch { }
            return false;
        }
    }

    /// <summary>UiBuilder.Draw を確実に切り離す (冪等)。これにより無効化後に
    /// 我々のコールバックが Dalamud から二度と呼ばれない。</summary>
    private void UnhookDraw()
    {
        if (!_hooked) return;
        _hooked = false;
        try
        {
            if (_uiDrawCb != null)
                Plugin.PluginInterface.UiBuilder.Draw -= _uiDrawCb;
        }
        catch (Exception ex) { Plugin.Log.Warning($"[Method A] UnhookDraw 例外: {ex.Message}"); }
    }

    public void Disable()
    {
        // まずフレーム駆動を確実に停止 (-= は同期・確定的＝以後コールバック
        // は呼ばれない)。その後に復元。ON 状態でのプラグイン無効化でも、
        // Dalamud のキューに我々のデリゲートが残り続けて後から呼ばれる
        // (= Framework クラッシュ) ことがなくなる。
        _active = false;
        UnhookDraw();
        RestoreAndTeardownSync();
    }

    /// <summary>オーバーレイが「Dalamud のレンダラを安全に向けられる状態」か。
    /// ここが false の間に RT/RTV を Dalamud へ差すと RenderDrawDataInternal が
    /// 例外を投げ、ゲームごとクラッシュする (0.1.1.0 の事故)。</summary>
    private bool OverlayUsable()
        => _overlay != null && _overlay.IsReady
           && _overlay.RenderTargetPtr != IntPtr.Zero
           && _overlay.RenderTargetViewPtr != IntPtr.Zero;

    /// <summary>不正状態を検知したら、その場で Dalamud を元の swapchain へ復元し
    /// 安全に停止する (Dalamud の描画は通常どおりゲーム swapchain へ戻る)。</summary>
    private void AbortToSafe(string why)
    {
        Plugin.Log.Error($"[Method A] {why} → 安全復元 (通常描画へ)");
        _active = false;
        try { RestoreAndTeardownSync(); } catch (Exception ex) { Plugin.Log.Error($"[Method A] 復元例外: {ex}"); }
    }

    /// <summary>Framework.Update から毎ティック呼ぶ。FF14 が裏でも回り続けるため、
    /// Present 経路が止まってもオーバーレイの表示/非表示を確実に切替えられる
    /// (裏画面に UI が出っぱなしになる問題の対策)。</summary>
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
        catch { /* 表示制御の失敗は致命的でない */ }
    }

    /// <summary>
    /// UiBuilder.Draw で毎フレーム呼ばれる (Dalamud の ImGui build 内、
    /// ImGui.Render()/RenderDrawData の前)。Dispose で -= するため無効化後は
    /// 二度と呼ばれない (= ON 状態での無効化クラッシュを根絶)。
    ///
    /// 1 ハンドラに集約:
    ///  (a) 前フレームに我々の RT へ描かれた ImGui をオーバーレイへ転送/表示
    ///  (b) サイズ/前面追従
    ///  (c) この後の RenderDrawData が我々の RT へ描くよう RT/RTV/swapChain 差替
    /// (a) は 1 フレーム遅延だがオーバーレイ用途では実用上問題ない。
    /// </summary>
    private void OnUiDraw()
    {
        if (!_active) return;
        if (!OverlayUsable()) { AbortToSafe("オーバーレイ無効 (RT/RTV 不正)"); return; }

        // GPU デバイス除去の早期検知 + 実 HRESULT 記録 → 安全停止
        try
        {
            int drr = _overlay!.GetDeviceRemovedReason();
            if (drr != 0)
            {
                Plugin.Log.Error(
                    $"[Method A] GPU デバイス除去を検知 reason=0x{drr:X8} " +
                    "(0x887A0005=REMOVED 0x887A0006=HUNG 0x887A0020=DRIVER_INTERNAL " +
                    "0x887A0001=INVALID_CALL 0x887A0021=DRIVER_REMOVED) → 安全停止");
                AbortToSafe($"DeviceRemoved 0x{drr:X8}");
                return;
            }
        }
        catch { }

        try
        {
            // (a) 前フレームに我々の RT へ描かれた内容をオーバーレイへ転送。
            // ※ 空スキップ最適化は撤去。OnUiDraw は ImGui.Render() の前に走る
            //   ため ImGui.GetDrawData() が無効/前フレーム値となり「常に空」と
            //   誤判定 → 一度だけ転送して以後スキップ → 自分の画面にも UI が
            //   出なくなる不具合があったため。間隔ごとに必ず転送する
            //   (負荷は methodAUpdateIntervalFrames で制御)。
            _frameCount++;
            int interval = Math.Max(1, Cfg.methodAUpdateIntervalFrames);
            if (_frameCount % interval == 0) _overlay!.UpdateLayered(_clientX, _clientY);

            // (b) サイズ/前面追従
            if (GetGameClientScreenRect(out int gx, out int gy, out uint gw, out uint gh))
            {
                _clientX = gx; _clientY = gy;
                var fg = GetForegroundWindow();
                var ffxiv = GetOwnFfxivHwnd();
                bool resized = _overlay!.SetVisibleAndSize(ffxiv != IntPtr.Zero && fg == ffxiv, gw, gh);
                if (resized && !OverlayUsable()) { AbortToSafe("リサイズ後オーバーレイ無効"); return; }
            }

            // (c) この後の RenderDrawData が我々の RT へ描くよう差替
            if (!SetComPtr(_internals.MainViewportRenderTargetField!, _internals.MainViewport!, _overlay!.RenderTargetPtr)
                || !SetComPtr(_internals.MainViewportRenderTargetViewField!, _internals.MainViewport!, _overlay!.RenderTargetViewPtr)
                || !SetComPtr(_internals.MainViewportSwapChainField!, _internals.MainViewport!, IntPtr.Zero))
            {
                AbortToSafe("ComPtr 差替失敗");
                return;
            }
        }
        catch (Exception ex)
        {
            AbortToSafe($"OnUiDraw 例外: {ex.Message}");
        }
    }

    /// <summary>元の RT/RTV/swapChain に復元しオーバーレイ破棄。多重呼び出し安全。</summary>
    private void RestoreAndTeardownSync()
    {
        _active = false;
        UnhookDraw(); // 念のため確実に切り離す (冪等)

        // ★厳密冪等化: 実際に差し替えていない (= 既に復元済み or 一度も
        //   差替えていない) なら、Dalamud の正常な mainViewport に二度と
        //   触らない。ここで reflection 上書き+ResetBuffers を再適用すると
        //   正常状態を破壊して GPU デバイス除去→クラッシュする
        //   (実機repro: ON→OFF→プラグイン無効化)。
        if (!_swapped)
        {
            var leftover = _overlay;
            _overlay = null;
            if (leftover != null)
            {
                try
                {
                    if (Plugin.Framework.IsInFrameworkUpdateThread) leftover.DisposeWindowOnly();
                    else leftover.RequestCloseNonBlocking();
                }
                catch { }
            }
            _origSwapChain = _origRt = _origRtv = null;
            return;
        }
        _swapped = false; // これ以降の再入は上の no-op 経路へ

        try
        {
            var mv = _internals.MainViewport;
            if (mv != null)
            {
                // swapChain は「Enable 時に保存した古いポインタ」ではなく、
                // Dalamud が追跡している "生きた" ゲーム swapchain を復元する。
                // 保存値はその後 FFXIV が swapchain を作り直していると stale となり、
                // 復元→ResetBuffers/GetBuffer で解放済みオブジェクトを触り
                // GPU デバイス除去 (DXGI_DEVICE_REMOVED) を招くため (実機: teardown
                // 直後に vnavmesh が DEVICE_REMOVED を踏んでゲームごとクラッシュ)。
                var liveSc = _internals.GetLiveGameSwapChain();
                if (liveSc == IntPtr.Zero) liveSc = _internals.GameSwapChainPtr; // 次善
                if (liveSc != IntPtr.Zero)
                    SetComPtr(_internals.MainViewportSwapChainField!, mv, liveSc);
                else
                    // 生きた swapchain 不明: 空にして Dalamud にオフスクリーン
                    // テクスチャを作らせる (UI は不可視になるがクラッシュは回避)。
                    SetComPtr(_internals.MainViewportSwapChainField!, mv, IntPtr.Zero);

                // RT/RTV は保存値 (stale 化しうる) を戻さず空に。
                // ResetBuffers の .Reset() は空に対し no-op で安全。次
                // EnsureRenderTarget が上記 swapChain から作り直す。
                SetComPtr(_internals.MainViewportRenderTargetField!, mv, IntPtr.Zero);
                SetComPtr(_internals.MainViewportRenderTargetViewField!, mv, IntPtr.Zero);
                _origSwapChain = _origRt = _origRtv = null;

                _internals.MainViewportResetBuffersMethod?.Invoke(mv, null);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Method A] 復元失敗 (重大): {ex}");
        }
        try
        {
            if (_internals.GameDeviceContextPtr != IntPtr.Zero)
                ((ID3D11DeviceContext*)_internals.GameDeviceContextPtr)->Flush();
        }
        catch { }
        try
        {
            // ★非ブロッキングで破棄。プラグイン無効化(Dispose)が Framework
            //  スレッド外で走るとき RunOnFrameworkThread().GetResult() は
            //  WaitBeforeDispose 中の Framework 停止とデッドロックし、ゲームが
            //  無応答で落ちる (実機: 3回目の無効化で ClearAndDisposeAllResources
            //  直後に即死)。Framework スレッド上なら即破棄、それ以外は待たずに
            //  WM_CLOSE を投げて所有スレッドに破棄させる。D3D は有界リーク。
            var ov = _overlay;
            if (ov != null)
            {
                if (Plugin.Framework.IsInFrameworkUpdateThread)
                    ov.DisposeWindowOnly();
                else
                    ov.RequestCloseNonBlocking();
            }
        }
        catch (Exception ex) { Plugin.Log.Warning($"[Method A] overlay 破棄例外: {ex.Message}"); }
        _overlay = null;
    }

    /// <summary>ComPtr&lt;T&gt; フィールドの ptr_ を差し替える (ptr=Zero で空に)。</summary>
    private bool SetComPtr(FieldInfo field, object mv, IntPtr ptr)
    {
        try
        {
            var comPtrType = field.FieldType;
            var ptrField = comPtrType.GetField("ptr_", BindingFlags.NonPublic | BindingFlags.Instance);
            if (ptrField == null) { Plugin.Log.Error($"[Method A] {comPtrType.Name}.ptr_ なし"); return false; }
            object boxed = Activator.CreateInstance(comPtrType)!;
            ptrField.SetValue(boxed, System.Reflection.Pointer.Box((void*)ptr, ptrField.FieldType));
            field.SetValue(mv, boxed);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Method A] SetComPtr 例外: {ex.Message}");
            return false;
        }
    }

    private bool GetGameClientScreenRect(out int x, out int y, out uint w, out uint h)
    {
        x = y = 0; w = h = 0;
        var hwnd = GetOwnFfxivHwnd();
        if (hwnd == IntPtr.Zero) return false;
        if (!GetClientRect(hwnd, out RECT rc)) return false;
        int cw = rc.right - rc.left;
        int ch = rc.bottom - rc.top;
        // 1x1 などの不正/未確定サイズでは絶対に成功扱いしない。
        // (ここで 1x1 を許すと Dalamud のレンダラを 1x1/不正 RT へ向けて
        //  RenderDrawDataInternal が例外→ゲームごとクラッシュする)
        if (cw < 16 || ch < 16) return false;
        var tl = new POINT { X = rc.left, Y = rc.top };
        if (!ClientToScreen(hwnd, ref tl)) return false;
        x = tl.X; y = tl.Y;
        w = (uint)cw;
        h = (uint)ch;
        return true;
    }

    public void Dispose() => RestoreAndTeardownSync();

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
}
