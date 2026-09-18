using System;
using System.Runtime.InteropServices;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DXGI_SWAP_EFFECT;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;
using static TerraFX.Interop.DirectX.DXGI;
using static TerraFX.Interop.DirectX.D3D11;
using static TerraFX.Interop.DirectX.D3D_DRIVER_TYPE;
using static TerraFX.Interop.DirectX.D3D_FEATURE_LEVEL;
using static TerraFX.Interop.DirectX.DirectX;

namespace MaskedDalamud;

/// <summary>
/// Phase E Beta Step 1 (ii): 完全独立な D3D11 device + context + swapchain を作成。
///
/// 前回 (Step 1.i) で FF14 の ID3D11Device を借用する実装はクラッシュした。
/// 原因として「FF14 メインレンダースレッドと Framework.Update での device 操作の競合」が
/// 強く疑われたため、本実装では D3D11CreateDevice で完全独立な device を新規生成する。
/// メモリは少々重複するが、競合リスクを構造的に排除できる。
/// </summary>
internal sealed unsafe class LayeredDx11 : IDisposable
{
    private ID3D11Device* _device;
    private ID3D11DeviceContext* _ctx;
    private IDXGISwapChain1* _swapChain;
    private ID3D11RenderTargetView* _rtv;
    private uint _width;
    private uint _height;
    private bool _initialized;
    private bool _renderErrorLogged; // 連続エラー時のログスパム抑制

    public bool IsReady => _initialized;

    public bool Init(IntPtr hwnd, uint width, uint height)
    {
        if (_initialized) return true;
        try
        {
            // ====== 完全独立な ID3D11Device を新規作成 ======
            var featureLevels = stackalloc D3D_FEATURE_LEVEL[]
            {
                D3D_FEATURE_LEVEL_11_1,
                D3D_FEATURE_LEVEL_11_0,
            };
            ID3D11Device* dev;
            ID3D11DeviceContext* ctx;
            D3D_FEATURE_LEVEL flOut;
            int hr = D3D11CreateDevice(
                null,
                D3D_DRIVER_TYPE_HARDWARE,
                HMODULE.NULL,
                0,
                featureLevels,
                2,
                D3D11_SDK_VERSION,
                &dev,
                &flOut,
                &ctx);
            if (hr != 0 || dev == null || ctx == null)
            {
                Plugin.Log.Error($"[Phase E Beta] D3D11CreateDevice failed hr=0x{hr:X}");
                return false;
            }
            _device = dev;
            _ctx = ctx;

            // ====== IDXGIFactory2 を直接作成して CreateSwapChainForHwnd ======
            IDXGIFactory2* factory = null;
            try
            {
                var iidFactory2 = IID.IID_IDXGIFactory2;
                hr = CreateDXGIFactory1(&iidFactory2, (void**)&factory);
                if (hr != 0 || factory == null)
                {
                    Plugin.Log.Error($"[Phase E Beta] CreateDXGIFactory1 failed hr=0x{hr:X}");
                    return false;
                }

                var desc = new DXGI_SWAP_CHAIN_DESC1
                {
                    Width = width,
                    Height = height,
                    Format = DXGI_FORMAT_R8G8B8A8_UNORM,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                    BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT,
                    BufferCount = 2,
                    Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                    SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD,
                    AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_UNSPECIFIED,
                    Flags = 0,
                };
                IDXGISwapChain1* sc;
                hr = factory->CreateSwapChainForHwnd(
                    (IUnknown*)_device, (HWND)(void*)hwnd, &desc, null, null, &sc);
                if (hr != 0 || sc == null)
                {
                    Plugin.Log.Error($"[Phase E Beta] CreateSwapChainForHwnd failed hr=0x{hr:X}");
                    return false;
                }
                _swapChain = sc;
                _width = width;
                _height = height;
            }
            finally
            {
                if (factory != null) factory->Release();
            }

            if (!CreateRenderTargetView())
            {
                Plugin.Log.Error("[Phase E Beta] CreateRenderTargetView failed");
                return false;
            }

            _initialized = true;
            Plugin.Log.Info($"[Phase E Beta] Independent DX11 swapchain ready: {width}x{height} on HWND 0x{hwnd:X}");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Phase E Beta] Init exception: {ex}");
            // 初期化途中で例外 → 中途半端なリソースをクリーンアップ
            DisposeInternal();
            return false;
        }
    }

    private bool CreateRenderTargetView()
    {
        if (_swapChain == null || _device == null) return false;
        ID3D11Texture2D* backbuffer = null;
        try
        {
            var iidTex = IID.IID_ID3D11Texture2D;
            if (_swapChain->GetBuffer(0, &iidTex, (void**)&backbuffer) != 0) return false;
            ID3D11RenderTargetView* rtv;
            if (_device->CreateRenderTargetView((ID3D11Resource*)backbuffer, null, &rtv) != 0) return false;
            if (_rtv != null) _rtv->Release();
            _rtv = rtv;
            return true;
        }
        finally
        {
            if (backbuffer != null) backbuffer->Release();
        }
    }

    /// <summary>1 フレーム描画 → Present。現状は単色クリアのみ (Step 1 検証用)。</summary>
    public void RenderFrame()
    {
        if (!_initialized || _ctx == null || _rtv == null || _swapChain == null) return;
        try
        {
            // 青クリア (検証用に判別しやすい色)
            float* color = stackalloc float[4] { 0.10f, 0.30f, 0.55f, 1.0f };
            _ctx->ClearRenderTargetView(_rtv, color);
            // 独立 device かつ独立 swapchain なので Present(1, 0) は FF14 側と競合しない
            _swapChain->Present(1, 0);
        }
        catch (Exception ex)
        {
            if (!_renderErrorLogged)
            {
                Plugin.Log.Warning($"[Phase E Beta] RenderFrame exception (will be silenced): {ex.Message}");
                _renderErrorLogged = true;
            }
        }
    }

    public void Dispose() => DisposeInternal();

    private void DisposeInternal()
    {
        try
        {
            if (_rtv != null) { _rtv->Release(); _rtv = null; }
            if (_swapChain != null) { _swapChain->Release(); _swapChain = null; }
            // 独立 device / context は自前で解放する (FF14 借用ではない)
            if (_ctx != null) { _ctx->Release(); _ctx = null; }
            if (_device != null) { _device->Release(); _device = null; }
            if (_initialized) Plugin.Log.Info("[Phase E Beta] Independent DX11 disposed");
            _initialized = false;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Phase E Beta] Dispose exception: {ex.Message}");
        }
    }

    /// <summary>TerraFX が公開していない IID 定数を別途定義 (GUID は標準のもの)。</summary>
    private static class IID
    {
        public static readonly Guid IID_IDXGIFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
        public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    }
}
