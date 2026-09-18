using System;
using System.Runtime.InteropServices;
using System.Text;

using TerraFX.Interop.DirectX;

namespace MaskedDalamud;

/// <summary>
/// [試験] 差分キャプチャ (capture mode 2) 用のフルスクリーン シェーダ処理。
///
/// 背景: BossModReborn 7.5.1.9 (2026-08 の DX11 レンダラー移行) 以降、レーダー等の描画は
/// ImGui の頂点データではなく <c>ImDrawList.AddCallback</c> で積んだ**ネイティブ D3D11 直描き**
/// になった。しかもコールバックは <c>RemovePacket</c> で「一度きり消費」されるため、
/// 従来の「DrawData をオーバーレイへリプレイ」方式では 2 回目の描画で何も出ない
/// (= 手元からレーダーが消える) 問題が起きる。
///
/// 本クラスは「誰がどう描いたか」を問わず、**描き終わった backbuffer と描く前のクリーン画の
/// 差分**を UI レイヤーとして抽出する。差分がゼロの画素は元の色と同一なので透明にしても
/// 見た目は完全一致し、アーティファクトが原理的に発生しない。
///
/// 出力は premultiplied alpha 前提の DComp オーバーレイに合わせ、UI 画素は a=1 で出す
/// (a=1 なら premultiply しても色は不変)。
/// </summary>
internal sealed unsafe class DCompDiffCapture : IDisposable
{
    private ID3D11VertexShader* _vs;
    private ID3D11PixelShader* _ps;
    private ID3D11PixelShader* _psMirror;
    private ID3D11PixelShader* _psCopy;
    private ID3D11PixelShader* _psErase;
    private ID3D11PixelShader* _psTest;
    private ID3D11BlendState* _blendOver;
    private ID3D11RasterizerState* _rasterNoCull;
    private bool _failed;

    public string? LastError { get; private set; }
    public bool Ready => _vs != null && _ps != null && _psMirror != null && !_failed;

    // 頂点バッファ不要のフルスクリーン三角形 (SV_VertexID から生成)。
    private const string VsSrc = @"
struct VSOut { float4 pos : SV_Position; };
VSOut main(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}";

    // cur (UI 描画後) と clean (描画前) を Load で厳密比較し、差があれば UI として不透明出力。
    // Load = テクセル直読みのためサンプラ不要・補間なしで厳密判定できる。
    private const string PsSrc = @"
Texture2D<float4> CurTex   : register(t0);
Texture2D<float4> CleanTex : register(t1);
float4 main(float4 pos : SV_Position) : SV_Target
{
    int3 p = int3((int)pos.x, (int)pos.y, 0);
    float4 cur = CurTex.Load(p);
    float4 cln = CleanTex.Load(p);
    float3 d = abs(cur.rgb - cln.rgb);
    // しきい値 ~1/255。量子化誤差だけの画素を UI と誤検出しないための最小値。
    if ((d.r + d.g + d.b) > 0.0039)
        return float4(cur.rgb, 1.0);
    return float4(0, 0, 0, 0);
}";

    // 全画面ミラー用。ゲーム backbuffer のアルファは不定 (0 のことが多く、premultiplied alpha の
    // DComp では透明になって何も映らない) ため、a=1 を強制して取り込む。
    private const string PsMirrorSrc = @"
Texture2D<float4> CurTex : register(t0);
float4 main(float4 pos : SV_Position) : SV_Target
{
    int3 p = int3((int)pos.x, (int)pos.y, 0);
    return float4(CurTex.Load(p).rgb, 1.0);
}";

    // 保持したピクセルをそのまま (アルファ込みで) 出す。矩形合成用。
    private const string PsCopySrc = @"
Texture2D<float4> SrcTex : register(t0);
float4 main(float4 pos : SV_Position) : SV_Target
{
    int3 p = int3((int)pos.x, (int)pos.y, 0);
    return SrcTex.Load(p);
}";

