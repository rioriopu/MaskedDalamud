using System;
using System.Reflection;

namespace MaskedDalamud;

/// <summary>
/// Method A (RTV リダイレクト) 用の Dalamud 内部 reflection アクセサ。
///
/// 設計方針 (Phase F / 安全フォールバック):
///   - Dalamud 15.0.0.7 の内部構造を decompile で確認した private シンボルへ
///     reflection で到達する。
///   - 1 段でも取得に失敗したら <see cref="Available"/> を false に倒し、
///     リダイレクトは一切行わない = Dalamud は従来通りゲーム backbuffer に
///     描画する (UI は配信に映るが、クラッシュ・黒残留は発生しない)。
///   - 本クラスは「読み取り + 検証」のみ。実際の swapchain 差替は Stage 2 で
///     別クラスが本アクセサ経由で行う。
///
/// 参照: memory/reference_dalamud_imgui_pipeline.md
/// </summary>
internal sealed unsafe class DalamudImGuiInternals
{
    private const BindingFlags InstFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>全 reflection 段が成功した場合のみ true。false の間は呼び出し側は
    /// 何もしてはならない (フォールバック = 通常描画)。</summary>
    public bool Available { get; private set; }

    /// <summary>取得失敗時の原因 (ログ/診断表示用)。</summary>
    public string? FailReason { get; private set; }

    // 解決済みハンドル群 (Available == true のときのみ有効)
    public object? InterfaceManager { get; private set; }
    public object? Backend { get; private set; }       // Dx11Win32Backend
    public object? Renderer { get; private set; }      // Dx11Renderer
    public object? MainViewport { get; private set; }  // Dx11Renderer+ViewportData

    public IntPtr GameDevicePtr { get; private set; }        // ID3D11Device*
    public IntPtr GameDeviceContextPtr { get; private set; } // ID3D11DeviceContext*
    public IntPtr GameSwapChainPtr { get; private set; }     // IDXGISwapChain*

    // Stage 2 で使う差替用メンバ
    public FieldInfo? MainViewportSwapChainField { get; private set; }        // ComPtr<IDXGISwapChain>
    public FieldInfo? MainViewportRenderTargetField { get; private set; }     // ComPtr<ID3D11Texture2D>
    public FieldInfo? MainViewportRenderTargetViewField { get; private set; } // ComPtr<ID3D11RenderTargetView>
    public MethodInfo? MainViewportResetBuffersMethod { get; private set; }

    // teardown 復元用: Dalamud が追跡している「生きた」ゲーム swapchain。
    // Enable 時に保存した古いポインタを復元すると、その間に FFXIV が swapchain
    // を作り直していた場合 stale となりデバイス除去を招く。これは毎回最新値。
    private PropertyInfo? _gameDeviceSwapChainProp;

