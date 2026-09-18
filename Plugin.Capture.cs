using System;

namespace MaskedDalamud;

// Plugin の機能別 partial: キャプチャ除外方式の操作 ―
// Method A 診断 / 各方式 (Method A / Scrub / GPU-GDI / DComp) の Enable/Disable /
// 排他制御 (DisableOtherCaptureMethods) / 旧 WDA 連携 / 排他FS→ボーダレス化 /
// 固着 swapchain 自己回復 / WDA 再適用リトライ / /md 系コマンドハンドラ。
// フィールド・P/Invoke 宣言・定数は Plugin.cs 側に集約 (partial で参照可能)。
public partial class Plugin
{
    // Method A (RTV リダイレクト) Stage 1: 読み取り専用 reflection 診断。
    // 差替は一切行わないためクラッシュ・黒残留リスクはゼロ。
    // ゲーム内でこの診断が全段 OK になることを確認してから Stage 2 (実差替) に進む。
    private DalamudImGuiInternals? _methodAProbe;

    public void RunMethodADiagnostics()
    {
        try
        {
            _methodAProbe ??= new DalamudImGuiInternals();
            _methodAProbe.Resolve();
            _methodAProbe.LogDiagnostics();

            var p = _methodAProbe;
            Say("[Masked Dalamud] Method A 診断 ----");
            Say($"  Backend:   {(p.Backend?.GetType().Name ?? "—")}");
            Say($"  Renderer:  {(p.Renderer?.GetType().Name ?? "—")}");
            Say($"  GameDevice:    0x{p.GameDevicePtr.ToInt64():X}");
            Say($"  GameSwapChain: 0x{p.GameSwapChainPtr.ToInt64():X}");
            if (p.Available)
            {
                Say("  => 全 reflection 段 OK。Stage 2 (実差替) に進行可能");
            }
            else
            {
                Err($"  => 取得失敗: {p.FailReason}");
                Err("  => フォールバック動作 (リダイレクト無効)。Stage 2 へは進めない");
            }
            Say("  詳細は /xllog の [Method A] 行を参照");
        }
        catch (Exception ex)
        {
            Log.Error($"[Method A] 診断中に予期しない例外: {ex}");
            Err($"[Masked Dalamud] Method A 診断で例外: {ex.Message}");
        }
    }

    // ===== Method A 操作 (WDA とは完全独立。失敗しても WDA 側に波及しない) =====
    public bool MethodAActive => methodA?.IsActive ?? false;
    public string? MethodALastError => methodA?.LastError;

    /// <summary>Method A を有効化。成否を返す。失敗しても WDA には一切影響しない。</summary>
    public bool EnableMethodA()
    {
        methodA ??= new MethodARedirect(this);
        if (methodA.IsActive) return true;
        bool ok = methodA.Enable();
        if (ok) { cfg.methodAEnabled = true; cfg.Save(); }
        return ok;
    }

    /// <summary>Method A を解除し通常描画へ復元。</summary>
    public void DisableMethodA()
    {
        methodA ??= new MethodARedirect(this);
        methodA.Disable();
        cfg.methodAEnabled = false; cfg.Save();
    }

    // /md a コマンド
    // 新方式 (最終提示スクラブ + 独立 UI リプレイ)。Dalamud 無改変。主機能。
    public bool ScrubActive => scrub?.IsActive ?? false;
    public string? ScrubLastError => scrub?.LastError;

    /// <summary>スクラブ方式 (CPU) を有効化 (設定に永続化)。GPU 合成方式とは
    /// 排他: 有効化時に GPU 合成方式が動いていれば自動で解除する。</summary>
    public bool EnableScrub()
    {
        scrub ??= new ScrubOverlay(this);
        if (scrub.IsActive) return true;
        DisableOtherCaptureMethods(CaptureMethod.Scrub);
        bool ok = scrub.Enable();
        if (ok)
        {
            cfg.scrubEnabled = true;
            cfg.scrubGdiGpu = false;
            cfg.Save();
        }
        return ok;
    }

    /// <summary>スクラブ方式を解除 (設定に永続化)。</summary>
    public void DisableScrub()
    {
        scrub ??= new ScrubOverlay(this);
        scrub.Disable();
        cfg.scrubEnabled = false; cfg.Save();
        ScheduleSwapchainRecovery();   // 固着していれば窓 nudge で再同期
    }

    // ── 新方式: GPU-GDI レイヤードスクラブ (完全独立) ──
    public bool GdiScrubActive => gdiScrub?.IsActive ?? false;
    public string? GdiScrubLastError => gdiScrub?.LastError;

