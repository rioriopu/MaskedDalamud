using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;

using MaskedDalamud.Windows;

namespace MaskedDalamud;

public partial class Plugin : IDalamudPlugin
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static IKeyState KeyState { get; private set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] public static INotificationManager NotificationManager { get; private set; } = null!;
    [PluginService] public static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] public static IGameGui GameGui { get; private set; } = null!;
    [PluginService] public static IGameInteropProvider GameInterop { get; private set; } = null!;

    public string Name => "MaskedDalamud";
    public const string CommandName = "/maskedalamud";
    public const string CommandShort = "/md";

    public Configuration cfg { get; init; }
    public readonly WindowSystem WindowSystem = new("MaskedDalamud");
    private ConfigWindow ConfigWindow { get; init; }
    private StatusWindow StatusWindow { get; init; }

    // Dalamud サーバー情報バーへ登録する自前 DTR エントリ (● Masked / ○ Masked)。
    private Dalamud.Game.Gui.Dtr.IDtrBarEntry? _selfDtrEntry;
    private string _selfDtrText = "";

    private bool _prevHotkey;
    private bool _appliedState;
    private IntPtr _lastAppliedHwnd = IntPtr.Zero;
    private int _lastWindowCount = 0;
    private int _wdaVerifyCounter = 0;

    // Method A Stage 2: RTV リダイレクト (mainViewport.swapChain 差替) — 旧/不安定
    internal MethodARedirect? methodA;

    // Method A 作り直し版: 最終提示スクラブ + 独立 UI リプレイ (Dalamud 無改変)
    internal ScrubOverlay? scrub;
    internal GdiScrubOverlay? gdiScrub;
    internal DCompOverlay? dcompOverlay;

    // [診断] resizeBuffers 失敗の切り分け用。常時購読 (無効化中も計測)。
    internal ResizeProbe? resizeProbe;

    // [試験] 排他FS→強制ボーダレス化用。
    private readonly BorderlessForcer _borderlessForcer = new();
    private DalamudImGuiInternals? _fsProbe;
    private int _forceBorderlessTick;

    // DComp 起動時自動復元: GShade 再初期化を避けるため、Framework.Update ではなく
    // UiBuilder.Draw (Present フェーズ・config と同じタイミング) で EnableDComp する。
    private bool _dcompAutoStartPending;
    private readonly System.Diagnostics.Stopwatch _dcompAutoStartSw = new();

    // Present フェーズ (UiBuilder.Draw) で実行するアクションのキュー。/md・ホットキー等の
    // DComp 有効/無効を config と同じ Present フェーズで行い GShade 再初期化を避けるため。
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _presentPhaseQueue = new();
    internal void EnqueuePresentPhase(Action a) => _presentPhaseQueue.Enqueue(a);

    // Phase E プロトタイプの Layered Overlay インスタンス
    internal LayeredOverlay? layeredOverlay;
    public bool PhaseEActive => layeredOverlay != null && layeredOverlay.IsOpen;

    // [試験・独立] KamiToolKit ネイティブ UI ミラー。既存方式と状態を共有しない別系統。
    internal KamiMirror? kamiMirror;
    /// <summary>[検証用] キャプチャ除外を止めたままミラーを backbuffer へ描くモード。</summary>
    internal AtkMirrorPreview? mirrorPreview;
    /// <summary>[調査] ゲームのピクセルシェーダ バイトコード採取。</summary>
    internal ShaderCapture? shaderCapture;

    /// <summary>[調査] D3D デバイスを解決してシェーダ採取フックを仕掛ける。</summary>
    internal bool TryEnableShaderCapture()
    {
        try
        {
            var probe = new DalamudImGuiInternals();
            probe.Resolve();
            if (!probe.Available)
            {
                if (shaderCapture != null) return false;
                return false;
            }
            return shaderCapture?.Enable(probe.GameDevicePtr) ?? false;
        }
        catch (Exception ex) { Log.Warning($"[ShaderCapture] 開始失敗: {ex.Message}"); return false; }
    }

    // Win32 定義
    public const uint WDA_NONE = 0x00000000;
    public const uint WDA_MONITOR = 0x00000001;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, out uint TokenInformation, uint TokenInformationLength, out uint ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    // 固着 swapchain 回復用 (窓 nudge)。SWP_* 定数は既存定義を流用。
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);

    // 複数 FFXIV プロセス (= 2 垢以上) の HWND を全件列挙するため EnumWindows を使う
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // ウィンドウ全体の再描画通知 (キャプチャ側にフレーム再評価を強制するため)
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);
    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_UPDATENOW = 0x0100;
    private const uint RDW_FRAME = 0x0400;
    private const uint RDW_ALLCHILDREN = 0x0080;

    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    // ====== タイミング/間隔の調整値 ======
    // DComp 起動時自動復元の猶予 (D3D/窓の準備待ち。早すぎる試行のログ荒れを防ぐ)。
    private const int DcompAutoStartDelayMs = 2000;
    // DComp 起動時自動復元を諦めるタイムアウト。
    private const int DcompAutoStartTimeoutMs = 60000;
    // WDA 実 affinity 検証 (自己修復) の実行間隔。OnUpdate 約 60fps で約 2 秒ごと。
    private const int WdaVerifyIntervalFrames = 120;

    // このロードの世代トークン。ホットリロードで旧ロードの overlay (孤児) を
    // 自滅させるために使う ([[OverlayGeneration]])。ロード時に最新を主張する。
    internal string OverlayGen { get; } = OverlayGeneration.Claim();

    /// <summary>いずれかのキャプチャ除外方式が有効か (集約状態)。ステータス表示に使う。</summary>
    public bool AnyMaskingActive => DCompActive || ScrubActive || GdiScrubActive || MethodAActive || PhaseEActive || cfg.enabled;

    /// <summary>キャプチャ除外スクラブ系 (DComp / GPU-GDI / CPU) のいずれかが動作中か。</summary>
    public bool CaptureScrubActive => DCompActive || ScrubActive || GdiScrubActive;

    /// <summary>現在有効な方式名 (ステータスウィンドウの補足表示用)。無効なら空文字。</summary>
    public string ActiveMethodLabel
    {
        get
        {
            if (DCompActive) return "DComp";
            if (GdiScrubActive) return "GPU-GDI";
            if (ScrubActive) return "スクラブ";
            if (MethodAActive) return "Method A";
            if (PhaseEActive) return "Phase E";
            if (cfg.enabled) return "WDA";
            return "";
        }
    }

    public Plugin()
    {
        cfg = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // 自作 UI ライブラリ EstellUtils を初期化。
        // Dalamud の WindowSystem と併存できる (どちらも UiBuilder.Draw に繋がるだけ)。
        try { EstellUtils.UI.EUi.Initialize(PluginInterface, log: Log); }
        catch (Exception ex) { Log.Warning($"[EstellUtils] 初期化失敗 (既存 UI で続行): {ex.Message}"); }

        ConfigWindow = new ConfigWindow(this);
        EstellUtils.UI.EUi.Windows.Add(ConfigWindow);

        // ウィンドウの位置と大きさを設定ファイルへ保存/復元する。
        // Add した後に呼ぶこと (BindLayout は登録済みウィンドウへ入れ物を配る)。
        try { EstellUtils.UI.EUi.Windows.BindLayout(cfg.WindowLayout, cfg.Save); }
        catch (Exception ex) { Log.Warning($"[EstellUtils] ウィンドウ位置の復元に失敗: {ex.Message}"); }

        StatusWindow = new StatusWindow(this);
        WindowSystem.AddWindow(StatusWindow);

        // Dalamud のサーバー情報バーに自前エントリを登録。Text は UpdateSelfDtrEntry で随時更新。
        // 左クリック = キャプチャ除外トグル / 右クリック = 設定画面。
        try
        {
            _selfDtrEntry = DtrBar.Get("Masked Dalamud");
            _selfDtrEntry.Tooltip = new Dalamud.Game.Text.SeStringHandling.SeString(
                new Dalamud.Game.Text.SeStringHandling.Payloads.TextPayload(
                    "左クリック: キャプチャ除外 ON/OFF ／ 右クリック: 設定"));
            _selfDtrEntry.OnClick = e =>
            {
                if (e.ClickType == Dalamud.Game.Gui.Dtr.MouseClickType.Right)
                    ToggleConfigUI();
                else
                    ToggleRecommendedCapture();
            };
        }
        catch (Exception ex) { Log.Warning($"[DTR] 自前エントリ登録失敗: {ex.Message}"); }

        // 通常時はプラグイン起動 (FFXIV 起動 / プラグイン読込) で設定を自動表示。
        // 表示設定タブで OFF にできる (デフォルト ON)。
        if (cfg.autoOpenConfigOnStartup)
            ConfigWindow.IsOpen = true;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "[on|off|toggle|status|refresh|diag|a|config] — 引数なしでトグル / a=Method A リダイレクト"
        });
        CommandManager.AddHandler(CommandShort, new CommandInfo(OnCommand)
        {
            HelpMessage = "/maskedalamud の短縮形 (引数なし=トグル / status=状態 / refresh=キャプチャ再認識 / config=設定)"
        });

        // [試験・独立] KamiToolKit ミラー。生成失敗しても他機能へ波及させない。
        try
        {
            kamiMirror = new KamiMirror(this);
            if (cfg.kamiMirrorEnabled) kamiMirror.Enable();
            mirrorPreview = new AtkMirrorPreview(this);
            shaderCapture = new ShaderCapture();
            // [調査] シェーダ採取はゲーム起動時の生成を捕まえたいので、
            // プラグイン読み込み直後の**できるだけ早い段階**で仕掛ける。
            if (cfg.kamiShaderCapture) TryEnableShaderCapture();
        }
        catch (Exception ex) { Log.Warning($"[KamiMirror] 生成失敗 (無視): {ex.Message}"); }

        Framework.Update += OnUpdate;
        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;
        PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUI;
        ChatGui.ChatMessage += OnPluginChatFilter; // 他プラグインのチャット抑制 (cfg.hidePluginChat)

        // [診断] ResizeBuffers イベント購読 (InterfaceManager 初期化を待って数回リトライ)。
        ScheduleResizeProbeSubscribe(0);

        // restoreOnLoad が ON の場合は最後の状態を反映 (config が enabled=true で保存されていれば即適用)
        // 加えて、配信中に FFXIV を再起動した状況の救済として、ロード時に強制的にキャプチャ再認識を発火する
        if (cfg.restoreOnLoad && cfg.enabled)
        {
            ApplyState(true);
            // FFXIV 再起動直後は HWND が用意できておらず ApplyState が空振りする。
            // 確実に WDA が EXCLUDE になるまで執拗に再適用 + 適用確認したら RefreshCapture。
            ScheduleWdaApplyRetry(0);
            if (cfg.refreshOnLoad)
            {
                // 念のため独立した RefreshCapture も発火 (古い WGC セッション救済)。
                Framework.RunOnTick(() => RefreshCapture(), delay: TimeSpan.FromMilliseconds(1500));
            }
        }

        // Method A は不安定のため起動時自動開始しない (/md a のみ)。

        // 主機能=スクラブ方式: scrubEnabled が保存されていれば起動時に自動復元。
        // Dalamud 内部が初期化され FFXIV ウィンドウが用意できるまでリトライ。
        // スクラブは Dalamud 無改変設計のため、Enable は条件未達なら安全に
        // false を返すだけ (クラッシュしない)。
        // 3 方式は排他 (永続フラグは1つだけ true)。優先: DComp(推奨) > GPU-GDI > CPU。
        // DComp / GPU-GDI は *RestoreOnLoad フラグのみを真実とする (チェック単独で
        // 起動時 ON。前回状態は不要)。CPU は従来通り「前回 ON」要件あり。
        if (cfg.dcompRestoreOnLoad)
        {
            Log.Info("[DComp] 起動時自動復元 スケジュール (dcompRestoreOnLoad=true / Present フェーズで実行)");
            // RunOnTick(Framework.Update) で Enable すると GShade が swapchain 生成を
            // 描画フロー外と誤検知して再初期化する。config と同じ Present フェーズ
            // (UiBuilder.Draw) で Enable するため、ペンディングにして DrawUI で処理する。
            _dcompAutoStartPending = true;
            _dcompAutoStartSw.Restart();
        }
        else if (cfg.gdiScrubRestoreOnLoad)
        {
            Log.Info("[GdiScrub] 起動時自動復元 スケジュール (gdiScrubRestoreOnLoad=true)");
            ScheduleGdiScrubAutoStart(0);
        }
        else if (cfg.scrubRestoreOnLoad && cfg.scrubEnabled)
            ScheduleScrubAutoStart(0);
    }

    /// <summary>DComp 起動時自動復元を Present フェーズ (DrawUI) で試行する。
    /// GShade が描画フロー外の swapchain 生成を再初期化と誤検知するのを避けるため、
    /// config (チェックボックス) と同一の UiBuilder.Draw タイミングで Enable する。
    /// 起動直後は D3D/窓が未準備のため、準備できるまで毎フレーム再試行 (タイムアウト付き)。</summary>
    // ====== GPU ベンダー判定 (AMD=GPU-GDI / 他=DComp の自動選択用) ======
    private uint _gpuVendorId; // 0=未検出, 0x1002=AMD, 0x10DE=NVIDIA, 0x8086=Intel
    public uint GpuVendorId => _gpuVendorId;
    public bool IsAmdGpu => _gpuVendorId == 0x1002;
    public string GpuVendorName => _gpuVendorId switch
    {
        0x1002 => "AMD",
        0x10DE => "NVIDIA",
        0x8086 => "Intel",
        0 => "未検出",
        _ => $"0x{_gpuVendorId:X4}"
    };

    // ====== ReShade / GShade 検出 (DComp 自動選択回避用) ======
    // ReShade は DXGI スワップチェインをフックするため、DComp の合成スワップチェイン
    // 生成/破棄と競合し、無効化・表示モード切替時に NVIDIA ドライバごとクラッシュする。
    // 検出時は自動選択で DComp を避け GPU-GDI (スワップチェイン非生成) を選ぶ。
    private int _reshadeDetected = -1; // -1=未判定, 0=なし, 1=検出
    public bool IsReshadePresent { get { EnsureReshadeDetected(); return _reshadeDetected == 1; } }

    public void EnsureReshadeDetected()
    {
        if (_reshadeDetected != -1) return;
        try
        {
            _reshadeDetected = 0;
            foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
            {
                try
                {
                    var name = (m.ModuleName ?? "").ToLowerInvariant();
                    var desc = (m.FileVersionInfo?.FileDescription ?? "").ToLowerInvariant();
                    var prod = (m.FileVersionInfo?.ProductName ?? "").ToLowerInvariant();
                    // ReShade 本体 (dxgi.dll プロキシ / ReShade64.dll) や GShade、
                    // ReShade アドオン (*.addon64) を製品名・説明・ファイル名で判定。
                    if (name.Contains("gshade") || name.Contains("reshade") || name.EndsWith(".addon64")
                        || desc.Contains("reshade") || prod.Contains("reshade")
                        || desc.Contains("gshade") || prod.Contains("gshade"))
                    {
                        _reshadeDetected = 1;
                        Log.Info($"[ReShade] 検出: {m.ModuleName} ({prod}) → DComp 自動選択を回避");
                        break;
                    }
                }
                catch { /* 一部モジュールは FileVersionInfo 取得不可。無視。 */ }
            }
        }
        catch (Exception ex) { Log.Warning($"[ReShade] 検出失敗: {ex.Message}"); _reshadeDetected = 0; }
    }

    private static readonly Guid _iidIDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    /// <summary>ゲームの D3D11 デバイスからアダプターの VendorId を取得 (1回成功でキャッシュ)。</summary>
    public unsafe void EnsureGpuVendorDetected()
    {
        if (_gpuVendorId != 0) return;
        try
        {
            _fsProbe ??= new DalamudImGuiInternals();
            if (!_fsProbe.Available) _fsProbe.Resolve();
            if (!_fsProbe.Available || _fsProbe.GameDevicePtr == IntPtr.Zero) return;

            var dev = (TerraFX.Interop.DirectX.ID3D11Device*)_fsProbe.GameDevicePtr;
            TerraFX.Interop.DirectX.IDXGIDevice* dxgiDev = null;
            var iid = _iidIDXGIDevice;
            if (dev->QueryInterface(&iid, (void**)&dxgiDev).FAILED || dxgiDev == null) return;
            try
            {
                TerraFX.Interop.DirectX.IDXGIAdapter* adapter = null;
                if (dxgiDev->GetAdapter(&adapter).FAILED || adapter == null) return;
                try
                {
                    TerraFX.Interop.DirectX.DXGI_ADAPTER_DESC desc;
                    if (adapter->GetDesc(&desc).SUCCEEDED)
                    {
                        _gpuVendorId = desc.VendorId;
                        Log.Info($"[GPU] VendorId=0x{_gpuVendorId:X4} ({GpuVendorName})");
                    }
                }
                finally { adapter->Release(); }
            }
            finally { dxgiDev->Release(); }
        }
        catch (Exception ex) { Log.Warning($"[GPU] vendor 検出失敗: {ex.Message}"); }
    }

    private void TryDcompAutoStartOnDraw()
    {
        if (!_dcompAutoStartPending) return;
        try
        {
            // 起動直後の猶予 (D3D/窓の準備待ち。早すぎる試行のログ荒れを防ぐ)。
            if (_dcompAutoStartSw.ElapsedMilliseconds < DcompAutoStartDelayMs) return;
            // タイムアウトで諦め。
            if (_dcompAutoStartSw.ElapsedMilliseconds > DcompAutoStartTimeoutMs)
            {
                _dcompAutoStartPending = false;
                Log.Warning($"[DComp] 起動時自動復元を諦め (タイムアウト): {dcompOverlay?.LastError}");
                return;
            }
            dcompOverlay ??= new DCompOverlay(this);
            if (dcompOverlay.IsActive) { _dcompAutoStartPending = false; return; }

            // 自動選択で GPU-GDI を優先する条件:
            //  ① AMD: MPO 不発で DComp が重い
            //  ② ReShade/GShade 環境: DComp の合成スワップチェイン破棄が ReShade の
            //     フックと衝突し、無効化・表示モード切替でドライバごとクラッシュするため
            //     (GPU-GDI はスワップチェインを作らないレイヤード窓で安全)。
            if (cfg.autoSelectByVendor || cfg.avoidDcompWithReshade)
            {
                EnsureGpuVendorDetected();
                bool amd = cfg.autoSelectByVendor && IsAmdGpu;
                // ReShade 環境の判定:
                //  迂回 DComp が ON かつ実際に迂回が成立する (本物 dxgi を掴める) なら DComp を使う。
                //  迂回が OFF or 不成立なら GPU-GDI へ逃がす (破棄/リサイズクラッシュ回避)。
                bool bypassOk = cfg.dcompReshadeBypass && (dcompOverlay?.ProbeBypass() ?? false);
                bool reshade = cfg.avoidDcompWithReshade && IsReshadePresent && !bypassOk;
                if (amd || reshade)
                {
                    var why = amd ? "AMD" : "ReShade/GShade";
                    Log.Info($"[自動選択] {why} 検出 → GPU-GDI を起動 (DComp はスキップ)");
                    if (EnableGdiScrub())
                    {
                        _dcompAutoStartPending = false;
                        return;
                    }
                    // GPU-GDI 失敗時は下の DComp へフォールバック。
                }
            }

            // ここは UiBuilder.Draw = Present フェーズ。config と同じタイミングなので
            // GShade の再初期化が起きない。
            if (EnableDComp())
            {
                _dcompAutoStartPending = false;
                Log.Info("[DComp] 起動時自動復元 成功 (Present フェーズ)");
            }
            // 失敗時はペンディング維持 → 次フレーム再試行。
        }
        catch (Exception ex) { Log.Error($"[DComp] 起動時自動復元 例外: {ex}"); }
    }

    // GPU-GDI レイヤードスクラブ自動復元 (推奨方式)。レイヤード窓のため
    // 前面問題が無く、前面待ちゲートは不要 (CPU 方式と同様の単純リトライ)。
    private void ScheduleGdiScrubAutoStart(int attempt)
    {
        // 起動直後 (タイトル/ログイン/ロード) は D3D/窓未準備で Enable が
        // false を返すため、在世まで長めにリトライする (約6分)。
        const int maxAttempts = 240;
        Framework.RunOnTick(() =>
        {
            try
            {
                gdiScrub ??= new GdiScrubOverlay(this);
                // ユーザが auto を OFF にした / すでに起動済み の場合は静かに終了。
                if (gdiScrub.IsActive || !cfg.gdiScrubRestoreOnLoad) return;
                if (attempt == 0 || attempt % 20 == 0)
                    Log.Info($"[GdiScrub] 起動時自動復元 試行 {attempt} (auto={cfg.gdiScrubRestoreOnLoad})");
                if (EnableGdiScrub()) { Log.Info($"[GdiScrub] 起動時自動復元 成功 (試行 {attempt})"); return; }
                if (attempt + 1 < maxAttempts)
                    ScheduleGdiScrubAutoStart(attempt + 1);
                else
                    Log.Warning($"[GdiScrub] 起動時自動復元を諦め: {gdiScrub.LastError}");
            }
            catch (Exception ex) { Log.Error($"[GdiScrub] 起動時自動復元 例外: {ex}"); }
        }, delay: TimeSpan.FromMilliseconds(attempt == 0 ? 2500 : 1500));
    }

    private void ScheduleScrubAutoStart(int attempt)
    {
        const int maxAttempts = 30;
        Framework.RunOnTick(() =>
        {
            try
            {
                scrub ??= new ScrubOverlay(this);
                if (scrub.IsActive || !cfg.scrubEnabled) return;
                if (scrub.Enable()) { Log.Info("[Scrub] 起動時自動復元 成功"); return; }
                if (attempt + 1 < maxAttempts)
                    ScheduleScrubAutoStart(attempt + 1);
                else
                    Log.Warning($"[Scrub] 起動時自動復元を諦め: {scrub.LastError}");
            }
            catch (Exception ex) { Log.Error($"[Scrub] 起動時自動復元 例外: {ex}"); }
        }, delay: TimeSpan.FromMilliseconds(attempt == 0 ? 2500 : 1500));
    }

    public void Dispose()
    {
        // 既定では Dispose 時に WDA を解除しない。
        // FF14 を閉じた瞬間に Dispose で WDA を解除すると、ウィンドウがまだ
        // 描画中の一瞬だけキャプチャ対象に戻り Dalamud UI が配信へ漏れる
        // (ユーザ報告: 既存 WDA 固有。Method A では発生しない)。
        // removeWdaOnUnload を ON にした場合のみ従来どおり解除する
        // (明示的な OFF 操作での解除は別経路なので影響しない)。
        if (cfg.removeWdaOnUnload)
        {
            try { ApplyState(false); } catch { }
        }

        // Method A リダイレクトを必ず復元 (元の swapchain に戻してから後始末)。
        // これを怠ると Dalamud が解放済み swapchain を参照して落ちる。
        try
        {
            methodA?.Dispose();
            methodA = null;
        }
        catch { }

        // スクラブ方式の安全停止 (Dalamud 状態は元々無改変)。
        try
        {
            scrub?.Dispose();
            scrub = null;
        }
        catch { }

        // (旧 GPU 合成試験 / GpuScrubOverlay は廃止 — 新 DCompOverlay に置換)
        try
        {
            dcompOverlay?.Dispose();
            dcompOverlay = null;
        }
        catch { }

        // GPU-GDI レイヤードスクラブ (独立・安全停止)。
        try
        {
            gdiScrub?.Dispose();
            gdiScrub = null;
        }
        catch { }

        // [試験・独立] KamiToolKit ミラーを停止し、隠したノードを必ず復元する。
        try { shaderCapture?.Dispose(); shaderCapture = null; } catch { }
        try { mirrorPreview?.Dispose(); mirrorPreview = null; } catch { }
        try { kamiMirror?.Dispose(); kamiMirror = null; } catch { }

        // [診断] ResizeBuffers イベント購読解除。
        try { resizeProbe?.Dispose(); resizeProbe = null; } catch { }

        // [試験] 強制ボーダレス化していたら元のウィンドウスタイルへ復元。
        try { if (_borderlessForcer.IsApplied) _borderlessForcer.Restore(); } catch { }

        // Phase E プロトタイプの Layered Overlay を確実にクローズ
        try
        {
            layeredOverlay?.Close();
            layeredOverlay = null;
        }
        catch { }

        Framework.Update -= OnUpdate;
        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUI;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUI;
        try { ChatGui.ChatMessage -= OnPluginChatFilter; } catch { }
        try { _selfDtrEntry?.Remove(); } catch { }
        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        StatusWindow.Dispose();
        try { EstellUtils.UI.EUi.Shutdown(); } catch { }
        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandShort);
    }

    private bool _phaseBDiagnosticsLogged;

    private void DrawUI()
    {
        // GShade 再初期化回避: /md・ホットキー等からの DComp 有効/無効も Present フェーズ
        // (ここ) で実行する。config (チェックボックス) と同一タイミング。
        while (_presentPhaseQueue.TryDequeue(out var act))
        {
            try { act(); }
            catch (Exception ex) { Log.Error($"[DComp] Present フェーズ アクション例外: {ex.Message}"); }
        }
        // DComp 起動時自動復元も同じく Present フェーズで実行。
        TryDcompAutoStartOnDraw();
        // [試験] KamiToolKit ネイティブ UI のミラー描画。プラグイン窓より奥へ描くため
        // WindowSystem より先に呼ぶ (背景ドローリストなので実際の前後は変わらないが意図を明示)。
        try { kamiMirror?.Draw(); } catch { }
        WindowSystem.Draw();
    }

    private void DiagnoseImGuiViewports()
    {
        if (_phaseBDiagnosticsLogged) return;
        try
        {
            var io = Dalamud.Bindings.ImGui.ImGui.GetIO();
            var cfg = io.ConfigFlags;
            var backend = io.BackendFlags;
            bool viewportsEnable = (cfg & Dalamud.Bindings.ImGui.ImGuiConfigFlags.ViewportsEnable) != 0;
            bool platformViewports = (backend & Dalamud.Bindings.ImGui.ImGuiBackendFlags.PlatformHasViewports) != 0;
            bool rendererViewports = (backend & Dalamud.Bindings.ImGui.ImGuiBackendFlags.RendererHasViewports) != 0;

            Log.Info("[Phase E B] === ImGui Multi-Viewport 診断 ===");
            Log.Info($"[Phase E B] ConfigFlags = {cfg}");
            Log.Info($"[Phase E B] BackendFlags = {backend}");
            Log.Info($"[Phase E B] ConfigFlags.ViewportsEnable      = {viewportsEnable}");
            Log.Info($"[Phase E B] BackendFlags.PlatformHasViewports = {platformViewports}");
            Log.Info($"[Phase E B] BackendFlags.RendererHasViewports = {rendererViewports}");

            // 試行: ViewportsEnable を強制的に立ててみる
            // (※ Framework.Update スレッドからの書込は不安定な可能性。書き込み失敗時は無視)
            bool nowEnabled = viewportsEnable;
            try
            {
                io.ConfigFlags |= Dalamud.Bindings.ImGui.ImGuiConfigFlags.ViewportsEnable;
                var cfgAfter = io.ConfigFlags;
                nowEnabled = (cfgAfter & Dalamud.Bindings.ImGui.ImGuiConfigFlags.ViewportsEnable) != 0;
                Log.Info($"[Phase E B] ViewportsEnable 強制設定後 ConfigFlags.ViewportsEnable = {nowEnabled}");
            }
            catch (Exception exSet)
            {
                Log.Warning($"[Phase E B] ViewportsEnable 強制設定で例外: {exSet.Message}");
            }

            // 判定: B (Multi-Viewport) が使えるか
            bool bUsable = nowEnabled && platformViewports && rendererViewports;
            if (bUsable)
            {
                Log.Info("[Phase E B] ✅ Multi-Viewport が利用可能 → B 路線で実装続行可能");
                Say("[Masked Dalamud] Phase E B 診断: Multi-Viewport 利用可能 (詳細は /xllog)");
            }
            else
            {
                Log.Warning("[Phase E B] ❌ Multi-Viewport が利用不可 (Dalamud Renderer が未対応) → A 路線への切替が必要");
                Err("[Masked Dalamud] Phase E B 診断: Multi-Viewport 利用不可、A 路線が必要 (詳細は /xllog)");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Phase E B] 診断中の例外: {ex}");
        }
        finally
        {
            _phaseBDiagnosticsLogged = true;
        }
    }
    public void ToggleConfigUI() => ConfigWindow.Toggle();

    /// <summary>Phase E (Layered Window 分離) プロトタイプの ON/OFF を切替える。</summary>
    public void TogglePhaseE()
    {
        if (PhaseEActive)
        {
            layeredOverlay?.Close();
            layeredOverlay = null;
            cfg.phaseEEnabled = false;
            cfg.Save();
            Say("[Masked Dalamud] Phase E (試験版 Layered) を停止しました");
        }
        else
        {
            layeredOverlay ??= new LayeredOverlay();
            if (layeredOverlay.Open())
            {
                cfg.phaseEEnabled = true;
                cfg.Save();
                Say("[Masked Dalamud] Phase E (試験版 Layered) を開始しました — 配信に映らないことを確認してください");
            }
            else
            {
                Err("[Masked Dalamud] Phase E の起動に失敗しました");
            }
        }
    }

    private void OnCommand(string command, string argument)
    {
        var arg = argument.Trim().ToLowerInvariant();

        // Method A: "/md a", "/md a on|off|toggle"
        if (arg == "a" || arg.StartsWith("a "))
        {
            var sub = arg.Length > 1 ? arg.Substring(1).Trim() : "";
            HandleMethodACommand(sub);
            return;
        }

        // 新方式 (スクラブ): "/md scrub", "/md scrub on|off|toggle|status"
        if (arg == "scrub" || arg.StartsWith("scrub "))
        {
            var sub = arg.Length > 5 ? arg.Substring(5).Trim() : "";
            HandleScrubCommand(sub);
            return;
        }

        switch (arg)
        {
            // /md と /maskedalamud のトグル系は「スクラブ方式 (UI だけ配信から
            // 隠す・Dalamud 無改変)」を主機能として操作する。
            // 旧 WDA (ウィンドウ全体) は設定画面のクイック操作から。
            // Method A (不安定) は明示的な /md a のみ。
            case "on":
                HandleGdiCommand("on");
                break;
            case "off":
                HandleGdiCommand("off");
                break;
            case "config":
            case "cfg":
            case "settings":
                ToggleConfigUI();
                break;
            case "phasee":
            case "layered":
                TogglePhaseE();
                break;
            case "diag":
            case "diagnose":
                RunMethodADiagnostics();
                break;
            case "status":
            case "stat":
                NotifyStatus();
                break;
            case "ui":
            case "window":
            case "win":
                cfg.showStatusWindow = !cfg.showStatusWindow;
                cfg.Save();
                Say($"[Masked Dalamud] ステータスウィンドウ: {(cfg.showStatusWindow ? "表示" : "非表示")}");
                break;
            case "refresh":
            case "reset":
                RefreshCapture();
                break;
            case "":
            case "toggle":
                HandleGdiCommand("toggle");
                break;
            default:
                ToggleConfigUI();
                break;
        }
    }

    /// <summary>有効化/無効化結果を Chat とトースト両方に表示する。</summary>
    public static string AffinityToString(uint aff) => aff switch
    {
        WDA_NONE                 => "NONE",
        WDA_MONITOR              => "MONITOR",
        WDA_EXCLUDEFROMCAPTURE   => "EXCLUDEFROMCAPTURE",
        uint.MaxValue            => "取得不可",
        _                        => $"0x{aff:X8}",
    };

    /// <summary>WDA を一旦解除→再適用 + SWP_FRAMECHANGED + RedrawWindow + 複数回ループ
    /// で OBS/Discord にキャプチャ再評価を強制する。
    /// 配信中に FFXIV を落として再起動した直後にオーバーレイが見えてしまう状態の救済策。
    /// 全 FFXIV ウィンドウ (2 垢対応) に対して適用する。</summary>
    public void RefreshCapture()
    {
        var hwnds = GetAllFfxivHwnds();
        if (hwnds.Count == 0)
        {
            Err("[Masked Dalamud] FFXIV ウィンドウが見つかりません");
            return;
        }

        uint target = cfg.enabled ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE;
        int rounds = System.Math.Max(1, cfg.refreshRounds);

        for (int round = 0; round < rounds; round++)
        {
            foreach (var hwnd in hwnds)
            {
                // 3 段切り替え NONE → MONITOR → 目標 affinity (キャプチャ層が頑固なケース対応)
                SetWindowDisplayAffinity(hwnd, WDA_NONE);
                System.Threading.Thread.Sleep(40);
                SetWindowDisplayAffinity(hwnd, WDA_MONITOR);
                System.Threading.Thread.Sleep(40);
                SetWindowDisplayAffinity(hwnd, target);

                // ウィンドウのフレーム変化通知 + フル再描画でキャプチャ側に強い再評価誘発
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
                    RDW_INVALIDATE | RDW_FRAME | RDW_UPDATENOW | RDW_ALLCHILDREN);
            }
            if (round + 1 < rounds)
                System.Threading.Thread.Sleep(System.Math.Max(50, cfg.refreshRoundIntervalMs));
        }

        _appliedState = cfg.enabled;
        _lastAppliedHwnd = hwnds[0];

        var aff = GetCurrentAffinity();
        var msg = $"[Masked Dalamud] キャプチャ再認識を実行 ({rounds} 回 × {hwnds.Count} 窓) / WDA={AffinityToString(aff)}";
        Say(msg);
        Log.Info(msg);

        try
        {
            NotificationManager.AddNotification(new Dalamud.Interface.ImGuiNotification.Notification
            {
                Title = "Masked Dalamud",
                Content = $"OBS / Discord のキャプチャ再評価を強制しました ({rounds} 回 × {hwnds.Count} 窓)",
                Type = Dalamud.Interface.ImGuiNotification.NotificationType.Info,
            });
        }
        catch { }
    }

    private void OnUpdate(IFramework fw)
    {
        // Dalamud サーバー情報バーの自前エントリを更新。
        try { UpdateSelfDtrEntry(); } catch { }

        // Method A: FF14 が裏でも Framework.Update は回り続けるため、
        // ここで表示/非表示を更新する (Present 経路が止まっても裏画面に
        // UI が出っぱなしにならないようにする)。
        try { methodA?.FrameworkTick(); } catch { }
        try { scrub?.FrameworkTick(); } catch { }
        try { dcompOverlay?.FrameworkTick(); } catch { }
        try { gdiScrub?.FrameworkTick(); } catch { }

        // [試験・独立] KamiToolKit ノードの収集・非表示 (ゲームスレッドで行う)。
        try { kamiMirror?.FrameworkTick(); } catch { }
        // [検証用] キャプチャ除外が止まっていて検証トグルが ON のときだけ、
        // ミラーを backbuffer へ直接描く (スクリーンショットに写るようにするため)。
        try
        {
            // 実測モードは「ミラー OFF のネイティブ色」も測りたいので、
            // ミラーが止まっていても動かす必要がある。
            bool wantPreview = (cfg.kamiMirrorEnabled && cfg.kamiMirrorAlwaysRun && !CaptureScrubActive)
                             || (cfg.kamiPixelProbe && !CaptureScrubActive);
            if (wantPreview) { if (!(mirrorPreview?.IsActive ?? false)) mirrorPreview?.Enable(); }
            else if (mirrorPreview?.IsActive ?? false) mirrorPreview.Disable();
        }
        catch { }

        // [試験] 排他フルスクリーン → 強制ボーダレス化 (forceBorderlessExperimental)。
        try { TryForceBorderless(); } catch { }

        // Phase E Beta Step 1: 独立 swapchain のフレームレンダリング
        // (DX11 が bind されている時のみ、毎フレーム単色クリア + Present)
        try { layeredOverlay?.Dx11?.RenderFrame(); } catch { }

        // ※ Phase E A Step A.1 (LayeredImGuiBackend.RenderFrame) は実機でクラッシュ +
        //    黒画面残留 を引き起こしたため緊急撤去。
        //    Framework スレッドから ImGui.SetCurrentContext / NewFrame を呼ぶことが
        //    Dalamud の ImGui 状態と競合した模様。Step A.2 では UI スレッド側で呼ぶ設計に
        //    再構成する必要がある。
        // try { layeredOverlay?.ImGuiBackend?.RenderFrame(); } catch { }

        // Phase E B (Multi-Viewport) は実環境でマルチモニター必須となり実用性が制限的のため撤去済。
        // A 路線 (自前 DX11 ImGui backend) は LayeredDx11 + LayeredImGuiBackend で実装中。

        // 設定変更時の保険 + HWND がゲーム再起動 / 2 垢起動で変わった場合の追従反映。
        // 全 FFXIV ウィンドウの HWND 集合を毎フレーム取得し、
        //   - 新しい HWND が登場した
        //   - 前回適用したリストとセットが変わった
        // のどちらかなら ApplyState を呼ぶ + RefreshCapture も発火 (キャプチャ層救済)
        if (cfg.enabled)
        {
            var hwnds = GetAllFfxivHwnds();
            if (hwnds.Count > 0)
            {
                bool changed = !_appliedState
                               || !hwnds.Contains(_lastAppliedHwnd)
                               || hwnds.Count != _lastWindowCount;
                _lastWindowCount = hwnds.Count;

                // 実 affinity 検証による自己修復:
                // FF14 を閉じて再起動した場合、新インスタンスでは WDA が外れた状態で
                // 起動する。「changed」検知だけだと _appliedState の取りこぼしで
                // 再適用されないことがあるため、定期的に実際の affinity を確認し、
                // 1 つでも EXCLUDE でなければ確実に再適用 + キャプチャ再認識する。
                bool needHeal = false;
                if (++_wdaVerifyCounter >= WdaVerifyIntervalFrames) // 約 2 秒ごと
                {
                    _wdaVerifyCounter = 0;
                    foreach (var h in hwnds)
                    {
                        if (!GetWindowDisplayAffinity(h, out uint a) || a != WDA_EXCLUDEFROMCAPTURE)
                        {
                            needHeal = true;
                            break;
                        }
                    }
                }

                if (changed || needHeal)
                {
                    ApplyState(true);
                    // HWND 変化 or 未除外検出 → キャプチャセッションが古い/未除外の可能性
                    try { RefreshCapture(); } catch { }
                }
            }
        }
        else if (_appliedState)
        {
            ApplyState(false);
        }

        // ホットキー Ctrl + Shift + L
        if (cfg.useHotkey)
        {
            try
            {
                var ctrl  = KeyState[(Dalamud.Game.ClientState.Keys.VirtualKey)0x11];
                var shift = KeyState[(Dalamud.Game.ClientState.Keys.VirtualKey)0x10];
                var l     = KeyState[(Dalamud.Game.ClientState.Keys.VirtualKey)0x4C];
                var pressed = ctrl && shift && l;
                if (pressed && !_prevHotkey)
                {
                    // ホットキーは推奨方式 (ベンダー判定でAMD→GPU-GDI / 他→DComp) をトグル。
                    // /md と同一経路 (HandleGdiCommand) を通すことで挙動を統一する。
                    // GShade 再初期化を避けるため Present フェーズ (DrawUI) で実行 (HandleGdiCommand 内)。
                    HandleGdiCommand("toggle");
                }
                _prevHotkey = pressed;
            }
            catch { }
        }
    }

    /// <summary>FFXIV のメインウィンドウ HWND を毎回最新値で取得 (class = FFXIVGAME)。
    /// ゲーム再起動 / 全画面切替で HWND が変わるためキャッシュしない。
    /// 1 件目のみ返す互換 API。複数取りたい場合は GetAllFfxivHwnds を使う。</summary>
    public IntPtr GetFfxivHwnd()
    {
        var all = GetAllFfxivHwnds();
        return all.Count == 0 ? IntPtr.Zero : all[0];
    }

    /// <summary>
    /// 自プロセス (= 現在の FFXIV インスタンス) のメインウィンドウ HWND を列挙する。
    /// 通常は 1 件。他プロセス (他垢/別 FFXIV インスタンス) の窓には WDA を
    /// クロスプロセスで適用できない (UIPI/権限差で ERROR_ACCESS_DENIED) ため
    /// 含めない。各 FFXIV インスタンスにロードされた Masked Dalamud が
    /// それぞれの窓だけを面倒見る分担モデル。
    /// </summary>
    public List<IntPtr> GetAllFfxivHwnds()
    {
        var list = new List<IntPtr>();
        var sb = new System.Text.StringBuilder(64);
        uint myPid = GetCurrentProcessId();
        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hwnd)) return true;
                sb.Clear();
                int n = GetClassNameW(hwnd, sb, sb.Capacity);
                if (n <= 0) return true;
                if (sb.ToString() != "FFXIVGAME") return true;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == myPid) list.Add(hwnd);
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();

    public void ApplyState(bool enable)
    {
        var hwnds = GetAllFfxivHwnds();
        if (hwnds.Count == 0)
        {
            // 起動直後で HWND がまだ作られていないケース。次フレームでリトライさせるため記録は更新しない。
            return;
        }
        uint affinity = enable ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE;
        int ok = 0;
        foreach (var hwnd in hwnds)
        {
            if (SetWindowDisplayAffinity(hwnd, affinity)) ok++;
            else
            {
                int err = Marshal.GetLastWin32Error();
                Log.Error($"[MaskedDalamud] SetWindowDisplayAffinity 失敗 err={err} hwnd=0x{hwnd:X}");
            }
        }
        _appliedState = enable;
        _lastAppliedHwnd = hwnds[0];
        Log.Info($"[MaskedDalamud] WDA={(enable ? "EXCLUDEFROMCAPTURE" : "NONE")} applied to {ok}/{hwnds.Count} windows");
    }

    /// <summary>現在の Display Affinity を取得。取得失敗時は uint.MaxValue。</summary>
    public uint GetCurrentAffinity()
    {
        var hwnd = GetFfxivHwnd();
        if (hwnd == IntPtr.Zero) return uint.MaxValue;
        if (!GetWindowDisplayAffinity(hwnd, out uint affinity)) return uint.MaxValue;
        return affinity;
    }

    /// <summary>現在 FFXIV プロセスが管理者権限で起動されているか。</summary>
    public bool IsRunningElevated()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token)) return false;
            if (!GetTokenInformation(token, TokenElevation, out uint elevated, sizeof(uint), out _)) return false;
            return elevated != 0;
        }
        catch { return false; }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }
}