    // UI の画素だけを「UI が無い絵」で塗り消す。差が無い画素は元のまま残すので、
    // 矩形の背景に継ぎ目やゴーストが出ない (矩形を丸ごと置換すると二重像に見える)。
    private const string PsEraseSrc = @"
Texture2D<float4> CurTex   : register(t0);
Texture2D<float4> CleanTex : register(t1);
float4 main(float4 pos : SV_Position) : SV_Target
{
    int3 p = int3((int)pos.x, (int)pos.y, 0);
    float4 cur = CurTex.Load(p);
    float4 cln = CleanTex.Load(p);
    float3 d = abs(cur.rgb - cln.rgb);
    if ((d.r + d.g + d.b) > 0.0039)
        return float4(cln.rgb, cur.a);   // UI があった画素 → 背景で塗り消す
    return cur;                          // それ以外はそのまま (継ぎ目を作らない)
}";

    // [診断] 矩形の書き込み経路そのものを検証するためのテスト塗り (マゼンタ)。
    // これが配信に出れば「書き込みは効いている＝差分/退避側の問題」と切り分けられる。
    private const string PsTestSrc = @"
float4 main(float4 pos : SV_Position) : SV_Target { return float4(1,0,1,1); }";

    /// <summary>シェーダとステートを遅延生成する。失敗しても呼び出し側は従来方式へ落とせる。</summary>
    public bool Ensure(ID3D11Device* dev)
    {
        if (_failed) return false;
        if (Ready) return true;
        if (dev == null) { LastError = "device null"; _failed = true; return false; }
        try
        {
            if (!CompileVs(dev)) { _failed = true; return false; }
            if (!CompilePs(dev, PsSrc, out var ps)) { _failed = true; return false; }
            _ps = ps;
            if (!CompilePs(dev, PsMirrorSrc, out var psm)) { _failed = true; return false; }
            _psMirror = psm;
            if (!CompilePs(dev, PsCopySrc, out var psc)) { _failed = true; return false; }
            _psCopy = psc;
            if (!CompilePs(dev, PsEraseSrc, out var pse)) { _failed = true; return false; }
            _psErase = pse;
            if (!CompilePs(dev, PsTestSrc, out var pst)) { _failed = true; return false; }
            _psTest = pst;

            // premultiplied over (src + dst*(1-srcA))。UI ピクセルだけを既存の絵へ重ねる。
            var bd = new D3D11_BLEND_DESC();
            bd.RenderTarget[0].BlendEnable = 1;
            bd.RenderTarget[0].SrcBlend = D3D11_BLEND.D3D11_BLEND_ONE;
            bd.RenderTarget[0].DestBlend = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA;
            bd.RenderTarget[0].BlendOp = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD;
            bd.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND.D3D11_BLEND_ONE;
            bd.RenderTarget[0].DestBlendAlpha = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA;
            bd.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD;
            bd.RenderTarget[0].RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL;
            ID3D11BlendState* bs;
            if (dev->CreateBlendState(&bd, &bs) != 0 || bs == null)
            { LastError = "BlendState 生成失敗"; _failed = true; return false; }
            _blendOver = bs;

            // 巻き順に依存しないようカリング無効のラスタライザを用意する。
            var rd = new D3D11_RASTERIZER_DESC
            {
                FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID,
                CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE,
                FrontCounterClockwise = 0,
                DepthClipEnable = 0,
                ScissorEnable = 0,
                MultisampleEnable = 0,
                AntialiasedLineEnable = 0,
            };
            ID3D11RasterizerState* rs;
            if (dev->CreateRasterizerState(&rd, &rs) != 0 || rs == null)
            { LastError = "RasterizerState 生成失敗"; _failed = true; return false; }
            _rasterNoCull = rs;
            return true;
        }
        catch (Exception ex) { LastError = $"Ensure 例外: {ex.Message}"; _failed = true; return false; }
    }

    private bool CompileVs(ID3D11Device* dev)
    {
        ID3DBlob* code = null;
        if (!Compile(VsSrc, "vs_5_0", ref code)) return false;
        try
        {
            ID3D11VertexShader* vs;
            if (dev->CreateVertexShader(code->GetBufferPointer(), code->GetBufferSize(), null, &vs) != 0 || vs == null)
            { LastError = "CreateVertexShader 失敗"; return false; }
            _vs = vs;
            return true;
        }
        finally { if (code != null) code->Release(); }
    }

