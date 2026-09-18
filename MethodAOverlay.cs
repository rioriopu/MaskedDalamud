using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.DXGI_FORMAT;

namespace MaskedDalamud;

/// <summary>
/// Method A Stage 2 (Layered 方式): FFXIV の ID3D11Device 上に「オフスクリーン
/// レンダーターゲット」を持ち、Dalamud にそこへ ImGui を描かせ、毎フレーム
/// CPU へコピーして <c>UpdateLayeredWindow</c> で per-pixel alpha 表示する。
///
/// なぜ DComp をやめたか:
///   DComp + WS_EX_NOREDIRECTIONBITMAP + WS_EX_TRANSPARENT のオーバーレイは
///   クリックは貫通するが <c>WindowFromPoint</c>／OS のマウスルーティング/占有
///   判定からは消えず、FF14 本体がマウスクリックを「自分宛てでない」と判断して
///   無視してしまう (キーボード/パッドはフォーカス窓へ届くので動くが、マウスは
///   カーソル下窓へ向かうため壊れる)。
///   <b>WS_EX_LAYERED | WS_EX_TRANSPARENT</b> の古典的クリックスルー窓は OS の
///   ヒットテスト/WindowFromPoint から完全に除外されるため、FF14 も Dalamud も
///   FFXIV 窓を通常通り認識し、マウスも UI も正常化する (PlatformHandle ハック
///   も不要になる)。代償は毎フレームの GPU→CPU 転送。
///
/// 参照: memory/reference_dalamud_imgui_pipeline.md
/// </summary>
internal sealed unsafe class MethodAOverlay : IDisposable
{
    private const string ClassName = "MaskedDalamudMethodAOverlay";

    private IntPtr _hwnd;
    private bool _classRegistered;
    private WndProcDelegate? _wndProc;
    private IntPtr _hInstance;

    private ID3D11Device* _device;          // FFXIV device (借用)
    private ID3D11DeviceContext* _ctx;       // FFXIV immediate context (借用)
    private ID3D11Texture2D* _rt;            // Dalamud が描く先 (BIND_RENDER_TARGET)
    private ID3D11RenderTargetView* _rtv;
    private ID3D11Texture2D* _staging;       // CPU READ 用

    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _dibBits;
    private IntPtr _oldDibObj;

    private uint _width, _height;
    private DXGI_FORMAT _format;  // ゲーム swapchain と同じフォーマットで RT を作る
    private bool _swapRB;         // RT が R8G8B8A8 のとき DIB(BGRA) へ詰める際 R/B 入替
    private bool _ready;
    private bool _shown;
    // teardown/リサイズで「解放すると危険」な D3D ポインタを保持しリークさせる
    // (use-after-free クラッシュ根絶のため意図的。デバイス破棄でまとめて回収)
    private readonly System.Collections.Generic.List<IntPtr> _retained = new();

    public IntPtr Hwnd => _hwnd;
    public bool IsReady => _ready;

    /// <summary>P3: CPU のピクセル変換を行単位で並列化する (呼出側が cfg から設定)。</summary>
    public bool ParallelCopy { get; set; } = true;

    // Z [試験]: 前回更新した UI 矩形 (残像消去のため union を取る)。
    private int _pRx, _pRy, _pRw, _pRh;
    private bool _lastRegion;

    /// <summary>FFXIV デバイスの除去理由 (HRESULT)。0=正常。
    /// 0x887A0005 DEVICE_REMOVED / 0x887A0006 DEVICE_HUNG /
    /// 0x887A0020 DRIVER_INTERNAL_ERROR / 0x887A0001 INVALID_CALL 等。
    /// どのコンポーネント起因でも、検知次第 Method A を安全停止し原因を記録する。</summary>
    public int GetDeviceRemovedReason()
        => _device != null ? _device->GetDeviceRemovedReason() : 0;
    /// <summary>差替に渡す ID3D11Texture2D* (Dalamud の mainViewport.renderTarget へ)。</summary>
    public IntPtr RenderTargetPtr => (IntPtr)_rt;
    /// <summary>差替に渡す ID3D11RenderTargetView* (mainViewport.renderTargetView へ)。</summary>
    public IntPtr RenderTargetViewPtr => (IntPtr)_rtv;

