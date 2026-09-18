using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MaskedDalamud;

/// <summary>
/// Phase E プロトタイプ: 独立した HWND を作成し、その HWND だけに
/// WDA_EXCLUDEFROMCAPTURE を適用する Layered Overlay。
///
/// 試験段階の Alpha 版:
///   - 独立 HWND の作成 + WDA 適用が正しく動作するかの基盤検証
///   - GDI で簡単テキストを描画してユーザが本人画面で視認できることを確認
///   - 配信側 (OBS / Discord) では HWND ごと除外されていることを確認
///
/// 次段階 (Phase E Beta) で予定:
///   - ImGui Context を別途作成し、独立 swapchain (DX11) でレンダリング
///   - Dalamud の Draw event を購読して、すべての Plugin UI をこの HWND に集約
///   - 元の game swapchain には Dalamud UI を出さない (本来の Layered Window 分離)
/// </summary>
internal sealed class LayeredOverlay : IDisposable
{
    private const string ClassName = "MaskedDalamudLayeredOverlay";
    private const int OverlayWidth = 360;
    private const int OverlayHeight = 120;

    private IntPtr _hwnd = IntPtr.Zero;
    private bool _classRegistered;
    private WndProcDelegate? _wndProcDelegate; // GC 防止のため保持
    private IntPtr _hInstance = IntPtr.Zero;

    /// <summary>Phase E Beta Step 1: DX11 swapchain + 青クリア render loop。
    /// 非 null のとき DX11 描画が有効 (GDI 描画は WM_PAINT で行うが Present の方が優先される)。</summary>
    public LayeredDx11? Dx11 { get; private set; }

    /// <summary>Phase E A Step A.1: 独立 ImGui Context skeleton。
    /// Step A.2 で自前 DX11 backend と接続される。</summary>
    public LayeredImGuiBackend? ImGuiBackend { get; private set; }

    public IntPtr Handle => _hwnd;
    public bool IsOpen => _hwnd != IntPtr.Zero;

