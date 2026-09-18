using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

using Dalamud.Hooking;

namespace MaskedDalamud;

/// <summary>
/// [調査] ゲームが作るピクセルシェーダのバイトコードを捕まえて保存する。
///
/// 目的: KamiToolKit 製ネイティブ UI を**完全再現**するには、Atk が
/// 「ノードの値 → ピクセル」へ変換する規則そのものが要る。調べた結果:
///   ・シェーダパッケージ (.shpk) は 3D シーン用のみで UI 用は存在しない
///   ・ffxiv_dx11.exe 埋め込みの DXBC 51 個は深度/シャドウ/ポスト処理で UI 用は無い
/// つまり静的には取れない。
///
/// しかし <c>ID3D11Device::CreatePixelShader</c> には**バイトコードが引数で渡ってくる**。
/// ここを掴めば Atk の UI シェーダを丸ごと入手でき、逆アセンブルして計算式を読むのはもちろん、
/// **そのバイトコードを自前で CreatePixelShader に渡して使う**ことまでできる (定義上ピクセル一致)。
///
/// ⚠ ゲームは UI シェーダを**起動時に 1 回だけ**作る。プラグイン読み込みより前に作られていれば
/// 捕まえられないので、**この機能を ON にしたままゲームを再起動**して採取する。
///
/// フック先は「生成時」= ロード中に数十〜数百回呼ばれるだけで、毎フレーム負荷はゼロ。
/// 過去にクラッシュした OMSetRenderTargets (毎フレーム数十回) とは危険度が異なる。
/// </summary>
internal sealed unsafe class ShaderCapture : IDisposable
{
    /// <summary>ID3D11Device の vtable における CreatePixelShader の位置。
    /// IUnknown(0-2) の後、CreateBuffer(3) … CreateVertexShader(12) /
    /// CreateGeometryShader(13) / CreateGeometryShaderWithStreamOutput(14) / CreatePixelShader(15)。</summary>
    private const int VtblCreatePixelShader = 15;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreatePixelShaderDelegate(
        nint device, void* pShaderBytecode, nuint bytecodeLength, nint pClassLinkage, void** ppPixelShader);

    private Hook<CreatePixelShaderDelegate>? _hook;
    private readonly HashSet<string> _seen = new();
    private string _dir = "";

    public bool IsActive { get; private set; }
    public int Captured { get; private set; }
    public string Status { get; private set; } = "未使用";
    public string Directory => _dir;

    public bool Enable(nint devicePtr)
    {
        if (IsActive) return true;
        try
        {
            if (devicePtr == 0) { Status = "device が取得できません"; return false; }

            _dir = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "shaders");
            System.IO.Directory.CreateDirectory(_dir);

            // COM オブジェクトの vtable から関数アドレスを取る。
            var vtbl = *(nint**)devicePtr;
            var addr = vtbl[VtblCreatePixelShader];
            if (addr == 0) { Status = "vtable から関数を取得できません"; return false; }

            _hook = Plugin.GameInterop.HookFromAddress<CreatePixelShaderDelegate>(addr, Detour);
            _hook.Enable();
            IsActive = true;
            Status = "採取中 (ゲームを再起動すると起動時のシェーダも取れます)";
            Plugin.Log.Info($"[ShaderCapture] 採取開始: {_dir}");
            return true;
        }
        catch (Exception ex)
        {
            Status = $"フック失敗: {ex.Message}";
            Plugin.Log.Warning($"[ShaderCapture] フック失敗: {ex.Message}");
            return false;
        }
    }

    public void Disable()
    {
        if (!IsActive) return;
        IsActive = false;
        try { _hook?.Disable(); } catch { }
        Status = $"停止 (採取 {Captured} 個)";
        Plugin.Log.Info($"[ShaderCapture] 停止: {Captured} 個");
    }

    private int Detour(nint device, void* pShaderBytecode, nuint bytecodeLength, nint pClassLinkage, void** ppPixelShader)
    {
        // 何があってもゲームの呼び出しは通す。保存は完全に副作用として扱う。
        try
        {
            if (pShaderBytecode != null && bytecodeLength > 0 && bytecodeLength < 4 * 1024 * 1024)
                Save(pShaderBytecode, (int)bytecodeLength);
        }
        catch { }
        return _hook!.Original(device, pShaderBytecode, bytecodeLength, pClassLinkage, ppPixelShader);
    }

    private void Save(void* code, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy((nint)code, bytes, 0, length);

        // 同じシェーダが何度も作られることがあるので内容で重複排除する。
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes))[..16];
        lock (_seen)
        {
            if (!_seen.Add(hash)) return;
            if (_seen.Count > 4000) return;   // 暴走時の保険
        }

        File.WriteAllBytes(Path.Combine(_dir, $"ps_{Captured:D4}_{hash}.cso"), bytes);
        Captured++;
        Status = $"採取中 ({Captured} 個)";
    }

    public void Dispose()
    {
        try { Disable(); } catch { }
        try { _hook?.Dispose(); _hook = null; } catch { }
    }
}