    private bool CompilePs(ID3D11Device* dev, string src, out ID3D11PixelShader* shader)
    {
        shader = null;
        ID3DBlob* code = null;
        if (!Compile(src, "ps_5_0", ref code)) return false;
        try
        {
            ID3D11PixelShader* ps;
            if (dev->CreatePixelShader(code->GetBufferPointer(), code->GetBufferSize(), null, &ps) != 0 || ps == null)
            { LastError = "CreatePixelShader 失敗"; return false; }
            shader = ps;
            return true;
        }
        finally { if (code != null) code->Release(); }
    }

    private bool Compile(string src, string target, ref ID3DBlob* code)
    {
        var srcBytes = Encoding.UTF8.GetBytes(src);
        var entry = Encoding.ASCII.GetBytes("main\0");
        var tgt = Encoding.ASCII.GetBytes(target + "\0");
        fixed (byte* pSrc = srcBytes)
        fixed (byte* pEntry = entry)
        fixed (byte* pTgt = tgt)
        {
            ID3DBlob* blob = null;
            ID3DBlob* err = null;
            int hr = D3DCompile(pSrc, (nuint)srcBytes.Length, null, null, null,
                                (sbyte*)pEntry, (sbyte*)pTgt, 0, 0, &blob, &err);
            if (hr < 0 || blob == null)
            {
                string msg = "unknown";
                if (err != null)
                    msg = Marshal.PtrToStringAnsi((nint)err->GetBufferPointer(), (int)(nuint)err->GetBufferSize()) ?? "unknown";
                LastError = $"{target} コンパイル失敗: {msg}";
                if (err != null) err->Release();
                return false;
            }
            if (err != null) err->Release();
            code = blob;
            return true;
        }
    }