    public bool Create(IntPtr gameDevice, IntPtr gameCtx, DXGI_FORMAT format,
                       IntPtr ffxivHwnd, int x, int y, uint w, uint h)
    {
        if (_ready) return true;
        try
        {
            _device = (ID3D11Device*)gameDevice;
            _ctx = (ID3D11DeviceContext*)gameCtx;
            _width = Math.Max(1, w);
            _height = Math.Max(1, h);
            // ゲーム swapchain と同じフォーマットで RT を作る (Dalamud のブラー等が
            // 不一致でデバイス除去する問題の根治)。DIB は BGRA 固定なので、
            // RT が B8G8R8A8 以外 (= R8G8B8A8 等) なら CPU コピー時に R/B を入替。
            _format = format;
            _swapRB = format != DXGI_FORMAT_B8G8R8A8_UNORM;

            _hInstance = GetModuleHandle(null);
            if (!RegisterClassIfNeeded()) { Cleanup(); return false; }

            _hwnd = CreateWindowExW(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                ClassName, "MaskedDalamud Method A Overlay",
                WS_POPUP,
                x, y, (int)_width, (int)_height,
                IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) { Plugin.Log.Error($"[Method A] CreateWindowExW 失敗 err={Marshal.GetLastWin32Error()}"); Cleanup(); return false; }

            if (!SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE))
                Plugin.Log.Warning($"[Method A] SetWindowDisplayAffinity 失敗 err={Marshal.GetLastWin32Error()} (続行)");

            if (!CreateGpuResources()) { Cleanup(); return false; }
            if (!CreateDib()) { Cleanup(); return false; }

            uint myT = GetCurrentThreadId();
            uint fxT = ffxivHwnd != IntPtr.Zero ? GetWindowThreadProcessId(ffxivHwnd, out _) : 0;
            uint ovT = GetWindowThreadProcessId(_hwnd, out _);
            Plugin.Log.Info($"[Method A] Layered overlay 生成 OK hwnd=0x{_hwnd:X} {_width}x{_height} " +
                            $"thread(create={myT} overlay={ovT} ffxiv={fxT} 一致={(ovT == fxT && fxT != 0)})");
            _ready = true;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[Method A] Overlay.Create 例外: {ex}");
            Cleanup();
            return false;
        }
    }

    private bool CreateGpuResources()
    {
        // RT は **ゲーム swapchain と同じフォーマット** で作る。
        // Dalamud の Dx11Renderer はゲーム swapchain フォーマット (rtvFormat) 前提で
        // ブラー(Kawase)等の中間テクスチャ/SRV を扱うため、ここを B8G8R8A8 等に
        // すると、ログイン後にブラー付きプラグインウィンドウが出た瞬間に
        // フォーマット不一致パスが走り D3D デバイス除去 (DXGI_DEVICE_REMOVED) で
        // ゲームごとクラッシュする。CPU コピー時に DIB(BGRA) へ詰める際、
        // 必要なら R/B を入替える (_swapRB)。
        var td = new D3D11_TEXTURE2D_DESC
        {
            Width = _width,
            Height = _height,
            MipLevels = 1,
            ArraySize = 1,
            Format = _format,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
            // RENDER_TARGET だけでなく SHADER_RESOURCE も付与する。
            // Dalamud の Dx11Renderer は ImGui ウィンドウの背景ブラー(Kawase)で
            // レンダーターゲットを SRV としてサンプリングするため、SHADER_RESOURCE
            // が無いと CreateShaderResourceView が失敗 → D3D デバイス除去
            // (DXGI_DEVICE_REMOVED) でゲームごとクラッシュする (ログイン後に
            // ブラー付きプラグイン窓が出た瞬間に顕在化)。ゲーム swapchain の
            // backbuffer も同様に RT|SRV で作られている。
            BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET
                             | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE),
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };
        ID3D11Texture2D* rt;
        if (_device->CreateTexture2D(&td, null, &rt) != 0 || rt == null)
        { Plugin.Log.Error("[Method A] RT CreateTexture2D 失敗"); return false; }
        _rt = rt;

        ID3D11RenderTargetView* rtv;
        if (_device->CreateRenderTargetView((ID3D11Resource*)_rt, null, &rtv) != 0 || rtv == null)
        { Plugin.Log.Error("[Method A] CreateRenderTargetView 失敗"); return false; }
        _rtv = rtv;

        var sd = td;
        sd.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        sd.BindFlags = 0;
        sd.CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        ID3D11Texture2D* st;
        if (_device->CreateTexture2D(&sd, null, &st) != 0 || st == null)
        { Plugin.Log.Error("[Method A] staging CreateTexture2D 失敗"); return false; }
        _staging = st;
        return true;
    }

    private bool CreateDib()
    {
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = (int)_width;
        bmi.bmiHeader.biHeight = -(int)_height; // top-down
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0; // BI_RGB

        IntPtr bits;
        _dib = CreateDIBSection(IntPtr.Zero, ref bmi, 0u, out bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero || bits == IntPtr.Zero)
        { Plugin.Log.Error("[Method A] CreateDIBSection 失敗"); return false; }
        _dibBits = bits;

        _memDc = CreateCompatibleDC(IntPtr.Zero);
        if (_memDc == IntPtr.Zero) { Plugin.Log.Error("[Method A] CreateCompatibleDC 失敗"); return false; }
        _oldDibObj = SelectObject(_memDc, _dib);
        return true;
    }

    /// <summary>Dalamud が RT に描いた内容を CPU へ取り出し、premultiply 済の
    /// まま (二重 premultiply 回避) レイヤードウィンドウへ反映する。
    /// rx&lt;0 = 全画面処理。rx&gt;=0 = Z[試験]: 指定矩形のみ読み戻し/変換し、
    /// 前回矩形との union を 0 クリアして残像を防ぐ。</summary>
    public void UpdateLayered(int screenX, int screenY,
                              int rx = -1, int ry = -1, int rw = -1, int rh = -1)
    {
        if (!_ready || !_shown) return;
        try
        {
            int W = (int)_width, H = (int)_height;
            bool region = rx >= 0;
            // フル→領域 への切替直後は全消去が必要 (前回フル内容が残るため)。
            bool forceFull = region && !_lastRegion;

            if (region)
            {
                // 新矩形をクランプ
                rx = Math.Clamp(rx, 0, W); ry = Math.Clamp(ry, 0, H);
                rw = Math.Clamp(rw, 0, W - rx); rh = Math.Clamp(rh, 0, H - ry);
                // 新領域のみ GPU から staging へ部分コピー
                if (rw > 0 && rh > 0)
                {
                    var box = new D3D11_BOX
                    {
                        left = (uint)rx, top = (uint)ry, front = 0,
                        right = (uint)(rx + rw), bottom = (uint)(ry + rh), back = 1,
                    };
                    _ctx->CopySubresourceRegion((ID3D11Resource*)_staging, 0,
                        (uint)rx, (uint)ry, 0, (ID3D11Resource*)_rt, 0, &box);
                }
            }
            else
            {
                _ctx->CopyResource((ID3D11Resource*)_staging, (ID3D11Resource*)_rt);
            }

            D3D11_MAPPED_SUBRESOURCE map;
            if (_ctx->Map((ID3D11Resource*)_staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &map) != 0)
                return;
            try
            {
                byte* src = (byte*)map.pData;
                byte* dst = (byte*)_dibBits;
                uint srcPitch = map.RowPitch;
                uint dstPitch = _width * 4;
                // DIB は常に BGRA (byte0=B,1=G,2=R,3=A)。RT が R8G8B8A8 のときは
                // src が R,G,B,A 並びなので R/B を入替える (_swapRB)。
                int bi = _swapRB ? 2 : 0; // src 内の B のオフセット
                int ri = _swapRB ? 0 : 2; // src 内の R のオフセット
                uint width = _width;

                // 1 行分の変換 ([x0,x1) 範囲のみ・各行独立=並列化可能)。
                // RenderDrawDataInternal は透明クリア+標準 ImGui ブレンドで描くため
                // RT の RGB は既に premultiplied alpha → straight コピーが正
                // (再 *a すると二重 premultiply で半透明が色化け)。
                void RowRange(int yy, int x0, int x1)
                {
                    byte* s = src + (uint)yy * srcPitch + (uint)x0 * 4;
                    byte* d = dst + (uint)yy * dstPitch + (uint)x0 * 4;
                    for (int xx = x0; xx < x1; xx++)
                    {
                        byte a = s[3];
                        if (a == 0) *(uint*)d = 0;
                        else { d[0] = s[bi]; d[1] = s[1]; d[2] = s[ri]; d[3] = a; }
                        s += 4; d += 4;
                    }
                }

                if (!region)
                {
                    if (ParallelCopy && H >= 64)
                        Parallel.For(0, H, yy => RowRange(yy, 0, W));
                    else
                        for (int yy = 0; yy < H; yy++) RowRange(yy, 0, W);
                }
                else
                {
                    // 消去すべき union 矩形 (前回領域 ∪ 新領域、または強制全面)
                    int cx0, cy0, cx1, cy1;
                    if (forceFull) { cx0 = 0; cy0 = 0; cx1 = W; cy1 = H; }
                    else
                    {
                        cx0 = Math.Min(rx, _pRx); cy0 = Math.Min(ry, _pRy);
                        cx1 = Math.Max(rx + rw, _pRx + _pRw);
                        cy1 = Math.Max(ry + rh, _pRy + _pRh);
                        cx0 = Math.Clamp(cx0, 0, W); cy0 = Math.Clamp(cy0, 0, H);
                        cx1 = Math.Clamp(cx1, 0, W); cy1 = Math.Clamp(cy1, 0, H);
                    }
                    int clrBytes = Math.Max(0, (cx1 - cx0)) * 4;
                    int nx1 = rx + rw, ny1 = ry + rh;

                    void RegionRow(int yy)
                    {
                        // union 行を 0 クリア
                        if (clrBytes > 0)
                            Unsafe.InitBlockUnaligned(
                                dst + (uint)yy * dstPitch + (uint)cx0 * 4, 0, (uint)clrBytes);
                        // 新領域に含まれる行はその範囲だけ変換で上書き
                        if (rw > 0 && yy >= ry && yy < ny1)
                            RowRange(yy, rx, nx1);
                    }

                    if (cy1 > cy0)
                    {
                        if (ParallelCopy && (cy1 - cy0) >= 64)
                            Parallel.For(cy0, cy1, RegionRow);
                        else
                            for (int yy = cy0; yy < cy1; yy++) RegionRow(yy);
                    }
                    _pRx = rx; _pRy = ry; _pRw = rw; _pRh = rh;
                }
                _lastRegion = region;
            }
            finally
            {
                _ctx->Unmap((ID3D11Resource*)_staging, 0);
            }

            var size = new SIZE { cx = (int)_width, cy = (int)_height };
            var ptDst = new POINT { x = screenX, y = screenY };
            var ptSrc = new POINT { x = 0, y = 0 };
            var blend = new BLENDFUNCTION
            {
                BlendOp = 0,        // AC_SRC_OVER
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = 1,    // AC_SRC_ALPHA
            };
            UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref ptDst, ref size,
                _memDc, ref ptSrc, 0, ref blend, ULW_ALPHA);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Method A] UpdateLayered 例外: {ex.Message}");
        }
    }

    /// <summary>FF14 前面状態に応じ表示/非表示。サイズ変化なら GPU/DIB 作り直し、
    /// true を返す (呼び出し側は reflection の RT/RTV を貼り直すべき)。</summary>
    public bool SetVisibleAndSize(bool ffxivForeground, uint w, uint h)
    {
        if (!_ready) return false;
        w = Math.Max(1, w); h = Math.Max(1, h);

        if (!ffxivForeground)
        {
            if (_shown) { ShowWindow(_hwnd, SW_HIDE); _shown = false; }
            return false;
        }

        bool resized = false;
        if (w != _width || h != _height)
        {
            // 旧 GPU リソースは Release しない (Dalamud/GPU がまだ前フレームで
            // 参照している可能性があり、即解放すると use-after-free でドライバが
            // クラッシュする)。デバイス破棄まで保持＝有界リーク。リサイズは稀。
            var oldRt = _rt; var oldRtv = _rtv; var oldStaging = _staging;
            _rt = null; _rtv = null; _staging = null;
            _width = w; _height = h;
            ReleaseDib();
            if (!CreateGpuResources() || !CreateDib())
            {
                Plugin.Log.Error("[Method A] リサイズ時の再生成失敗 → 安全停止");
                _ready = false;
                return false;
            }
            _retained.Add((IntPtr)oldRt);
            _retained.Add((IntPtr)oldRtv);
            _retained.Add((IntPtr)oldStaging);
            // 新 DIB はゼロ初期化。領域限定の残像追跡もリセットし、次の領域
            // 更新は強制全面クリア (forceFull) になるようにする。
            _lastRegion = false; _pRx = _pRy = _pRw = _pRh = 0;
            _posX = _posY = int.MinValue; // サイズ変化 → 次の SyncPosition で再適用
            resized = true;
        }
        if (!_shown) { ShowWindow(_hwnd, SW_SHOWNOACTIVATE); _shown = true; }
        return resized;
    }

    /// <summary>表示/非表示のみを冪等に切替える (D3D は触らない)。
    /// FF14 が裏でも回る Framework.Update から呼べるよう軽量・安全。</summary>
    public void SetShown(bool show)
    {
        if (!_ready || _hwnd == IntPtr.Zero) return;
        if (show && !_shown) { ShowWindow(_hwnd, SW_SHOWNOACTIVATE); _shown = true; }
        else if (!show && _shown) { ShowWindow(_hwnd, SW_HIDE); _shown = false; }
    }

    private int _posX = int.MinValue, _posY = int.MinValue;

    /// <summary>位置だけを即追従させる軽量版 (内容更新スロットルとは独立)。
    /// レイヤードウィンドウは SetWindowPos で移動可能 (ビットマップは保持)。
    /// ウィンドウドラッグ時に内容更新を待たず窓が貼り付くようにする。</summary>
    public void SyncPosition(int x, int y)
    {
        if (!_ready || !_shown || _hwnd == IntPtr.Zero) return;
        if (x == _posX && y == _posY) return;
        _posX = x; _posY = y;
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, (int)_width, (int)_height, SWP_NOACTIVATE);
    }

    public void Dispose() => Cleanup();

    private void ReleaseGpu()
    {
        if (_rtv != null) { _rtv->Release(); _rtv = null; }
        if (_rt != null) { _rt->Release(); _rt = null; }
        if (_staging != null) { _staging->Release(); _staging = null; }
    }

    private void ReleaseDib()
    {
        if (_memDc != IntPtr.Zero)
        {
            if (_oldDibObj != IntPtr.Zero) { SelectObject(_memDc, _oldDibObj); _oldDibObj = IntPtr.Zero; }
            DeleteDC(_memDc); _memDc = IntPtr.Zero;
        }
        if (_dib != IntPtr.Zero) { DeleteObject(_dib); _dib = IntPtr.Zero; }
        _dibBits = IntPtr.Zero;
    }

    /// <summary>
    /// teardown 用の安全な破棄: ウィンドウと GDI のみ破棄し、
    /// D3D リソース (RT/RTV/staging) は **解放しない**。
    /// Dalamud / GPU が直前フレームでまだ参照している可能性があり、即解放すると
    /// NVIDIA ドライバ内で use-after-free (C0000005) クラッシュするため、
    /// デバイス破棄 (ゲーム終了) までリークさせる (有界・トグル毎数MB)。
    /// </summary>
    public void DisposeWindowOnly()
    {
        _ready = false; _shown = false;
        try { ReleaseDib(); } catch { }
        try { if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; } } catch { }
        // _rt/_rtv/_staging は意図的に解放しない (use-after-free 根絶)
        _device = null; _ctx = null; // 借用なので Release しない
        Plugin.Log.Info("[Method A] Layered overlay 破棄 (D3D は安全のため保持)");
    }

    /// <summary>
    /// 非ブロッキングの破棄要求。プラグイン無効化(Dispose)が Framework スレッド
    /// 以外で走るとき、クロススレッド同期待ち (RunOnFrameworkThread().GetResult())
    /// は WaitBeforeDispose 中の Framework 停止とデッドロックし、ゲームが
    /// 無応答で落ちる。これを根絶するため、ここでは待たずに WM_CLOSE を
    /// PostMessage するだけ。所有スレッド (FF14 メイン) が自前メッセージポンプで
    /// DefWindowProc(WM_CLOSE)→DestroyWindow を実行する。DIB/D3D は安全のため
    /// 解放しない (有界リーク)。
    /// </summary>
    public void RequestCloseNonBlocking()
    {
        _ready = false; _shown = false;
        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                ShowWindow(_hwnd, SW_HIDE);          // 即座に不可視 (残像防止)
                PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                _hwnd = IntPtr.Zero;                 // 以後触らない (所有スレッドが破棄)
            }
        }
        catch { }
        _device = null; _ctx = null;
        Plugin.Log.Info("[Method A] Layered overlay 破棄要求 (非ブロッキング/WM_CLOSE)");
    }

    private void Cleanup()
    {
        _ready = false; _shown = false;
        try { ReleaseGpu(); } catch { }
        try { ReleaseDib(); } catch { }
        try { if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; } } catch { }
        _device = null; _ctx = null; // 借用なので Release しない
        Plugin.Log.Info("[Method A] Layered overlay 破棄");
    }

    private bool RegisterClassIfNeeded()
    {
        if (_classRegistered) return true;
        _wndProc = StaticWndProc;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInstance,
            lpszClassName = ClassName,
        };
        if (RegisterClassExW(ref wc) == 0)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != 1410) return false;
        }
        _classRegistered = true;
        return true;
    }

    // WS_EX_LAYERED|WS_EX_TRANSPARENT が OS レベルでクリックスルー+WindowFromPoint
    // 除外を担うため、WndProc は素の DefWindowProc でよい。
    private static IntPtr StaticWndProc(IntPtr h, uint m, IntPtr w, IntPtr l)
        => DefWindowProcW(h, m, w, l);

    // ===== Win32 / GDI =====
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_HIDE = 0;
    private const uint WM_CLOSE = 0x0010;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint ULW_ALPHA = 0x00000002;

    private delegate IntPtr WndProcDelegate(IntPtr h, uint m, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight;
        public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? n);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr h, uint aff);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst,
        ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage,
        out IntPtr ppvBits, IntPtr hSection, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
}
