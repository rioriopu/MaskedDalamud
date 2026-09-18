using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace MaskedDalamud;

/// <summary>
/// [試験] Atk 互換のクアッド描画。KamiToolKit 製ネイティブ UI を「手元だけ」に描き直すために使う。
///
/// 経緯: 方式A の ImGui ミラーは <c>ImDrawList.AddImage</c> しか使えず、
/// **乗算 tint しか掛けられない**という道具の制約があった。Atk の合成式は
///
///   <code>out.rgb = texel.rgb × (Color.rgb × Multiply/100) + (Add/255)</code>
///   <code>out.a   = texel.a   × Color.a</code>
///
/// で**加算項がある**ため、ImGui では加算分を乗算側へ寄せる近似しかできず、
/// 「色があせる・ゲージの発光部分が出ない」原因になっていた。
/// また Add は符号付き (負 = 暗くする) だが、近似では負を捨てていた。
///
/// 本クラスは専用ピクセルシェーダでこの式をそのまま実装する。近似は無くなる。
///
/// 描画先: **ゲームの backbuffer** (`Before()` で save 済みの直後)。こうすると
///   ・差分キャプチャ (mode 2) / 全画面ミラー (mode 1) が変化画素として拾う → 手元に出る
///   ・`After()` 末尾の backbuffer 復元で消える                               → 配信には出ない
///   ・Dalamud の ImGui より**前**に描くので、プラグイン窓の下に潜る (正しい前後関係)
/// が同時に成立する。Atk 自身と同じ「backbuffer へアルファ合成する」経路なので素直。
/// </summary>
internal sealed unsafe class AtkQuadRenderer : IDisposable
{
    /// <summary>1 枚のテクスチャ付き矩形。9 分割ノードは 9 個に展開して積む。</summary>
    internal struct Quad
    {
        public float X0, Y0, X1, Y1;    // 画面座標 (px)。回転なしのときの矩形
        /// <summary>回転後の 4 隅 (左上,右上,左下,右下)。null 相当 (Rotated=false) なら X0..Y1 を使う。</summary>
        public bool Rotated;
        public float Cx0, Cy0, Cx1, Cy1, Cx2, Cy2, Cx3, Cy3;
        public float U0, V0, U1, V1;    // テクスチャ座標 (0..1)
        public nint Srv;                // ID3D11ShaderResourceView*
        public float MR, MG, MB, MA;    // 乗算色 (Color/255 × Multiply/100) と アルファ (Color.A/255)
        public float AR, AG, AB;        // 加算色 (Add/255)。負もあり得る
        public float Bright;            // [暫定] 明るさ補正 (加算)。原因未特定の差分を埋める
        public bool Additive;           // true = 加算合成 (発光表現)
        public bool Tile;               // true = UV が 0..1 を超える繰り返し (WrapMode.Tile)
        /// <summary>クリッピング矩形 (画面座標)。W が 0 なら制限なし。
        /// AtkClippingMaskNode による切り抜きをシザー矩形で近似する。</summary>
        public float ClipX, ClipY, ClipW, ClipH;
    }

    /// <summary>1 フレームに積める上限。異常時に GPU を溺れさせないための保険。</summary>
    public const int MaxQuads = 1024;

    private ID3D11VertexShader* _vs;
    private ID3D11PixelShader* _ps;
    private ID3D11Buffer* _cb;
    private ID3D11BlendState* _blendOver;
    private ID3D11BlendState* _blendAdd;
    private ID3D11SamplerState* _sampler;      // Clamp (既定)
    private ID3D11SamplerState* _samplerWrap;  // Wrap (WrapMode.Tile 用)
    private ID3D11RasterizerState* _rasterNoCull;
    private ID3D11RasterizerState* _rasterScissor;   // クリッピング用 (シザー有効)
    private bool _failed;

    public string? LastError { get; private set; }
    public bool Ready => _vs != null && _ps != null && _cb != null && !_failed;