    public bool EnableGdiScrub()
    {
        gdiScrub ??= new GdiScrubOverlay(this);
        if (gdiScrub.IsActive) return true;
        DisableOtherCaptureMethods(CaptureMethod.GdiScrub);
        bool ok = gdiScrub.Enable();
        if (ok)
        {
            cfg.scrubGdiGpu = true;
            cfg.scrubEnabled = false;                   // 永続フラグも排他
            cfg.Save();
        }
        return ok;
    }

    public void DisableGdiScrub()
    {
        gdiScrub?.Disable();
        cfg.scrubGdiGpu = false; cfg.Save();
        ScheduleSwapchainRecovery();   // 固着していれば窓 nudge で再同期
    }

    // ── DComp Composition Swapchain (推奨方式・UpdateLayeredWindow 撤廃) ──
    public bool DCompActive => dcompOverlay?.IsActive ?? false;
    public string? DCompLastError => dcompOverlay?.LastError;
    internal DCompOverlay.DiagSnapshot DCompDiag => dcompOverlay?.LastDiag ?? default;

    public bool EnableDComp()
    {
        // DComp は CreateSwapChainForComposition で新しい DXGI swapchain を作る。
        // Framework (描画) スレッド外 (例: /md コマンドのディスパッチスレッド) から
        // 実行すると GShade(ReShade) の swapchain フックと競合し、ReShade が新
        // swapchain を誤検知して再初期化 → 画面が真っ暗になる。設定画面 (ImGui Draw)
        // は元々描画スレッドなので問題ないが、コマンド経路のため必ずゲームスレッドへ移譲。
        if (!Framework.IsInFrameworkUpdateThread)
        {
            bool r = false;
            try { Framework.RunOnFrameworkThread(() => r = EnableDComp()).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Error($"[DComp] EnableDComp スレッド移譲 例外: {ex.Message}"); }
            return r;
        }
        dcompOverlay ??= new DCompOverlay(this);
        if (dcompOverlay.IsActive) return true;
        DisableOtherCaptureMethods(CaptureMethod.DComp);
        bool ok = dcompOverlay.Enable();
        if (ok) { cfg.dcompOverlayEnabled = true; cfg.Save(); }
        return ok;
    }

    public void DisableDComp()
    {
        // Teardown で COM オブジェクトを Release するため、こちらも描画スレッドで
        // 実行する (EnableDComp と同じ理由・ReShade フック競合回避)。
        if (!Framework.IsInFrameworkUpdateThread)
        {
            try { Framework.RunOnFrameworkThread(DisableDComp).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Error($"[DComp] DisableDComp スレッド移譲 例外: {ex.Message}"); }
            return;
        }
        dcompOverlay?.Disable();
        cfg.dcompOverlayEnabled = false; cfg.Save();
        ScheduleSwapchainRecovery();   // 固着していれば窓 nudge で再同期
    }

    // ── キャプチャ除外方式の排他制御 ──
    // backbuffer を二重操作すると破綻するため、スクラブ系 (Scrub/GdiScrub/DComp) と
    // 旧 WDA は常に 1 つだけ有効にする。各 Enable* の冒頭でこれを呼び自分以外を解除する。
    // (Method A はプラグイン UI 専用オーバーレイで backbuffer を触らないため排他対象外。)
    private enum CaptureMethod { None, Scrub, GdiScrub, DComp, Wda }

    /// <summary>指定方式以外のキャプチャ除外方式を全て解除する。</summary>
    private void DisableOtherCaptureMethods(CaptureMethod keep)
    {
        if (keep != CaptureMethod.Scrub && ScrubActive) DisableScrub();
        if (keep != CaptureMethod.GdiScrub && GdiScrubActive) DisableGdiScrub();
        if (keep != CaptureMethod.DComp && DCompActive) DisableDComp();
        if (keep != CaptureMethod.Wda) DisableWdaIfOn();
    }

    // ── 旧 WDA (ウィンドウ全体除外) との排他連携用ヘルパー ──
    /// <summary>旧 WDA が ON なら自動で OFF にする (スクラブ系を有効化したとき呼ぶ)。</summary>
    public void DisableWdaIfOn()
    {
        if (cfg.enabled || _appliedState)
        {
            cfg.enabled = false; cfg.Save();
            try { ApplyState(false); } catch { }
        }
    }

