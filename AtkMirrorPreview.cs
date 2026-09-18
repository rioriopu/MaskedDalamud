using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

using TerraFX.Interop.DirectX;

namespace MaskedDalamud;

/// <summary>
/// [検証用] キャプチャ除外を止めたまま、ミラーの見た目を**そのまま画面に出す**モード。
///
/// 目的: DComp 稼働中はオーバーレイが <c>WDA_EXCLUDEFROMCAPTURE</c> で保護されるため、
/// **スクリーンショットに写らない**。見た目の検証結果を共有したくても撮れない。
/// そこで DComp を切った状態で、ミラーの描画だけをゲームの backbuffer へ直接出す。
/// backbuffer に描くので普通に撮影・キャプチャできる。
///
/// 通常運用との違いは「復元しないこと」だけ:
///   DComp:   backbuffer を save → ミラーを描く → 差分をオーバーレイへ → backbuffer を復元
///   本モード: ミラーを描く (復元しない)  ← 画面にもキャプチャにも残る
///
/// したがって**配信保護は一切働かない**。検証用トグルを明示的に ON にしたときだけ動く。
///
/// 実装は DComp の描画経路をそのまま流用する。合成スワップチェインも専用ウィンドウも作らず、
/// Dalamud の <c>RunBeforeImGuiRender</c> に乗って backbuffer へ <see cref="AtkQuadRenderer"/>
/// を実行するだけなので、既存方式へ干渉しない。
/// </summary>
internal sealed unsafe class AtkMirrorPreview : IDisposable
{
    private readonly Plugin _plugin;
    private readonly DalamudImGuiInternals _internals = new();
    private readonly AtkQuadRenderer _quads = new();
    private readonly List<AtkQuadRenderer.Quad> _quadList = new();

    private ID3D11Device* _dev;
    private ID3D11DeviceContext* _ctx;
    private MethodInfo? _runBefore, _runAfter;
    private Action? _beforeCb, _afterCb;
    private bool _active;

    // [調査] ピクセル実測用の 1x1 ステージングテクスチャ。
    private ID3D11Texture2D* _probeTex;
    private DXGI_FORMAT _probeFmt;

    // [調査] ゲームが UI 描画に使った定数バッファの読み出し用ステージング。
    private ID3D11Buffer* _cbStaging;
    private uint _cbStagingSize;
    /// <summary>[調査] ゲームが最後に使った cb0[3] / cb0[4] の実値。</summary>
    public string CbText { get; private set; } = "";

    /// <summary>[調査] 次フレームで画面の一部を切り出して保存する要求。
    /// ミラーの有無で別ファイルに保存され、そのまま見比べられる。</summary>
    public bool RegionDumpRequested { get; set; }
    public string RegionDumpResult { get; private set; } = "";

    /// <summary>[調査] 次フレームでテクスチャを丸ごと吸い出す要求。</summary>
    public bool DumpRequested { get; set; }
    public string DumpResult { get; private set; } = "";

    public bool IsActive => _active;
    public string Status { get; private set; } = "未使用";
    /// <summary>[調査] 直近に読み出した backbuffer のピクセル値 (表示用)。</summary>
    public string ProbeText { get; private set; } = "";

    public AtkMirrorPreview(Plugin plugin) => _plugin = plugin;