    /// <summary>定数バッファのレイアウト (16 バイト境界 × 4 = 64 バイト)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Cb
    {
        public float C0x, C0y, C1x, C1y;    // クリップ空間の 4 隅 (左上,右上)
        public float C2x, C2y, C3x, C3y;    // (左下,右下)
        public float Uu0, Uv0, Uu1, Uv1;    // テクスチャ座標
        public float Mr, Mg, Mb, Ma;        // 乗算色 + アルファ
        public float Ar, Ag, Ab, Bright;    // 加算色 + 明るさ補正
    }

    private const string CbDecl = @"
cbuffer Cb : register(b0)
{
    float4 c01;    // クリップ空間の隅: 左上.xy, 右上.xy
    float4 c23;    // 左下.xy, 右下.xy
    float4 uvr;    // u0,v0,u1,v1
    float4 mul;    // 乗算色 rgb + アルファ a
    float4 add;    // 加算色 rgb + 明るさ補正 w
};
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
";

    // 頂点バッファ不要。TRIANGLESTRIP 4 頂点を SV_VertexID から組み立てる。
    private const string VsSrc = CbDecl + @"
VSOut main(uint id : SV_VertexID)
{
    VSOut o;
    // TRIANGLESTRIP の 0..3 を 左上/右上/左下/右下 に対応させる。
    float2 p = (id == 0) ? c01.xy : (id == 1) ? c01.zw : (id == 2) ? c23.xy : c23.zw;
    float u = (id & 1) ? uvr.z : uvr.x;
    float v = (id & 2) ? uvr.w : uvr.y;
    o.pos = float4(p, 0, 1);
    o.uv  = float2(u, v);
    return o;
}";

    // Atk の合成式そのまま。saturate は Atk 側も飽和するため掛ける。
    private const string PsSrc = CbDecl + @"
Texture2D tex : register(t0);
SamplerState smp : register(s0);
float4 main(VSOut i) : SV_Target
{
    // mip 0 を明示する。9 分割で狭い領域を横に引き伸ばすと、勾配からミップが自動選択されて
    // ぼけた絵 (= 平坦で明るい色) になり、ネイティブと合わなくなる。
    float4 t = tex.SampleLevel(smp, i.uv, 0);
    float3 rgb = saturate(t.rgb * mul.rgb + add.rgb + add.w);
    float a = t.a * mul.a;
    return float4(rgb, a);
}";

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

            var bufDesc = new D3D11_BUFFER_DESC
            {
                ByteWidth = (uint)sizeof(Cb),
                Usage = D3D11_USAGE.D3D11_USAGE_DYNAMIC,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_CONSTANT_BUFFER,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE,
                MiscFlags = 0, StructureByteStride = 0,
            };
            ID3D11Buffer* cb;
            if (dev->CreateBuffer(&bufDesc, null, &cb) != 0 || cb == null)
            { LastError = "定数バッファ生成失敗"; _failed = true; return false; }
            _cb = cb;

            // 通常合成 (straight alpha over)。Atk が backbuffer へ描くときと同じ形。
            if (!CreateBlend(dev, D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA, out var bo)) { _failed = true; return false; }
            _blendOver = bo;
            // 加算合成 (発光表現用)。dst をそのまま残して足し込む。
            if (!CreateBlend(dev, D3D11_BLEND.D3D11_BLEND_ONE, out var ba)) { _failed = true; return false; }
            _blendAdd = ba;

            var sd = new D3D11_SAMPLER_DESC
            {
                // 線形フィルタ。UI は等倍とは限らず、アイコン(40→24)や枠(48→30)のように
                // **縮小して描かれる**ものがある。ポイントだと画素が間引かれて
                // 細い枠線が消え、絵もガタつく (実測で差分が枠に集中して判明)。
                Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
                AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
                ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_ALWAYS,
                MinLOD = 0, MaxLOD = 0,   // mip 0 のみ (UI は等倍前提)
            };
            ID3D11SamplerState* smp;
            if (dev->CreateSamplerState(&sd, &smp) != 0 || smp == null)
            { LastError = "SamplerState 生成失敗"; _failed = true; return false; }
            _sampler = smp;

