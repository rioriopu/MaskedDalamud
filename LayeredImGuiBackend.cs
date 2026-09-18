using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MaskedDalamud;

/// <summary>
/// Phase E A — Step A.1 (skeleton):
/// 独立 ImGui Context を管理する skeleton。
///
/// 本クラスの最終形は「Dalamud の ImGui Context とは別の独立 Context を持ち、
/// 自前の DX11 backend で LayeredDx11 の swapchain にレンダリングする」こと。
///
/// 現在は Step A.1 として:
///   - 独立 ImGui Context を作成
///   - グローバル ImGui の Current Context 切替 (NewFrame → Render)
///   - ImDrawData を取得するところまで
///   - 実際の描画 (頂点バッファ、シェーダ、フォントテクスチャ等) は Step A.2 で実装
///
/// Step A.2 で追加予定: VertexShader / PixelShader / InputLayout / VertexBuffer /
///   IndexBuffer / ConstantBuffer / FontTexture / SamplerState / RasterizerState /
///   BlendState / DepthStencilState
/// </summary>
internal sealed unsafe class LayeredImGuiBackend : IDisposable
{
    private ImGuiContextPtr _context;
    private ImGuiContextPtr _previousContext;
    private bool _initialized;
    private uint _width;
    private uint _height;

    public bool IsReady => _initialized;

    /// <summary>独立 ImGui Context を作成して必要な IO 設定を行う。</summary>
    public bool Init(uint width, uint height)
    {
        if (_initialized) return true;
        try
        {
            _width = width;
            _height = height;

            // 現在の Context (= Dalamud 本来の Context) を保存しておき、後で復元する
            var dalamudContext = ImGui.GetCurrentContext();

            // 独立 Context を新規作成
            _context = ImGui.CreateContext(null);
            if (_context.IsNull)
            {
                Plugin.Log.Error("[Phase E A] ImGui.CreateContext failed");
                return false;
            }

            // 一旦こちらに切替えて IO 初期化
            ImGui.SetCurrentContext(_context);
            var io = ImGui.GetIO();
            io.DisplaySize = new Vector2(width, height);
            io.DeltaTime = 1f / 60f;
            // DX11 backend が未実装の段階では BackendFlags は何も立てない
            // (RendererHasVtxOffset 等は Step A.2 で立てる)

            // Dalamud の Context に戻して安全な状態にする
            ImGui.SetCurrentContext(dalamudContext);

            _initialized = true;
            Plugin.Log.Info($"[Phase E A] Independent ImGui Context created: {width}x{height} (ctx=0x{((IntPtr)_context.Handle).ToInt64():X})");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Phase E A] Init exception: {ex}");
            return false;
        }
    }

    /// <summary>
    /// 独立 Context での 1 フレーム生成サイクル。
    /// 現状は NewFrame → 自前 UI draw → Render → ImDrawData 取得まで。
    /// 実際の DX11 描画は Step A.2 で追加。
    /// </summary>
    public void RenderFrame()
    {
        if (!_initialized) return;
        try
        {
            // Dalamud Context を保存して独立 Context に切替
            _previousContext = ImGui.GetCurrentContext();
            ImGui.SetCurrentContext(_context);

            var io = ImGui.GetIO();
            io.DisplaySize = new Vector2(_width, _height);
            io.DeltaTime = 1f / 60f;

            ImGui.NewFrame();
            DrawIndependentUi();
            ImGui.Render();

            // ImDrawData を取得 (Step A.2 でこれを DX11 backend に渡して描画)
            // var drawData = ImGui.GetDrawData();
            // → 次セッションで RenderDrawData() 実装

            // 必ず元の Dalamud Context に戻す (これを忘れると Dalamud の ImGui 描画が壊れる)
            ImGui.SetCurrentContext(_previousContext);
        }
        catch (Exception ex)
        {
            // 例外時も必ず Context を復元する
            try { ImGui.SetCurrentContext(_previousContext); } catch { }
            Plugin.Log.Warning($"[Phase E A] RenderFrame exception: {ex.Message}");
        }
    }

    /// <summary>独立 Context で描画する内容 (skeleton 段階の試験 UI)。</summary>
    private static void DrawIndependentUi()
    {
        ImGui.SetNextWindowPos(new Vector2(10, 10), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(340, 100), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Phase E A — Independent ImGui##phaseEAroot",
            ImGuiWindowFlags.NoCollapse))
        {
            ImGui.TextColored(new Vector4(0.5f, 1f, 0.5f, 1f), "Phase E A — Step A.1");
            ImGui.TextWrapped(
                "独立 ImGui Context が動作中。\n" +
                "Step A.2 で DX11 backend を実装 → 独立 swapchain に描画されるようになります。");
        }
        ImGui.End();
    }

    public void Dispose()
    {
        if (!_initialized) return;
        try
        {
            // 安全のため Dalamud Context に戻してから独立 Context を破棄
            try { ImGui.SetCurrentContext(_previousContext); } catch { }
            if (!_context.IsNull)
            {
                ImGui.DestroyContext(_context);
                Plugin.Log.Info("[Phase E A] Independent ImGui Context destroyed");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Phase E A] Dispose exception: {ex.Message}");
        }
        finally
        {
            _initialized = false;
        }
    }
}