    public bool Enable()
    {
        if (_active) return true;
        try
        {
            _internals.Resolve();
            if (!_internals.Available)
            { Status = $"reflection 解決失敗: {_internals.FailReason}"; return false; }

            _dev = (ID3D11Device*)_internals.GameDevicePtr;
            _ctx = (ID3D11DeviceContext*)_internals.GameDeviceContextPtr;
            if (_dev == null || _ctx == null) { Status = "device/context null"; return false; }

            var imt = _internals.InterfaceManager!.GetType();
            _runBefore = imt.GetMethod("RunBeforeImGuiRender",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Action) }, null);
            _runAfter = imt.GetMethod("RunAfterImGuiRender",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Action) }, null);
            if (_runBefore == null || _runAfter == null)
            { Status = "RunBefore/AfterImGuiRender 未解決"; return false; }

            _beforeCb ??= Before;
            _afterCb ??= After;
            _active = true;
            ArmBefore();
            Status = "検証モード稼働中";
            Plugin.Log.Info("[MirrorPreview] 有効化 (backbuffer へ直接描画・配信保護なし)");
            return true;
        }
        catch (Exception ex)
        {
            Status = $"例外: {ex.Message}";
            _active = false;
            return false;
        }
    }

    public void Disable()
    {
        if (!_active) return;
        _active = false;
        Status = "未使用";
        Plugin.Log.Info("[MirrorPreview] 無効化");
    }

    /// <summary>Dalamud のコールバックは「一度きり消費」なので毎フレーム積み直す必要がある。
    ///
    /// **自分が処理されているキューへ、その中から積み直してはいけない。**
    /// Dalamud は Before キューを「空になるまで」drain するため、Before の中で Before を積むと
    /// 永久に空にならず**ゲームごとフリーズする** (2026-09-12 に実機で発生)。
    /// DComp と同じく Before → After → Before の ping-pong にして、
    /// 常に「今 drain されていない側」へ積む。</summary>
    private void ArmBefore()
    {
        try { _runBefore!.Invoke(_internals.InterfaceManager, new object[] { _beforeCb! }); }
        catch (Exception ex) { Plugin.Log.Warning($"[MirrorPreview] ArmBefore 失敗: {ex.Message}"); _active = false; }
    }

    private void Before()
    {
        if (!_active) return;
        try
        {
            DrawToBackbuffer();
        }
        catch (Exception ex)
        {
            Status = $"描画例外: {ex.Message}";
            Plugin.Log.Warning($"[MirrorPreview] 描画例外→停止: {ex.Message}");
            _active = false;
            return;
        }
        // 次フレームの Before は After 側から積む (同一キューへの再投入を避ける)。
        try { _runAfter!.Invoke(_internals.InterfaceManager, new object[] { _afterCb! }); }
        catch (Exception ex) { Plugin.Log.Warning($"[MirrorPreview] After 予約失敗: {ex.Message}"); _active = false; }
    }

    private void After()
    {
        if (!_active) return;

        // **計測はここで行う。**
        // Before() の時点では Dalamud がまだ ImGui を描いていないため、
        // ImGui 側で描く文字が backbuffer に存在せず「数字が消えている」ように見えてしまう
        // (実際は描けているのに測定側の取りこぼしだった)。
        try
        {
            var km = _plugin.kamiMirror;
            bool mirrorOn = km != null && km.Active;
            if (_plugin.cfg.kamiPixelProbe || RegionDumpRequested)
            {
                var sc = (IDXGISwapChain*)_internals.GameSwapChainPtr;
                if (sc != null)
                {
                    ID3D11Texture2D* bb = null;
                    var iid = IID_ID3D11Texture2D;
                    if (sc->GetBuffer(0, &iid, (void**)&bb) == 0 && bb != null)
                    {
                        try
                        {
                            D3D11_TEXTURE2D_DESC td;
                            bb->GetDesc(&td);
                            if (_plugin.cfg.kamiPixelProbe)
                            {
                                ProbePixel(bb, td);
                                ReadGameConstantBuffer();
                            }
                            if (RegionDumpRequested)
                            {
                                RegionDumpRequested = false;
                                try { RegionDumpResult = DumpRegion(bb, td, mirrorOn); }
                                catch (Exception ex) { RegionDumpResult = $"保存失敗: {ex.Message}"; }
                            }
                        }
                        finally { bb->Release(); }
                    }
                }
            }
        }
        catch (Exception ex) { RegionDumpResult = $"計測例外: {ex.Message}"; }

        ArmBefore();
    }

    private void DrawToBackbuffer()
    {
        var km = _plugin.kamiMirror;
        bool mirrorOn = km != null && km.Active;

        var sc = (IDXGISwapChain*)_internals.GameSwapChainPtr;
        if (sc == null) { Status = "swapchain null"; return; }

        ID3D11Texture2D* bb = null;
        var iid = IID_ID3D11Texture2D;
        if (sc->GetBuffer(0, &iid, (void**)&bb) != 0 || bb == null) { Status = "backbuffer 取得失敗"; return; }
        try
        {
            D3D11_TEXTURE2D_DESC td;
            bb->GetDesc(&td);
            if (td.Width < 16 || td.Height < 16) { Status = "backbuffer サイズ異常"; return; }

            if (mirrorOn)
            {
                if (!_quads.Ensure(_dev)) { Status = $"シェーダ準備不可: {_quads.LastError}"; return; }

                int n = km!.SnapshotQuads(_quadList, _plugin.cfg.kamiAdditiveOnAdd);
                if (n > 0)
                {
                    ID3D11RenderTargetView* rtv;
                    if (_dev->CreateRenderTargetView((ID3D11Resource*)bb, null, &rtv) != 0 || rtv == null)
                    { Status = "backbuffer RTV 生成失敗"; return; }
                    try { _quads.Execute(_ctx, rtv, td.Width, td.Height, _quadList); }
                    finally { rtv->Release(); }
                }
                Status = $"検証モード OK クアッド={n}";
            }
            else Status = "ミラー停止中 (実測のみ)";

            if (DumpRequested) { DumpRequested = false; try { DumpTextures(); } catch (Exception ex) { DumpResult = ex.Message; } }
        }
        finally { bb->Release(); }
    }

    /// <summary>指定座標のピクセルを CPU へ読み出す。1x1 のステージングテクスチャへ
    /// コピーして Map するだけなので、ゲームの描画ステートには触れない。</summary>
    private void ProbePixel(ID3D11Texture2D* bb, in D3D11_TEXTURE2D_DESC bbDesc)
    {
        try
        {
            int px = Math.Clamp(_plugin.cfg.kamiProbeX, 0, (int)bbDesc.Width - 1);
            int py = Math.Clamp(_plugin.cfg.kamiProbeY, 0, (int)bbDesc.Height - 1);

            if (_probeTex == null || _probeFmt != bbDesc.Format)
            {
                if (_probeTex != null) { _probeTex->Release(); _probeTex = null; }
                var td = new D3D11_TEXTURE2D_DESC
                {
                    Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = bbDesc.Format,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                    Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                    BindFlags = 0,
                    CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
                    MiscFlags = 0,
                };
                ID3D11Texture2D* t;
                if (_dev->CreateTexture2D(&td, null, &t) != 0 || t == null)
                { ProbeText = "実測用テクスチャ生成失敗"; return; }
                _probeTex = t;
                _probeFmt = bbDesc.Format;
            }

            var box = new D3D11_BOX { left = (uint)px, top = (uint)py, front = 0,
                                      right = (uint)px + 1, bottom = (uint)py + 1, back = 1 };
            _ctx->CopySubresourceRegion((ID3D11Resource*)_probeTex, 0, 0, 0, 0,
                                        (ID3D11Resource*)bb, 0, &box);

            D3D11_MAPPED_SUBRESOURCE m;
            if (_ctx->Map((ID3D11Resource*)_probeTex, 0, D3D11_MAP.D3D11_MAP_READ, 0, &m) != 0)
            { ProbeText = "Map 失敗"; return; }
            try
            {
                var p = (byte*)m.pData;
                // 画面形式は R8G8B8A8_UNORM (28) 系を想定。B/R が入れ替わる形式も併記する。
                ProbeText = $"({px},{py}) RGBA=({p[0]},{p[1]},{p[2]},{p[3]})"
                          + $"  BGRA=({p[2]},{p[1]},{p[0]},{p[3]})  fmt={(int)bbDesc.Format}";
            }
            finally { _ctx->Unmap((ID3D11Resource*)_probeTex, 0); }
        }
        catch (Exception ex) { ProbeText = $"実測例外: {ex.Message}"; }
    }

    /// <summary>[調査] ミラー対象のテクスチャを**丸ごと**吸い出して保存する。
    ///
    /// 我々はゲームと同一の SRV を使っているので、ここに出るのはゲームが見ているものそのもの。
    /// 「どのテクセルを読むべきか」を目で確認するために、専用シェーダで等倍のRTへ描いてから
    /// CPU へ読み戻す (圧縮形式でも確実に取れる)。</summary>
    private void DumpTextures()
    {
        var km = _plugin.kamiMirror;
        if (km == null) { DumpResult = "ミラー未生成"; return; }
        if (!_quads.Ensure(_dev)) { DumpResult = $"シェーダ準備不可: {_quads.LastError}"; return; }
        if (km.SnapshotQuads(_quadList, false) == 0) { DumpResult = "対象なし"; return; }

        var dir = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "texdump");
        System.IO.Directory.CreateDirectory(dir);

        var done = new HashSet<nint>();
        int saved = 0;
        foreach (var q in _quadList)
        {
            if (q.Srv == 0 || !done.Add(q.Srv)) continue;
            if (!KamiMirror.TryGetRealTexSize(q.Srv, out var tw, out var th)) continue;
            int w = (int)tw, h = (int)th;
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096) continue;
            if (DumpOne(q.Srv, w, h, Path.Combine(dir, $"tex_{saved:D2}_{w}x{h}.bin"))) saved++;
            if (saved >= 8) break;
        }
        DumpResult = saved > 0 ? $"{saved} 枚を保存: {dir}" : "保存できませんでした";
        Plugin.Log.Info($"[TexDump] {DumpResult}");
    }

    /// <summary>1 枚を等倍で RT へ描いて CPU へ読み戻し、RGBA 生データとして保存する。</summary>
    private bool DumpOne(nint srv, int w, int h, string path)
    {
        ID3D11Texture2D* rt = null, stg = null;
        ID3D11RenderTargetView* rtv = null;
        try
        {
            var rtd = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET,
            };
            if (_dev->CreateTexture2D(&rtd, null, &rt) != 0 || rt == null) return false;
            if (_dev->CreateRenderTargetView((ID3D11Resource*)rt, null, &rtv) != 0 || rtv == null) return false;

            // テクスチャ全面を等倍で描く (色変換なし)。
            var one = new List<AtkQuadRenderer.Quad>
            {
                new AtkQuadRenderer.Quad
                {
                    X0 = 0, Y0 = 0, X1 = w, Y1 = h,
                    U0 = 0, V0 = 0, U1 = 1, V1 = 1,
                    Srv = srv, MR = 1, MG = 1, MB = 1, MA = 1,
                },
            };
            _quads.Execute(_ctx, rtv, (uint)w, (uint)h, one);

            var sd = rtd;
            sd.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
            sd.BindFlags = 0;
            sd.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
            if (_dev->CreateTexture2D(&sd, null, &stg) != 0 || stg == null) return false;
            _ctx->CopyResource((ID3D11Resource*)stg, (ID3D11Resource*)rt);

            D3D11_MAPPED_SUBRESOURCE m;
            if (_ctx->Map((ID3D11Resource*)stg, 0, D3D11_MAP.D3D11_MAP_READ, 0, &m) != 0) return false;
            try
            {
                var bytes = new byte[w * h * 4];
                for (var y = 0; y < h; y++)
                    Marshal.Copy((nint)((byte*)m.pData + y * m.RowPitch), bytes, y * w * 4, w * 4);
                File.WriteAllBytes(path, bytes);
            }
            finally { _ctx->Unmap((ID3D11Resource*)stg, 0); }
            return true;
        }
        catch { return false; }
        finally
        {
            if (rtv != null) rtv->Release();
            if (stg != null) stg->Release();
            if (rt != null) rt->Release();
        }
    }

    /// <summary>[調査] いま束ねられている PS 定数バッファ 0 番を読み出す。
    ///
    /// 採取したゲームの UI シェーダ (ps_0036) は
    ///   <c>out.rgb = saturate(texel.rgb * cb0[3].xyz + cb0[4].xyz)</c>
    ///   <c>out.a   = texel.a * cb0[3].w</c>
    /// という式なので、cb0[3] / cb0[4] が「ゲームが実際に使った Multiply / Add」そのもの。
    /// ノードのフィールドから計算した値と突き合わせれば変換規則が確定する。
    ///
    /// ゲームの UI 描画直後に呼ばれるため、最後に描かれた UI ノードの値が残っている。
    /// 読み取り専用 (ステージングへコピーして Map) でステートは一切変更しない。</summary>
    private void ReadGameConstantBuffer()
    {
        ID3D11Buffer* cb = null;
        try
        {
            _ctx->PSGetConstantBuffers(0, 1, &cb);
            if (cb == null) { CbText = "cb0 未バインド"; return; }

            D3D11_BUFFER_DESC bd;
            cb->GetDesc(&bd);
            if (bd.ByteWidth < 80) { CbText = $"cb0 が小さすぎます ({bd.ByteWidth}B)"; return; }

            if (_cbStaging == null || _cbStagingSize != bd.ByteWidth)
            {
                if (_cbStaging != null) { _cbStaging->Release(); _cbStaging = null; }
                var sd = new D3D11_BUFFER_DESC
                {
                    ByteWidth = bd.ByteWidth,
                    Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                    BindFlags = 0,
                    CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
                };
                ID3D11Buffer* stg;
                if (_dev->CreateBuffer(&sd, null, &stg) != 0 || stg == null)
                { CbText = "読み出し用バッファ生成失敗"; return; }
                _cbStaging = stg;
                _cbStagingSize = bd.ByteWidth;
            }

            _ctx->CopyResource((ID3D11Resource*)_cbStaging, (ID3D11Resource*)cb);

            D3D11_MAPPED_SUBRESOURCE m;
            if (_ctx->Map((ID3D11Resource*)_cbStaging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &m) != 0)
            { CbText = "Map 失敗"; return; }
            try
            {
                var f = (float*)m.pData;
                // float4 単位: [3] = 乗算色+アルファ / [4] = 加算色
                CbText = $"cb0[3]=({f[12]:F3},{f[13]:F3},{f[14]:F3},{f[15]:F3})"
                       + $"  cb0[4]=({f[16]:F3},{f[17]:F3},{f[18]:F3})  size={bd.ByteWidth}B";
            }
            finally { _ctx->Unmap((ID3D11Resource*)_cbStaging, 0); }
        }
        catch (Exception ex) { CbText = $"読み出し例外: {ex.Message}"; }
        finally { if (cb != null) cb->Release(); }
    }

    /// <summary>[調査] 画面の一部を切り出して RGBA の生データで保存する。
    /// ミラーの有無で別名になるので、2 回押すだけで比較材料が揃う。</summary>
    private string DumpRegion(ID3D11Texture2D* bb, in D3D11_TEXTURE2D_DESC bbDesc, bool mirrorOn)
    {
        var c = _plugin.cfg;
        int w = Math.Clamp(c.kamiRegionW, 1, 1024);
        int h = Math.Clamp(c.kamiRegionH, 1, 1024);
        int x = Math.Clamp(c.kamiProbeX, 0, (int)bbDesc.Width - 1);
        int y = Math.Clamp(c.kamiProbeY, 0, (int)bbDesc.Height - 1);
        w = Math.Min(w, (int)bbDesc.Width - x);
        h = Math.Min(h, (int)bbDesc.Height - y);

        var dir = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "diag");
        System.IO.Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, mirrorOn ? "region_mirror.bin" : "region_native.bin");

        ID3D11Texture2D* stg = null;
        try
        {
            var sd = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = bbDesc.Format,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                BindFlags = 0,
                CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            };
            if (_dev->CreateTexture2D(&sd, null, &stg) != 0 || stg == null) return "保存用テクスチャ生成失敗";

            var box = new D3D11_BOX { left = (uint)x, top = (uint)y, front = 0,
                                      right = (uint)(x + w), bottom = (uint)(y + h), back = 1 };
            _ctx->CopySubresourceRegion((ID3D11Resource*)stg, 0, 0, 0, 0, (ID3D11Resource*)bb, 0, &box);

            D3D11_MAPPED_SUBRESOURCE m;
            if (_ctx->Map((ID3D11Resource*)stg, 0, D3D11_MAP.D3D11_MAP_READ, 0, &m) != 0) return "Map 失敗";
            try
            {
                var bytes = new byte[8 + w * h * 4];
                BitConverter.GetBytes(w).CopyTo(bytes, 0);
                BitConverter.GetBytes(h).CopyTo(bytes, 4);
                for (var row = 0; row < h; row++)
                    Marshal.Copy((nint)((byte*)m.pData + row * m.RowPitch), bytes, 8 + row * w * 4, w * 4);
                File.WriteAllBytes(path, bytes);
            }
            finally { _ctx->Unmap((ID3D11Resource*)stg, 0); }
            return $"{(mirrorOn ? "ミラー" : "ネイティブ")} {w}x{h} @({x},{y}) → {path}";
        }
        finally { if (stg != null) stg->Release(); }
    }

    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    public void Dispose()
    {
        try { Disable(); } catch { }
        try { if (_probeTex != null) { _probeTex->Release(); _probeTex = null; } } catch { }
        try { if (_cbStaging != null) { _cbStaging->Release(); _cbStaging = null; } } catch { }
        try { _quads.Dispose(); } catch { }
    }
}