    /// <summary>旧 WDA を有効化 (スクラブ系を自動で OFF にしてから適用)。
    /// 設定 UI / 即時 ON ボタン / コマンドから呼び出す統一エントリ。</summary>
    public bool EnableWda()
    {
        DisableOtherCaptureMethods(CaptureMethod.Wda);   // 排他: スクラブ系を全て停止
        cfg.enabled = true; cfg.Save();
        try { ApplyState(true); } catch (Exception ex) { Log.Error($"[Wda] Enable ApplyState 例外: {ex.Message}"); return false; }
        // 起動直後に HWND が無くて ApplyState が空振りした場合に備えてリトライループ。
        ScheduleWdaApplyRetry(0);
        return true;
    }

    /// <summary>旧 WDA を無効化。</summary>
    public void DisableWda()
    {
        cfg.enabled = false; cfg.Save();
        try { ApplyState(false); } catch { }
    }

    // HWND が用意できるまで WDA を執拗に再適用するリトライ (FFXIV 再起動直後の救済)。
    // [試験] 排他フルスクリーンを検知したらボーダレス窓へ変換する。
    // swapchain の vtable フックはしない (GShade 競合回避)。GetDesc で FS を検知し、
    // 自前で SetFullscreenState(FALSE) を呼んだ後にウィンドウをボーダレス整形する。
    // ~0.5 秒に1回だけ評価 (毎フレームのモード切替合戦を避ける)。
    private unsafe void TryForceBorderless()
    {
        // OFF に切り替えられていたら、適用済みなら元へ復元して終了。
        if (!cfg.forceBorderlessExperimental)
        {
            if (_borderlessForcer.IsApplied) _borderlessForcer.Restore();
            return;
        }
        if ((_forceBorderlessTick++ % 30) != 0) return;   // ~0.5s 間隔

        _fsProbe ??= new DalamudImGuiInternals();
        if (!_fsProbe.Available) _fsProbe.Resolve();
        if (!_fsProbe.Available) return;

        var scPtr = _fsProbe.GetLiveGameSwapChain();
        if (scPtr == IntPtr.Zero) scPtr = _fsProbe.GameSwapChainPtr;
        if (scPtr == IntPtr.Zero) return;

        var sc = (TerraFX.Interop.DirectX.IDXGISwapChain*)scPtr;
        TerraFX.Interop.DirectX.DXGI_SWAP_CHAIN_DESC d;
        if (sc->GetDesc(&d) != 0) return;
        bool exclusiveFs = d.Windowed == 0;

        if (exclusiveFs)
        {
            // 1) 排他を抜けてウィンドウモードへ (DWM 合成へ戻す)。
            try { sc->SetFullscreenState(0, null); }
            catch (Exception ex) { Log.Warning($"[Borderless] SetFullscreenState 例外: {ex.Message}"); }
            // 2) FF14 窓をモニタ全体のボーダレスへ整形。
            var hwnd = GetFfxivHwnd();
            if (hwnd != IntPtr.Zero) _borderlessForcer.Apply(hwnd);
        }
    }

    // ── 固着 swapchain の自己回復 (オフ後など) ──
    // ゲーム swapchain がウィンドウサイズに追従できず固着している場合 (resizeBuffers が
    // 過去に失敗した等)、FF14 窓を 1px だけリサイズして戻すことで、ゲーム自身の正規の
    // ResizeBuffers 経路を自然に発火させ、サイズを再同期させる。D3D は一切触らないので安全。
    // 無効化のたびに少し遅延させて発火 (teardown が落ち着いてから・描画スレッドで実行)。
    private void ScheduleSwapchainRecovery()
    {
        try
        {
            Framework.RunOnTick(() => { try { RecoverStuckSwapchainIfNeeded(); } catch { } },
                delay: TimeSpan.FromMilliseconds(250));
        }
        catch { }
    }