            // WrapMode.Tile 用。UV が 0..1 を超えたぶんを繰り返す。
            sd.AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_WRAP;
            sd.AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_WRAP;
            ID3D11SamplerState* smpW;
            if (dev->CreateSamplerState(&sd, &smpW) != 0 || smpW == null)
            { LastError = "SamplerState(Wrap) 生成失敗"; _failed = true; return false; }
            _samplerWrap = smpW;

            var rd = new D3D11_RASTERIZER_DESC
            {
                FillMode = D3D11_FILL_MODE.D3D11_FILL_SOLID,
                CullMode = D3D11_CULL_MODE.D3D11_CULL_NONE,
                FrontCounterClockwise = 0, DepthClipEnable = 0, ScissorEnable = 0,
                MultisampleEnable = 0, AntialiasedLineEnable = 0,
            };
            ID3D11RasterizerState* rs;
            if (dev->CreateRasterizerState(&rd, &rs) != 0 || rs == null)
            { LastError = "RasterizerState 生成失敗"; _failed = true; return false; }
            _rasterNoCull = rs;

            rd.ScissorEnable = 1;
            ID3D11RasterizerState* rsc;
            if (dev->CreateRasterizerState(&rd, &rsc) != 0 || rsc == null)
            { LastError = "RasterizerState(Scissor) 生成失敗"; _failed = true; return false; }
            _rasterScissor = rsc;
            return true;
        }
        catch (Exception ex) { LastError = $"Ensure 例外: {ex.Message}"; _failed = true; return false; }
    }

    private bool CreateBlend(ID3D11Device* dev, D3D11_BLEND destBlend, out ID3D11BlendState* state)
    {
        state = null;
        var bd = new D3D11_BLEND_DESC();
        bd.RenderTarget[0].BlendEnable = 1;
        bd.RenderTarget[0].SrcBlend = D3D11_BLEND.D3D11_BLEND_SRC_ALPHA;
        bd.RenderTarget[0].DestBlend = destBlend;
        bd.RenderTarget[0].BlendOp = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD;
        bd.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND.D3D11_BLEND_ONE;
        bd.RenderTarget[0].DestBlendAlpha = D3D11_BLEND.D3D11_BLEND_INV_SRC_ALPHA;
        bd.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP.D3D11_BLEND_OP_ADD;
        bd.RenderTarget[0].RenderTargetWriteMask = (byte)D3D11_COLOR_WRITE_ENABLE.D3D11_COLOR_WRITE_ENABLE_ALL;
        ID3D11BlendState* bs;
        if (dev->CreateBlendState(&bd, &bs) != 0 || bs == null)
        { LastError = "BlendState 生成失敗"; return false; }
        state = bs;
        return true;
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

    /// <summary>クアッド列を dstRtv へ描く。ゲームの描画ステートは保存 → 復元する。</summary>
    public void Execute(ID3D11DeviceContext* ctx, ID3D11RenderTargetView* dstRtv,
                        uint w, uint h, List<Quad> quads)
    {
        if (ctx == null || dstRtv == null || !Ready || quads.Count == 0) return;
        if (w == 0 || h == 0) return;

        ID3D11RenderTargetView* oldRtv = null;
        ID3D11DepthStencilView* oldDsv = null;
        ID3D11VertexShader* oldVs = null;
        ID3D11PixelShader* oldPs = null;
        ID3D11InputLayout* oldIl = null;
        ID3D11RasterizerState* oldRs = null;
        ID3D11BlendState* oldBs = null;
        ID3D11DepthStencilState* oldDs = null;
        ID3D11ShaderResourceView* oldSrv0 = null;
        ID3D11SamplerState* oldSmp = null;
        ID3D11Buffer* oldVsCb = null;
        ID3D11Buffer* oldPsCb = null;
        var oldBlendFactor = stackalloc float[4];
        uint oldSampleMask = 0xffffffff;
        uint oldStencilRef = 0;
        D3D_PRIMITIVE_TOPOLOGY oldTopo = default;
        var oldVps = stackalloc D3D11_VIEWPORT[16];
        uint oldVpCount = 16;
        var oldScissors = stackalloc RECT[16];
        uint oldScCount = 16;

        try
        {
            ctx->OMGetRenderTargets(1, &oldRtv, &oldDsv);
            ctx->VSGetShader(&oldVs, null, null);
            ctx->PSGetShader(&oldPs, null, null);
            ctx->IAGetInputLayout(&oldIl);
            ctx->IAGetPrimitiveTopology(&oldTopo);
            ctx->RSGetState(&oldRs);
            ctx->RSGetViewports(&oldVpCount, oldVps);
            ctx->RSGetScissorRects(&oldScCount, oldScissors);
            ctx->OMGetBlendState(&oldBs, oldBlendFactor, &oldSampleMask);
            ctx->OMGetDepthStencilState(&oldDs, &oldStencilRef);
            ctx->PSGetShaderResources(0, 1, &oldSrv0);
            ctx->PSGetSamplers(0, 1, &oldSmp);
            ctx->VSGetConstantBuffers(0, 1, &oldVsCb);
            ctx->PSGetConstantBuffers(0, 1, &oldPsCb);

            var rtv = dstRtv;
            ctx->OMSetRenderTargets(1, &rtv, null);           // 深度なし (UI は奥行きを持たない)
            var vp = new D3D11_VIEWPORT { TopLeftX = 0, TopLeftY = 0, Width = w, Height = h, MinDepth = 0, MaxDepth = 1 };
            ctx->RSSetViewports(1, &vp);
            ctx->RSSetState(_rasterNoCull);
            ctx->OMSetDepthStencilState(null, 0);
            ctx->IASetInputLayout(null);                      // 頂点バッファ不要
            ctx->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
            ctx->VSSetShader(_vs, null, 0);
            ctx->PSSetShader(_ps, null, 0);
            var cbLocal = _cb;
            ctx->VSSetConstantBuffers(0, 1, &cbLocal);
            ctx->PSSetConstantBuffers(0, 1, &cbLocal);
            var smpLocal = _sampler;
            ctx->PSSetSamplers(0, 1, &smpLocal);

            bool additiveNow = false;
            ctx->OMSetBlendState(_blendOver, null, 0xffffffff);
            bool tileNow = false;
            bool clipNow = false;

            int n = Math.Min(quads.Count, MaxQuads);
            for (var i = 0; i < n; i++)
            {
                var q = quads[i];
                if (q.Srv == 0) continue;
                if (!q.Rotated && (q.X1 - q.X0 <= 0.01f || q.Y1 - q.Y0 <= 0.01f)) continue;   // 潰れた矩形は描かない

                if (q.Additive != additiveNow)
                {
                    additiveNow = q.Additive;
                    ctx->OMSetBlendState(additiveNow ? _blendAdd : _blendOver, null, 0xffffffff);
                }

                // 画面座標 → クリップ空間 (y は上下反転)。
                // 回転ノードは呼び出し側が 4 隅を計算済み。
                float sx0, sy0, sx1, sy1, sx2, sy2, sx3, sy3;
                if (q.Rotated)
                {
                    sx0 = q.Cx0; sy0 = q.Cy0; sx1 = q.Cx1; sy1 = q.Cy1;
                    sx2 = q.Cx2; sy2 = q.Cy2; sx3 = q.Cx3; sy3 = q.Cy3;
                }
                else
                {
                    sx0 = q.X0; sy0 = q.Y0; sx1 = q.X1; sy1 = q.Y0;
                    sx2 = q.X0; sy2 = q.Y1; sx3 = q.X1; sy3 = q.Y1;
                }
                float ToCx(float x) => x / w * 2f - 1f;
                float ToCy(float y) => 1f - y / h * 2f;
                var cb = new Cb
                {
                    C0x = ToCx(sx0), C0y = ToCy(sy0),
                    C1x = ToCx(sx1), C1y = ToCy(sy1),
                    C2x = ToCx(sx2), C2y = ToCy(sy2),
                    C3x = ToCx(sx3), C3y = ToCy(sy3),
                    Uu0 = q.U0, Uv0 = q.V0, Uu1 = q.U1, Uv1 = q.V1,
                    Mr = q.MR, Mg = q.MG, Mb = q.MB, Ma = q.MA,
                    Ar = q.AR, Ag = q.AG, Ab = q.AB, Bright = q.Bright,
                };

                D3D11_MAPPED_SUBRESOURCE mapped;
                if (ctx->Map((ID3D11Resource*)_cb, 0, D3D11_MAP.D3D11_MAP_WRITE_DISCARD, 0, &mapped) != 0) continue;
                *(Cb*)mapped.pData = cb;
                ctx->Unmap((ID3D11Resource*)_cb, 0);

                // クリッピング矩形。AtkClippingMaskNode があるノードだけシザーを効かせる。
                bool wantClip = q.ClipW > 0.5f && q.ClipH > 0.5f;
                if (wantClip)
                {
                    var sr = new RECT
                    {
                        left = (int)MathF.Floor(q.ClipX), top = (int)MathF.Floor(q.ClipY),
                        right = (int)MathF.Ceiling(q.ClipX + q.ClipW),
                        bottom = (int)MathF.Ceiling(q.ClipY + q.ClipH),
                    };
                    ctx->RSSetScissorRects(1, &sr);
                }
                if (wantClip != clipNow)
                {
                    clipNow = wantClip;
                    ctx->RSSetState(clipNow ? _rasterScissor : _rasterNoCull);
                }

                if (q.Tile != tileNow)
                {
                    tileNow = q.Tile;
                    var sw = tileNow ? _samplerWrap : _sampler;
                    ctx->PSSetSamplers(0, 1, &sw);
                }

                var srv = (ID3D11ShaderResourceView*)q.Srv;
                ctx->PSSetShaderResources(0, 1, &srv);
                ctx->Draw(4, 0);
            }

            ID3D11ShaderResourceView* nullSrv = null;
            ctx->PSSetShaderResources(0, 1, &nullSrv);
        }
        catch (Exception ex) { LastError = $"Execute 例外: {ex.Message}"; }
        finally
        {
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
                if (oldScCount > 0) ctx->RSSetScissorRects(oldScCount, oldScissors);
                ctx->OMSetBlendState(oldBs, oldBlendFactor, oldSampleMask);
                ctx->OMSetDepthStencilState(oldDs, oldStencilRef);
                var b0 = oldSrv0; ctx->PSSetShaderResources(0, 1, &b0);
                var s0 = oldSmp; ctx->PSSetSamplers(0, 1, &s0);
                var v0 = oldVsCb; ctx->VSSetConstantBuffers(0, 1, &v0);
                var p0 = oldPsCb; ctx->PSSetConstantBuffers(0, 1, &p0);
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
            if (oldSmp != null) oldSmp->Release();
            if (oldVsCb != null) oldVsCb->Release();
            if (oldPsCb != null) oldPsCb->Release();
        }
    }

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(void* pSrcData, nuint SrcDataSize, sbyte* pSourceName,
        void* pDefines, void* pInclude, sbyte* pEntrypoint, sbyte* pTarget,
        uint Flags1, uint Flags2, ID3DBlob** ppCode, ID3DBlob** ppErrorMsgs);

    public void Dispose()
    {
        try { if (_vs != null) { _vs->Release(); _vs = null; } } catch { }
        try { if (_ps != null) { _ps->Release(); _ps = null; } } catch { }
        try { if (_cb != null) { _cb->Release(); _cb = null; } } catch { }
        try { if (_blendOver != null) { _blendOver->Release(); _blendOver = null; } } catch { }
        try { if (_blendAdd != null) { _blendAdd->Release(); _blendAdd = null; } } catch { }
        try { if (_sampler != null) { _sampler->Release(); _sampler = null; } } catch { }
        try { if (_samplerWrap != null) { _samplerWrap->Release(); _samplerWrap = null; } } catch { }
        try { if (_rasterNoCull != null) { _rasterNoCull->Release(); _rasterNoCull = null; } } catch { }
        try { if (_rasterScissor != null) { _rasterScissor->Release(); _rasterScissor = null; } } catch { }
    }
}