    /// <summary>現在のゲーム swapchain (IDXGISwapChain*) を毎回最新で取得。
    /// 解決失敗時 IntPtr.Zero。</summary>
    public IntPtr GetLiveGameSwapChain()
    {
        try
        {
            if (_gameDeviceSwapChainProp == null) return IntPtr.Zero;
            var boxed = _gameDeviceSwapChainProp.GetValue(null);
            if (boxed is System.Reflection.Pointer p)
                return (IntPtr)System.Reflection.Pointer.Unbox(p);
            return IntPtr.Zero;
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>
    /// reflection チェーンを 1 から解決する。例外は一切外に漏らさず、
    /// 失敗時は Available=false / FailReason に理由を残して安全に返る。
    /// 何度呼んでも安全 (再解決)。
    /// </summary>
    public void Resolve()
    {
        Available = false;
        FailReason = null;
        try
        {
            // Dalamud 本体アセンブリ (IDalamudPlugin が定義されている)
            var dalamudAsm = typeof(Dalamud.Plugin.IDalamudPlugin).Assembly;

            // ---- InterfaceManager (Service<T> ロケータ経由) ----
            var imType = dalamudAsm.GetType("Dalamud.Interface.Internal.InterfaceManager");
            if (imType == null) { Fail("InterfaceManager 型が見つからない"); return; }

            var serviceOpen = dalamudAsm.GetType("Dalamud.Service`1");
            if (serviceOpen == null) { Fail("Service`1 型が見つからない"); return; }
            var serviceClosed = serviceOpen.MakeGenericType(imType);

            // GetNullable() を優先 (Get() は未初期化時にブロックしうるため)
            object? im = null;
            var getNullable = serviceClosed.GetMethod(
                "GetNullable", BindingFlags.Public | BindingFlags.Static);
            if (getNullable != null)
            {
                // 既定引数 (ExceptionPropagationMode) は Type.Missing で省略
                var ps = getNullable.GetParameters();
                var args = new object?[ps.Length];
                for (int i = 0; i < ps.Length; i++) args[i] = Type.Missing;
                im = getNullable.Invoke(null, args);
            }
            if (im == null)
            {
                var get = serviceClosed.GetMethod(
                    "Get", BindingFlags.Public | BindingFlags.Static);
                im = get?.Invoke(null, null);
            }
            if (im == null) { Fail("InterfaceManager インスタンス取得失敗"); return; }
            InterfaceManager = im;

            // ---- Backend (Dx11Win32Backend) ----
            var backend = imType.GetProperty("Backend", InstFlags)?.GetValue(im);
            if (backend == null) { Fail("InterfaceManager.Backend が null (まだ未初期化の可能性)"); return; }
            Backend = backend;
            var backendType = backend.GetType();

            // ---- FFXIV の device / context / swapchain ポインタ ----
            GameDevicePtr        = ReadPointerProp(backendType, backend, "Device");
            GameDeviceContextPtr = ReadPointerProp(backendType, backend, "DeviceContext");
            GameSwapChainPtr     = ReadPointerProp(backendType, backend, "SwapChain");
            if (GameDevicePtr == IntPtr.Zero)     { Fail("Backend.Device ポインタが null"); return; }
            if (GameSwapChainPtr == IntPtr.Zero)  { Fail("Backend.SwapChain ポインタが null"); return; }

            // ---- Renderer (Dx11Renderer) ----
            var renderer = backendType.GetProperty("Renderer", InstFlags)?.GetValue(backend);
            if (renderer == null) { Fail("Backend.Renderer が null"); return; }
            Renderer = renderer;
            var rendererType = renderer.GetType();

            // ---- mainViewport (private readonly field) ----
            var mvField = rendererType.GetField("mainViewport", InstFlags);
            if (mvField == null) { Fail("Dx11Renderer.mainViewport フィールドが見つからない"); return; }
            var mv = mvField.GetValue(renderer);
            if (mv == null) { Fail("mainViewport が null"); return; }
            MainViewport = mv;
            var mvType = mv.GetType();

            // ---- Stage 2 で使う差替メンバを解決だけしておく ----
            MainViewportSwapChainField = mvType.GetField("swapChain", InstFlags);
            if (MainViewportSwapChainField == null) { Fail("ViewportData.swapChain フィールドが見つからない"); return; }
            MainViewportRenderTargetField = mvType.GetField("renderTarget", InstFlags);
            if (MainViewportRenderTargetField == null) { Fail("ViewportData.renderTarget フィールドが見つからない"); return; }
            MainViewportRenderTargetViewField = mvType.GetField("renderTargetView", InstFlags);
            if (MainViewportRenderTargetViewField == null) { Fail("ViewportData.renderTargetView フィールドが見つからない"); return; }
            MainViewportResetBuffersMethod = mvType.GetMethod(
                "ResetBuffers", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (MainViewportResetBuffersMethod == null) { Fail("ViewportData.ResetBuffers メソッドが見つからない"); return; }

            // ---- SwapChainHelper.GameDeviceSwapChain (static, 生きた game swapchain) ----
            // 非必須: 見つからなければ teardown は空復元にフォールバック。
            try
            {
                var sch = dalamudAsm.GetType("Dalamud.Interface.Internal.SwapChainHelper");
                _gameDeviceSwapChainProp = sch?.GetProperty("GameDeviceSwapChain",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }
            catch { _gameDeviceSwapChainProp = null; }

            // 全段成功
            Available = true;
        }
        catch (Exception ex)
        {
            Fail($"reflection 解決中に例外: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>pointer 戻り値のプロパティを reflection で読み IntPtr 化する。
    /// pointer プロパティは System.Reflection.Pointer でボックスされて返る。</summary>
    private static IntPtr ReadPointerProp(Type t, object instance, string propName)
    {
        try
        {
            var pi = t.GetProperty(propName, InstFlags);
            if (pi == null) return IntPtr.Zero;
            var boxed = pi.GetValue(instance);
            if (boxed == null) return IntPtr.Zero;
            if (boxed is System.Reflection.Pointer p)
                return (IntPtr)System.Reflection.Pointer.Unbox(p);
            // 念のため整数型でも来たら拾う
            if (boxed is IntPtr ip) return ip;
            if (boxed is UIntPtr up) return (IntPtr)(long)(ulong)up;
            return IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private void Fail(string reason)
    {
        Available = false;
        FailReason = reason;
        Plugin.Log.Warning($"[Method A] reflection 解決失敗 → フォールバック (リダイレクト無効): {reason}");
    }

    /// <summary>診断結果を 1 行ずつ返す (チャット/ログ表示用)。差替は一切行わない。</summary>
    public void LogDiagnostics()
    {
        Plugin.Log.Info("[Method A] ===== Dalamud 内部 reflection 診断 =====");
        Plugin.Log.Info($"[Method A] InterfaceManager : {(InterfaceManager != null ? "OK" : "—")}");
        Plugin.Log.Info($"[Method A] Backend          : {(Backend?.GetType().Name ?? "—")}");
        Plugin.Log.Info($"[Method A] Renderer         : {(Renderer?.GetType().Name ?? "—")}");
        Plugin.Log.Info($"[Method A] mainViewport     : {(MainViewport?.GetType().FullName ?? "—")}");
        Plugin.Log.Info($"[Method A] GameDevice       : 0x{GameDevicePtr.ToInt64():X}");
        Plugin.Log.Info($"[Method A] GameDeviceContext: 0x{GameDeviceContextPtr.ToInt64():X}");
        Plugin.Log.Info($"[Method A] GameSwapChain    : 0x{GameSwapChainPtr.ToInt64():X}");
        Plugin.Log.Info($"[Method A] swapChain field  : {(MainViewportSwapChainField != null ? "OK" : "—")}");
        Plugin.Log.Info($"[Method A] ResetBuffers()   : {(MainViewportResetBuffersMethod != null ? "OK" : "—")}");
        Plugin.Log.Info($"[Method A] => Available     : {Available}{(Available ? "" : $" (理由: {FailReason})")}");
    }
}