    /// <summary>
    /// cur (UI 込み) と clean (UI 前) の差分を dstRtv へ描く。
    /// ゲームの描画ステートを壊さないよう、触る範囲を保存→復元する。
    /// </summary>
    /// <param name="mirror">true = 全画面ミラー (差分を取らず a=1 で丸ごと取り込む)。
    /// このとき cleanSrv は未使用。</param>
    public void Execute(ID3D11DeviceContext* ctx, ID3D11RenderTargetView* dstRtv,
                        ID3D11ShaderResourceView* curSrv, ID3D11ShaderResourceView* cleanSrv,
                        uint w, uint h, bool mirror = false)
    {
        if (ctx == null || dstRtv == null || curSrv == null || !Ready) return;
        if (!mirror && cleanSrv == null) return;

        // ── 触るステートを退避 (Get* は AddRef するため必ず Release する) ──
        ID3D11RenderTargetView* oldRtv = null;
        ID3D11DepthStencilView* oldDsv = null;
        ID3D11VertexShader* oldVs = null;
        ID3D11PixelShader* oldPs = null;
        ID3D11InputLayout* oldIl = null;
        ID3D11RasterizerState* oldRs = null;
        ID3D11BlendState* oldBs = null;
        ID3D11DepthStencilState* oldDs = null;
        ID3D11ShaderResourceView* oldSrv0 = null;
        ID3D11ShaderResourceView* oldSrv1 = null;
        var oldBlendFactor = stackalloc float[4];
        uint oldSampleMask = 0xffffffff;
        uint oldStencilRef = 0;
        D3D_PRIMITIVE_TOPOLOGY oldTopo = default;
        var oldVps = stackalloc D3D11_VIEWPORT[16];
        uint oldVpCount = 16;

        try
        {
            ctx->OMGetRenderTargets(1, &oldRtv, &oldDsv);
            ctx->VSGetShader(&oldVs, null, null);
            ctx->PSGetShader(&oldPs, null, null);
            ctx->IAGetInputLayout(&oldIl);
            ctx->IAGetPrimitiveTopology(&oldTopo);
            ctx->RSGetState(&oldRs);
            ctx->RSGetViewports(&oldVpCount, oldVps);
            ctx->OMGetBlendState(&oldBs, oldBlendFactor, &oldSampleMask);
            ctx->OMGetDepthStencilState(&oldDs, &oldStencilRef);
            ctx->PSGetShaderResources(0, 1, &oldSrv0);
            ctx->PSGetShaderResources(1, 1, &oldSrv1);

            // ── 差分描画 ──
            var rtv = dstRtv;
            ctx->OMSetRenderTargets(1, &rtv, null);           // 深度なし
            var clear = stackalloc float[4] { 0f, 0f, 0f, 0f };
            ctx->ClearRenderTargetView(dstRtv, clear);        // 透明で初期化

            var vp = new D3D11_VIEWPORT { TopLeftX = 0, TopLeftY = 0, Width = w, Height = h, MinDepth = 0, MaxDepth = 1 };
            ctx->RSSetViewports(1, &vp);
            ctx->RSSetState(_rasterNoCull);
            ctx->OMSetBlendState(null, null, 0xffffffff);     // ブレンドなし (差分結果をそのまま書く)
            ctx->OMSetDepthStencilState(null, 0);
            ctx->IASetInputLayout(null);                      // 頂点バッファ不要
            ctx->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            ctx->VSSetShader(_vs, null, 0);
            ctx->PSSetShader(mirror ? _psMirror : _ps, null, 0);
            var srvs = stackalloc ID3D11ShaderResourceView*[2] { curSrv, cleanSrv };
            ctx->PSSetShaderResources(0, 2, srvs);
            ctx->Draw(3, 0);

            // SRV を外す (次に同テクスチャが RT として使われる場合の競合回避)。
            var nullSrvs = stackalloc ID3D11ShaderResourceView*[2] { null, null };
            ctx->PSSetShaderResources(0, 2, nullSrvs);
        }
        catch (Exception ex) { LastError = $"Execute 例外: {ex.Message}"; }
        finally
        {
            // ── 退避したステートを復元 ──
            try
            {
                var rtvBack = oldRtv;
                ctx->OMSetRenderTargets(1, &rtvBack, oldDsv);
                ctx->VSSetShader(oldVs, null, 0);
                ctx->PSSetShader(oldPs, null, 0);
                ctx->IASetInputLayout(oldIl);
                ctx->IASetPrimitiveTopology(oldTopo);
                ctx->RSSetState(oldRs);
                if (oldVpCount > 0) ctx->RSSetViewports(oldVpCount, oldVps);
                ctx->OMSetBlendState(oldBs, oldBlendFactor, oldSampleMask);
                ctx->OMSetDepthStencilState(oldDs, oldStencilRef);
                var back0 = oldSrv0; ctx->PSSetShaderResources(0, 1, &back0);
                var back1 = oldSrv1; ctx->PSSetShaderResources(1, 1, &back1);
            }
            catch { }

            if (oldRtv != null) oldRtv->Release();
            if (oldDsv != null) oldDsv->Release();
            if (oldVs != null) oldVs->Release();
            if (oldPs != null) oldPs->Release();
            if (oldIl != null) oldIl->Release();
            if (oldRs != null) oldRs->Release();
            if (oldBs != null) oldBs->Release();
            if (oldDs != null) oldDs->Release();
            if (oldSrv0 != null) oldSrv0->Release();
            if (oldSrv1 != null) oldSrv1->Release();
        }
    }

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(void* pSrcData, nuint SrcDataSize, sbyte* pSourceName,
        void* pDefines, void* pInclude, sbyte* pEntryPoint, sbyte* pTarget,
        uint Flags1, uint Flags2, ID3DBlob** ppCode, ID3DBlob** ppErrorMsgs);

    public void Dispose()
    {
        try { if (_rasterNoCull != null) { _rasterNoCull->Release(); _rasterNoCull = null; } } catch { }
        try { if (_psMirror != null) { _psMirror->Release(); _psMirror = null; } } catch { }
        try { if (_ps != null) { _ps->Release(); _ps = null; } } catch { }
        try { if (_vs != null) { _vs->Release(); _vs = null; } } catch { }
    }
}