    private unsafe void RecoverStuckSwapchainIfNeeded()
    {
        _fsProbe ??= new DalamudImGuiInternals();
        if (!_fsProbe.Available) _fsProbe.Resolve();
        if (!_fsProbe.Available) return;

        var scPtr = _fsProbe.GetLiveGameSwapChain();
        if (scPtr == IntPtr.Zero) scPtr = _fsProbe.GameSwapChainPtr;
        if (scPtr == IntPtr.Zero) return;

        var sc = (TerraFX.Interop.DirectX.IDXGISwapChain*)scPtr;
        TerraFX.Interop.DirectX.DXGI_SWAP_CHAIN_DESC d;
        if (sc->GetDesc(&d) != 0) return;
        if (d.Windowed == 0) return;   // 排他FSでは nudge しない
        uint scW = d.BufferDesc.Width, scH = d.BufferDesc.Height;

        var hwnd = GetFfxivHwnd();
        if (hwnd == IntPtr.Zero) return;
        if (!GetClientRect(hwnd, out RECT rc)) return;
        uint cw = (uint)(rc.right - rc.left), ch = (uint)(rc.bottom - rc.top);
        if (cw < 16 || ch < 16) return;

        // swapchain サイズと窓クライアントサイズが一致していれば固着していない = 何もしない。
        if (scW == cw && scH == ch) return;

        Log.Info($"[Recover] swapchain {scW}x{scH} != client {cw}x{ch} → 窓 nudge で ResizeBuffers 再同期");
        if (!GetWindowRect(hwnd, out RECT wr)) return;
        int w = wr.right - wr.left, h = wr.bottom - wr.top;
        if (w < 17) return;
        // 1px 縮めて戻す = ゲーム自身が正規経路で ResizeBuffers を 2 回発火。
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w - 1, h, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w, h, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // [診断] InterfaceManager 初期化を待って ResizeProbe を購読 (最大 ~30 秒リトライ)。
    private void ScheduleResizeProbeSubscribe(int attempt)
    {
        const int maxAttempts = 30;
        Framework.RunOnTick(() =>
        {
            try
            {
                if (resizeProbe != null) return;
                var probe = new ResizeProbe(this);
                if (probe.Subscribe()) { resizeProbe = probe; return; }
                probe.Dispose();
            }
            catch (Exception ex) { Log.Warning($"[ResizeProbe] subscribe 試行 {attempt} 例外: {ex.Message}"); }
            if (attempt < maxAttempts) ScheduleResizeProbeSubscribe(attempt + 1);
            else Log.Warning("[ResizeProbe] 購読を諦め (InterfaceManager 未解決のまま)");
        }, delay: TimeSpan.FromMilliseconds(1000));
    }

    private void ScheduleWdaApplyRetry(int attempt)
    {
        const int maxAttempts = 60; // ~30 秒
        Framework.RunOnTick(() =>
        {
            try
            {
                if (!cfg.enabled) return;
                var hwnds = GetAllFfxivHwnds();
                bool allOk = hwnds.Count > 0;
                foreach (var h in hwnds)
                {
                    if (!GetWindowDisplayAffinity(h, out uint a) || a != WDA_EXCLUDEFROMCAPTURE)
                    {
                        allOk = false; break;
                    }
                }
                if (allOk)
                {
                    if (attempt > 0) Log.Info($"[Wda] 再適用ループ 成功 (試行 {attempt})");
                    try { RefreshCapture(); } catch { }
                    return;
                }
                ApplyState(true);
                if (attempt + 1 < maxAttempts)
                    ScheduleWdaApplyRetry(attempt + 1);
                else
                    Log.Warning($"[Wda] 再適用ループ 諦め: HWND/affinity が想定状態にならず ({hwnds.Count} 窓)");
            }
            catch (Exception ex) { Log.Warning($"[Wda] 再適用ループ 例外: {ex.Message}"); }
        }, delay: TimeSpan.FromMilliseconds(attempt == 0 ? 1000 : 500));
    }


    // /md 主機能 = 推奨方式 (DComp キャプチャ除外) のトグル
    // 0.1.5.4 で GPU-GDI → DComp へ推奨方式変更に伴い切替。
    // /md (・/maskedalamud) の DComp トグル。GShade 再初期化を避けるため、enable/disable は
    // Present フェーズ (DrawUI) で実行する (config と同一タイミング)。状態表示もそのフレームで。
    private void HandleGdiCommand(string sub)
    {
        // ベンダー自動選択: AMD は GPU-GDI、それ以外 (NVIDIA等) は DComp をトグル/操作する。
        // 既存命名 (HandleGdiCommand) は履歴の都合で残してあるが、実体はベンダー対応の推奨方式ハンドラ。
        EnsureGpuVendorDetected();
        bool useGdi = cfg.autoSelectByVendor && IsAmdGpu;
        string label = useGdi ? "GPU-GDI" : "DComp";

        bool isActive() => useGdi ? GdiScrubActive : DCompActive;
        bool tryEnable() => useGdi ? EnableGdiScrub() : EnableDComp();
        void disable() { if (useGdi) DisableGdiScrub(); else DisableDComp(); }
        string? lastError() => useGdi ? GdiScrubLastError : DCompLastError;

        switch (sub)
        {
            case "status":
                Say($"[Masked Dalamud] キャプチャ除外({label}): {(isActive() ? "有効" : "無効")}"
                    + (lastError() != null ? $" / 直近エラー: {lastError()}" : ""));
                break;
            case "on":
                EnqueuePresentPhase(() =>
                {
                    if (isActive()) { Say($"[Masked Dalamud] キャプチャ除外({label})は既に有効です"); return; }
                    if (tryEnable())
                        Say($"[Masked Dalamud] キャプチャ除外({label}) ON — 配信に UI が映らず手元では見えることを確認してください");
                    else
                        Err($"[Masked Dalamud] キャプチャ除外({label}) ON 失敗: {lastError() ?? "原因不明"}");
                });
                break;
            case "off":
                EnqueuePresentPhase(() =>
                {
                    disable();
                    Say($"[Masked Dalamud] キャプチャ除外({label}) OFF — 通常表示へ");
                });
                break;
            case "":
            case "toggle":
            default:
                EnqueuePresentPhase(() =>
                {
                    if (isActive()) { disable(); Say($"[Masked Dalamud] キャプチャ除外({label}) OFF — 通常表示へ"); }
                    else if (tryEnable())
                        Say($"[Masked Dalamud] キャプチャ除外({label}) ON — 配信に UI が映らず手元では見えることを確認してください");
                    else
                        Err($"[Masked Dalamud] キャプチャ除外({label}) ON 失敗: {lastError() ?? "原因不明"}");
                });
                break;
        }
    }


    /// <summary>DTR バーのステータスエントリ等から、推奨方式 (キャプチャ除外) をトグルする。
    /// /md・ホットキーと同一経路 (ベンダー判定で AMD→GPU-GDI / 他→DComp)。
    /// 内部で Present フェーズにキューするのでどのスレッドから呼んでも安全。</summary>
    public void ToggleRecommendedCapture() => HandleGdiCommand("toggle");

    private void HandleScrubCommand(string sub)
    {
        scrub ??= new ScrubOverlay(this);
        switch (sub)
        {
            case "on":
                if (scrub.IsActive) { Say("[Masked Dalamud] スクラブは既に有効です"); return; }
                if (EnableScrub())
                {
                    Say("[Masked Dalamud] スクラブ方式 ON — 配信に UI が映らず手元では見えることを確認してください");
                }
                else
                {
                    Err($"[Masked Dalamud] スクラブ ON 失敗: {scrub.LastError}");
                }
                break;
            case "off":
                DisableScrub();
                Say("[Masked Dalamud] スクラブ方式 OFF — 通常表示へ");
                break;
            case "":
            case "toggle":
                if (scrub.IsActive) HandleScrubCommand("off");
                else HandleScrubCommand("on");
                break;
            case "status":
                Say($"[Masked Dalamud] スクラブ: {(scrub.IsActive ? "有効" : "無効")}"
                    + (scrub.LastError != null ? $" / 直近エラー: {scrub.LastError}" : ""));
                break;
            default:
                Say("[Masked Dalamud] /md scrub [on|off|toggle|status]");
                break;
        }
    }

    private void HandleMethodACommand(string sub)
    {
        methodA ??= new MethodARedirect(this);
        switch (sub)
        {
            case "on":
                if (methodA.IsActive) { Say("[Masked Dalamud] キャプチャ除外(UI) は既に有効です"); return; }
                if (EnableMethodA())
                {
                    Say("[Masked Dalamud] キャプチャ除外(UI) を有効化 — プラグイン UI を専用オーバーレイへ");
                    Say("  配信(ゲームキャプチャ)に UI が映らず、手元では見えることを確認してください");
                }
                else
                {
                    Err($"[Masked Dalamud] キャプチャ除外(UI) 有効化失敗: {methodA.LastError}");
                    Err("  フォールバック動作 (リダイレクト無効・通常描画) を維持しています");
                }
                break;
            case "off":
                DisableMethodA();
                Say("[Masked Dalamud] キャプチャ除外(UI) を解除 — 通常描画に復元");
                break;
            case "":
            case "toggle":
                if (methodA.IsActive) HandleMethodACommand("off");
                else HandleMethodACommand("on");
                break;
            case "status":
                Say($"[Masked Dalamud] キャプチャ除外(UI): {(methodA.IsActive ? "有効 (リダイレクト中)" : "無効")}");
                if (methodA.LastError != null) Say($"  直近エラー: {methodA.LastError}");
                break;
            default:
                Say("[Masked Dalamud] /md a [on|off|toggle|status]");
                break;
        }
    }
}
