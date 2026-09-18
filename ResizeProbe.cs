using System;
using System.Reflection;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;

namespace MaskedDalamud;

/// <summary>
/// [診断] Dalamud の InterfaceManager.ResizeBuffers イベント (public) を購読し、
/// ゲーム窓リサイズの「その瞬間」(= 実 ResizeBuffers が走る直前) の状態をログする。
///
/// 目的: `invalid call to resizeBuffers` (= DXGI_ERROR_INVALID_CALL = backbuffer に
/// 未解放参照が残存) の原因切り分け。オーバーレイ無効中でも購読が生きるので、
///   - MaskedDalamud 未有効化時のリサイズ
///   - 有効化→無効化後のリサイズ
/// で backbuffer 残存参照数を比較でき、「有効化が永続参照を作るか」を判定できる。
/// あわせて swapchain サイズ / 窓クライアントサイズ / オーバーレイ側サイズを出し、
/// 「ゲーム窓サイズと HUD 側サイズの初期化ズレ」仮説も検証する。
/// </summary>
internal sealed unsafe class ResizeProbe : IDisposable
{
    private readonly Plugin _plugin;
    private readonly DalamudImGuiInternals _internals = new();
    private object? _im;
    private EventInfo? _ev;
    private Delegate? _handler;
    private int _count;

    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    public ResizeProbe(Plugin plugin) => _plugin = plugin;

    public bool Subscribe()
    {
        try
        {
            _internals.Resolve();
            if (!_internals.Available || _internals.InterfaceManager == null)
            {
                Plugin.Log.Warning($"[ResizeProbe] reflection 未解決: {_internals.FailReason}");
                return false;
            }
            _im = _internals.InterfaceManager;
            _ev = _im.GetType().GetEvent("ResizeBuffers",
                BindingFlags.Public | BindingFlags.Instance);
            if (_ev == null) { Plugin.Log.Warning("[ResizeProbe] ResizeBuffers イベント未発見"); return false; }

            // event Action? ResizeBuffers → ハンドラ型は Action
            _handler = Delegate.CreateDelegate(_ev.EventHandlerType!, this,
                typeof(ResizeProbe).GetMethod(nameof(OnResize),
                    BindingFlags.NonPublic | BindingFlags.Instance)!);
            _ev.AddEventHandler(_im, _handler);
            Plugin.Log.Info("[ResizeProbe] ResizeBuffers イベント購読開始");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[ResizeProbe] 購読失敗: {ex.Message}");
            return false;
        }
    }

    // Dalamud 検出器のイベント発火 = ゲーム swapchain リサイズが今から走る直前。
    private void OnResize()
    {
        try
        {
            _count++;
            var scPtr = _internals.GetLiveGameSwapChain();
            string scInfo = "swapchain=null";
            uint refs = 0;
            if (scPtr != IntPtr.Zero)
            {
                var sc = (IDXGISwapChain*)scPtr;
                DXGI_SWAP_CHAIN_DESC d;
                if (sc->GetDesc(&d) == 0)
                    scInfo = $"swapchain={d.BufferDesc.Width}x{d.BufferDesc.Height} fmt={d.BufferDesc.Format}";
                ID3D11Texture2D* bb; var iid = IID_ID3D11Texture2D;
                if (sc->GetBuffer(0, &iid, (void**)&bb) == 0 && bb != null)
                    refs = bb->Release();   // GetBuffer ぶんを返した後の「他に握っている数」
            }

            // どのオーバーレイが active か + そのサイズ
            string ov = "none";
            if (_plugin.dcompOverlay?.IsActive == true) ov = "DComp";
            else if (_plugin.gdiScrub?.IsActive == true) ov = "GdiScrub";
            else if (_plugin.scrub?.IsActive == true) ov = "CPUScrub";

            // ゲーム窓クライアントサイズ (HUD 位置基準) も取り、swapchain と一致するか確認
            string cli = "client=?";
            var hwnd = GetForegroundOrFfxiv();
            if (hwnd != IntPtr.Zero && GetClientRect(hwnd, out RECT rc))
                cli = $"client={rc.right - rc.left}x{rc.bottom - rc.top}";

            if (_plugin.cfg.debugEnabled)   // 診断ログはデバッグ有効時のみ (通常はログを汚さない)
                Plugin.Log.Info($"[ResizeProbe] #{_count} 発火: {scInfo} backbuffer残存参照={refs} overlay={ov} {cli}");
        }
        catch (Exception ex) { Plugin.Log.Warning($"[ResizeProbe] OnResize 例外: {ex.Message}"); }
    }

    private IntPtr GetForegroundOrFfxiv()
    {
        try { return GetForegroundWindow(); } catch { return IntPtr.Zero; }
    }

    public void Dispose()
    {
        try { if (_ev != null && _im != null && _handler != null) _ev.RemoveEventHandler(_im, _handler); }
        catch { }
        _ev = null; _im = null; _handler = null;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
