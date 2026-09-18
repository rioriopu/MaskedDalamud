using System;
using System.Runtime.InteropServices;

namespace MaskedDalamud;

/// <summary>
/// [試験] 排他フルスクリーンを「ボーダレスウィンドウ全画面」へ変換するヘルパー。
///
/// 排他FSは DWM をバイパスするため手元UI (DComp/レイヤード窓) が映らない。ゲームを
/// ボーダレス窓に変換すれば DWM 合成に戻り、既存の手元UI 方式がそのまま動く。
///
/// 安全方針:
///  - swapchain の vtable フックは行わない (GShade/ReShade と競合し暗転する実績があるため)。
///    呼び出し側が `SetFullscreenState(FALSE)` を済ませた後に、本クラスは Win32 の
///    ウィンドウスタイル変更だけを行う。
///  - 初回適用時に元のスタイル/矩形を退避し、Restore() で完全復元する。
///  - すべて best-effort (失敗しても例外を投げない)。
/// </summary>
internal sealed class BorderlessForcer
{
    private bool _applied;
    private IntPtr _appliedHwnd;
    private int _savedStyle;
    private int _savedExStyle;
    private RECT _savedRect;

    public bool IsApplied => _applied;

    /// <summary>hwnd をモニタ全体のボーダレス窓に整形する (初回は元状態を退避)。</summary>
    public void Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            if (!_applied)
            {
                _savedStyle = GetWindowLong(hwnd, GWL_STYLE);
                _savedExStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                GetWindowRect(hwnd, out _savedRect);
                _appliedHwnd = hwnd;
            }

            // モニタ全体の矩形を取得。
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return;
            int x = mi.rcMonitor.left, y = mi.rcMonitor.top;
            int w = mi.rcMonitor.right - mi.rcMonitor.left;
            int h = mi.rcMonitor.bottom - mi.rcMonitor.top;

            // ボーダレス: WS_POPUP のみ + 装飾系を除去。
            int style = (_savedStyle & ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX
                          | WS_MAXIMIZEBOX | WS_SYSMENU | WS_BORDER | WS_DLGFRAME)) | WS_POPUP | WS_VISIBLE;
            SetWindowLong(hwnd, GWL_STYLE, style);
            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);

            if (!_applied)
            {
                _applied = true;
                Plugin.Log.Info($"[Borderless] 排他FS→ボーダレス変換 適用 {w}x{h}");
            }
        }
        catch (Exception ex) { Plugin.Log.Warning($"[Borderless] Apply 例外: {ex.Message}"); }
    }

    /// <summary>退避していた元のスタイル/矩形へ復元する。</summary>
    public void Restore()
    {
        if (!_applied || _appliedHwnd == IntPtr.Zero) { _applied = false; return; }
        try
        {
            SetWindowLong(_appliedHwnd, GWL_STYLE, _savedStyle);
            SetWindowLong(_appliedHwnd, GWL_EXSTYLE, _savedExStyle);
            SetWindowPos(_appliedHwnd, IntPtr.Zero,
                _savedRect.left, _savedRect.top,
                _savedRect.right - _savedRect.left, _savedRect.bottom - _savedRect.top,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
            Plugin.Log.Info("[Borderless] 元のウィンドウスタイルへ復元");
        }
        catch (Exception ex) { Plugin.Log.Warning($"[Borderless] Restore 例外: {ex.Message}"); }
        finally { _applied = false; _appliedHwnd = IntPtr.Zero; }
    }

    private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    private const int WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
    private const int WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_SYSMENU = 0x00080000, WS_BORDER = 0x00800000, WS_DLGFRAME = 0x00400000;
    private const int WS_POPUP = unchecked((int)0x80000000), WS_VISIBLE = 0x10000000;
    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int idx, int val);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
}