    public bool Open()
    {
        if (_hwnd != IntPtr.Zero) return true;
        try
        {
            _hInstance = GetModuleHandle(null);
            RegisterClassIfNeeded();

            // WS_EX_LAYERED は DX11 swapchain と非互換 (Win10 旧仕様)。
            // Step 1 (ii) で DX11 を再有効化したため再び除外する。
            // タイトルバー (WS_CAPTION) + リサイズ枠 (WS_THICKFRAME) を付与してドラッグ可能に。
            _hwnd = CreateWindowExW(
                WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
                ClassName,
                "Masked Dalamud Layered (DX11)",
                WS_VISIBLE | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU,
                100, 100, OverlayWidth, OverlayHeight,
                IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                Plugin.Log.Error($"[Phase E] CreateWindowExW failed err={err}");
                return false;
            }

            // DX11 が無効化された場合のフォールバック用に LWA_ALPHA を設定
            // (Phase E Alpha モードでも動作するように残す。DX11 がアクティブな間は無視される)

            // Phase E の本丸: この HWND だけに WDA_EXCLUDEFROMCAPTURE を適用
            if (!SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE))
            {
                int err = Marshal.GetLastWin32Error();
                Plugin.Log.Warning($"[Phase E] SetWindowDisplayAffinity failed err={err}");
            }
            else
            {
                Plugin.Log.Info($"[Phase E] Layered HWND opened: 0x{_hwnd:X} (WDA=EXCLUDEFROMCAPTURE)");
            }

            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            UpdateWindow(_hwnd);

            // Phase E Beta Step 1 (ii): 独立 ID3D11Device で再試行
            // (Step 1.i の FF14 device 借用版はクラッシュしたため、ここでは
            //  D3D11CreateDevice で完全独立な device を新規作成する)
            var dx = new LayeredDx11();
            if (dx.Init(_hwnd, OverlayWidth, OverlayHeight))
            {
                Dx11 = dx;

                // Phase E A Step A.1 (LayeredImGuiBackend Init) は実機でクラッシュ + 黒画面残留を
                // 引き起こしたため緊急撤去。Step A.2 で UI スレッド側設計に再構成してから再導入する。
                // var im = new LayeredImGuiBackend();
                // if (im.Init(OverlayWidth, OverlayHeight)) ImGuiBackend = im;
                // else im.Dispose();
            }
            else
            {
                Plugin.Log.Warning("[Phase E Beta] DX11 init failed — GDI fallback で表示します");
                dx.Dispose();
                // フォールバック: WS_EX_LAYERED 相当の半透明を後から付与
                SetLayeredWindowAttributes(_hwnd, 0, 200, LWA_ALPHA);
            }
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Phase E] Open() exception: {ex}");
            return false;
        }
    }

    public void Close()
    {
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            ImGuiBackend?.Dispose();
            ImGuiBackend = null;
            Dx11?.Dispose();
            Dx11 = null;
            DestroyWindow(_hwnd);
            Plugin.Log.Info("[Phase E] Layered HWND closed");
        }
        catch (Exception ex) { Plugin.Log.Warning($"[Phase E] Close() exception: {ex.Message}"); }
        _hwnd = IntPtr.Zero;
    }

    public void Dispose() => Close();

    private void RegisterClassIfNeeded()
    {
        if (_classRegistered) return;
        _wndProcDelegate = WndProc;

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = _hInstance,
            hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
            hbrBackground = (IntPtr)(COLOR_WINDOW + 1),
            lpszClassName = ClassName,
        };
        if (RegisterClassExW(ref wc) == 0)
        {
            int err = Marshal.GetLastWin32Error();
            // ERROR_CLASS_ALREADY_EXISTS == 1410 は OK (Plugin リロードで再登録時)
            if (err != 1410)
                Plugin.Log.Warning($"[Phase E] RegisterClassExW failed err={err}");
        }
        _classRegistered = true;
    }

    /// <summary>独立 HWND のメッセージハンドラ。WM_PAINT で GDI テキスト描画。</summary>
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                {
                    var ps = new PAINTSTRUCT();
                    var hdc = BeginPaint(hwnd, ref ps);
                    try
                    {
                        var rect = new RECT { left = 0, top = 0, right = 360, bottom = 120 };
                        // 背景クリア (半透明青系)
                        var brush = CreateSolidBrush(0x00553311);
                        FillRect(hdc, ref rect, brush);
                        DeleteObject(brush);

                        // テキスト描画
                        SetBkMode(hdc, TRANSPARENT_MODE);
                        SetTextColor(hdc, 0x00FFFFFF);
                        var msgText = "[MaskedDalamud Phase E]\nLayered Window test\nWDA=EXCLUDEFROMCAPTURE\n配信に映らないことを確認してください";
                        var sb = new StringBuilder(msgText);
                        DrawTextW(hdc, sb, msgText.Length, ref rect,
                            DT_LEFT | DT_TOP | DT_WORDBREAK);
                    }
                    finally
                    {
                        EndPaint(hwnd, ref ps);
                    }
                    return IntPtr.Zero;
                }
            case WM_DESTROY:
                return IntPtr.Zero;
            default:
                return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    // ============ Win32 P/Invoke 群 ============
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_CAPTION = 0x00C00000;     // タイトルバー
    private const uint WS_THICKFRAME = 0x00040000;  // リサイズ枠
    private const uint WS_SYSMENU = 0x00080000;     // システムメニュー (× ボタン)
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private const uint CS_HREDRAW = 0x0002;
    private const uint CS_VREDRAW = 0x0001;
    private const int COLOR_WINDOW = 5;
    private const int IDC_ARROW = 32512;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_DESTROY = 0x0002;
    private const uint DT_LEFT = 0x00000000;
    private const uint DT_TOP = 0x00000000;
    private const uint DT_WORDBREAK = 0x00000010;
    private const int TRANSPARENT_MODE = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);
    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, ref PAINTSTRUCT lpPaint);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT lpPaint);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hdc, StringBuilder lpchText, int nCount, ref RECT lpRect, uint uFormat);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint crColor);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int iBkMode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr hdc, uint color);
}
