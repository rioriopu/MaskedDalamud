using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Dalamud.Bindings.ImGui;
using TerraFX.Interop.DirectX;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MaskedDalamud;

/// <summary>
/// [試験・独立] KamiToolKit 製ネイティブ UI を配信から隠す (方式A: ネイティブ透過 + ミラー描画)。
///
/// 背景: JobBars / AetherBars 等が使う KamiToolKit は AtkResNode を直接操作してゲームの UI レイヤーへ
/// 描く。これはゲームの UI 描画パスで backbuffer に焼かれるため、通常のキャプチャ除外では分離できない
/// ([[maskeddalamud-arch-constraints]] §5)。
///
/// 方針:
///   ① 対象をネイティブ側でアルファ 0 にする → 配信からも手元からも消える
///   ② その見た目を我々が描き直す         → キャプチャ除外オーバーレイに乗り手元だけに見える
///
/// ②の描画は 2 系統:
///   ・画像 … <see cref="AtkQuadRenderer"/> が Atk の合成式どおりに専用シェーダで描く (厳密)
///   ・文字 … ImGui + ゲーム本体と同じ Axis フォントで描く
/// 厳密描画が使えない環境では画像も ImGui の近似 (乗算 tint のみ) にフォールバックする。
///
/// 識別: KamiToolKit は生成する全ノードへ `NodeId = 100_000_000 + n` を付与する。
/// 加えて JobBars 等は専用アドオン `KTK_Overlay_*` を作ってそこへ描くため、アドオン名でも判定する。
///
/// 可視フラグ (ToggleVisibility) は使わない。触ると所有プラグインの表示ロジックを壊すため
/// (隠した瞬間 IsVisible が false になり「本来出したいか」を読めなくなる)。**アルファのみ**を操作する。
///
/// 既知の限界 ([[maskeddalamud-native-ui-research]]): 回転・クリッピング・パーツ切り替えアニメ・
/// ブレンドモードは未対応のため**見た目は完全一致しない**。
/// </summary>
internal sealed unsafe class KamiMirror : IDisposable
{
    /// <summary>KamiToolKit が全ノードへ付与する NodeId の基準値。ゲーム本来のノードは桁が小さい。</summary>
    private const uint NodeIdBase = 100_000_000;

    /// <summary>プラグインが専用に作るオーバーレイアドオン名。既存アドオンには相乗りしない。
    ///
    /// KTK_Overlay_* … KamiToolKit (KamiToolKit/Enums/OverlayLayer.cs)。JobBars/AetherBars のゲージ等。
    /// BMR_Overlay_Back … BossModReborn の『Enable projecting radar into the 3D world』。
    ///   KamiToolKit そのものは使っていないが、同じ設計の自前実装 (BossMod/Framework/WorldOverlayNode.cs)。
    ///   レーダーをオフスクリーンに描いてから、全画面 1 枚の AtkImageNode として貼る。
    /// </summary>
    private static readonly string[] OverlayAddonNames =
    {
        "KTK_Overlay_Back", "KTK_Overlay_Middle", "KTK_Overlay_Higher", "KTK_Overlay_Front",
        "BMR_Overlay_Back",
    };

    /// <summary>KamiToolKit のノード表に載らないオーバーレイの所有プラグイン。
    /// 表に載らないと所有者が「(不明)」になり、プラグイン選別で弾かれてしまうため、
    /// アドオン名から引けるようにしておく。</summary>
    private static readonly Dictionary<string, string> AddonOwners = new()
    {
        ["BMR_Overlay_Back"] = "BossModReborn",
    };

    /// <summary>SimpleTweaks が自作ノードへ振る NodeId の範囲 (SimpleTweaksPlugin/Utility/CustomNodes.cs)。
    ///
    ///   SimpleTweaksNodeBase = 0x53540000;   // 固定 ID の基点
    ///   private static uint _nextId = 0x53541000;  // 動的 ID。16 ずつ増える
    ///
    /// SimpleTweaks は専用オーバーレイを作らず、ゲームのアドオン (_CastBar など) へ
    /// 直接ノードを足すため、アドオン名からは所有者を引けない。上位 16bit で振り分ける。</summary>
    private const uint SimpleTweaksNodeMask = 0xFFFF0000;
    private const uint SimpleTweaksNodeBase = 0x53540000;

    /// <summary>NodeId の範囲から所有プラグインを引く。該当しなければ null。</summary>
    private static string? OwnerByNodeId(uint nodeId)
        => (nodeId & SimpleTweaksNodeMask) == SimpleTweaksNodeBase ? "SimpleTweaksPlugin" : null;

    private readonly Plugin _plugin;

    /// <summary>1 フレーム分の描画スナップショット (Framework スレッドで作り描画スレッドで使う)。</summary>
    private struct Item
    {
        public float X, Y, W, H;
        public uint Color;          // ImGui ABGR (テキスト用 / 近似フォールバック用)
        public nint TextureSrv;     // 画像ノードのテクスチャ (無ければ 0)
        public float U0, V0, U1, V1;
        public string? Text;        // テキストノードの文字列
        public float FontSize;
        public FontType Font;       // Axis / TrumpGothic 等 (字形が違うので種別ごとに描く)
        public AlignmentType Align; // 矩形内の寄せ方 (右寄せ・中央寄せだと開始位置がずれる)
        public bool HasEdge;        // テキストの縁取り
        public uint EdgeColor;
        public TextFlags TextFlags; // Bold / Italic / Glare / Emboss 等

        /// <summary>カウンタ (AtkCounterNode) の 1 文字ぶん。数字はパーツ番号で指定される。</summary>
        public bool Counter;

        /// <summary>クリッピング矩形 (画面座標)。W が 0 なら制限なし。</summary>
        public float ClipX, ClipY, ClipW, ClipH;

        // Atk の合成式をそのまま渡すための値 (祖先ぶんまで合成済み)。
        //   out.rgb = texel.rgb × M + A   /   out.a = texel.a × MA
        public float MR, MG, MB, MA;
        public float AR, AG, AB;    // 符号付き (負 = 暗くする)

        // 9 分割 (NineGrid) 用。Slice=true のとき四辺の余白を保ったまま中央だけを伸ばす。
        public bool Slice;
        public float SliceL, SliceR, SliceT, SliceB;   // パーツ内のピクセル単位マージン

        /// <summary>祖先ぶんまで積んだ拡大率。9 分割のマージンを画面上の大きさへ
        /// 直すのに使う (マージンはテクスチャのピクセル数なので、そのままでは
        /// 「高解像度時の UI サイズ設定」を上げたときに四隅だけ小さいままになる)。</summary>
        public float ScaleX, ScaleY;
        public float PartW, PartH;                     // パーツの実ピクセルサイズ
        public float TexW, TexH;                       // テクスチャ全体の実ピクセルサイズ
        public float PartU, PartV;                     // パーツ左上のテクスチャ座標 (ピクセル)

        // NineGrid 専用。AtkNineGridNode は BlendMode(@212) と PartsTypeRenderType(@216) を持つ。
        // PartsTypeRenderType の bit2 (=4, JobBars でいう RenderType) が立っているとき
        // ノード側の BlendMode が採用される。JobBars のゲージ中身はこの経路。
        public uint BlendMode;
        public byte PartsRenderType;
        /// <summary>テクスチャの繰り返し方 (0=Clamp / 1=Tile / 2=Stretch 等)。
        /// JobBars は背景・枠・セパレータに Tile を指定している。</summary>
        public byte WrapMode;
        /// <summary>UV が 0..1 の外へ出る繰り返し描画。サンプラを WRAP に切り替える必要がある。</summary>
        public bool Tile;
        public string? NomTex;      // [診断] 公称サイズ/ActualSize (UV 分母の切り分け用)

        // 回転 (ラジアン) と回転中心 (画面座標)。JobBars のセパレータは π/2 回転している。
        public float Rotation;
        public float OriginX, OriginY;
    }

    private readonly List<Item> _items = new();
    private readonly List<string> _diag = new();
    /// <summary>[診断] 可視判定で描画対象から外したノード。描き落としの切り分け用。</summary>
    private readonly List<string> _skipped = new();
    public IReadOnlyList<string> SkippedDiagnostics => _skipped;
    public int LastSkippedCount { get; private set; }
    // ── スナップショットが持つテクスチャの寿命 ──
    //
    // TextureSrv はゲームが持つ ID3D11ShaderResourceView の生ポインタ。
    // Framework スレッドで拾い、**後のフレームの描画スレッドで使う**ため、
    // その間にゲームがテクスチャを解放/作り直すと解放済みメモリを読むことになる。
    // アイコンの読み込みや入れ替えが走ると壊れた絵が出る (ホットバーで多発との報告)。
    // 参照カウントを自分で 1 つ握り、スナップショットを捨てるときに返す。

    /// <summary>IUnknown::AddRef (vtable[1])。</summary>
    private static void ComAddRef(nint p)
    {
        if (p == 0) return;
        var vtbl = *(nint**)p;
        ((delegate* unmanaged[Stdcall]<nint, uint>)vtbl[1])(p);
    }

    /// <summary>IUnknown::Release (vtable[2])。</summary>
    private static void ComRelease(nint p)
    {
        if (p == 0) return;
        var vtbl = *(nint**)p;
        ((delegate* unmanaged[Stdcall]<nint, uint>)vtbl[2])(p);
    }

    /// <summary>スナップショットへ 1 件積む。テクスチャは参照を 1 つ握ってから入れる。</summary>
    private void AddItem(in Item item)
    {
        ComAddRef(item.TextureSrv);
        _items.Add(item);
    }

    /// <summary>スナップショットを捨てる。握っていた参照は必ず返す。</summary>
    private void ClearItems()
    {
        foreach (var it in _items) ComRelease(it.TextureSrv);
        _items.Clear();
    }

    /// <summary>_items は Framework スレッドで作り、描画スレッド (Before/Draw) で読むので保護する。</summary>
    private readonly object _gate = new();

    // 透過させたノード → 元のアルファ値 (復元用)。
    private readonly Dictionary<nint, byte> _dimmed = new();
    // テキストノード → 元の (文字色アルファ, 縁色アルファ)。Color.A とは別系統なので分けて持つ。
    private readonly Dictionary<nint, (byte Text, byte Edge)> _dimmedText = new();

    /// <summary>今フレームの走査で**実際に生存を確認した**ノード。
    ///
    /// これが安全性の要。所有プラグイン (JobBars 等) はゲージを作っては壊すため、
    /// 退避辞書に生ポインタを溜め続けると**解放済みメモリへ書き込む** (use-after-free)。
    /// 2026-09-11 の `AtkUldManager::Update` でのアクセス違反はこれが原因。
    /// **今フレーム辿って見つけたノード以外へは絶対に書かない**という規則にし、
    /// 見なくなったノードの退避情報は毎フレーム捨てる。</summary>
    /// <summary>走査中に有効なクリッピング矩形 (AtkClippingMaskNode が設定する)。
    /// 枝を抜けたら解除する。</summary>
    private (float X0, float Y0, float X1, float Y1)? _clip;

    private readonly HashSet<nint> _seen = new();
    private readonly List<nint> _pruneTmp = new();
    private int _skipped_n;

    /// <summary>[診断] アドオン別の検出枝数。「どこを捕まえているか」を一目で確認するため。
    /// KTK_Overlay_* が出ていなければ JobBars 等のゲージは掴めていない。</summary>
    private readonly Dictionary<string, int> _byAddon = new();
    private string _addonSummary = "";
    public string AddonSummary => _addonSummary;

    private bool _active;
    /// <summary>キャプチャ除外が止まっているあいだ待機中か (ノードに触らない)。</summary>
    private bool _suspended;

    public bool Active => _active;
    public bool Suspended => _suspended;
    public int LastNodeCount { get; private set; }
    public int LastDrawCount { get; private set; }
    public int LastQuadCount { get; private set; }
    /// <summary>直近の Draw() で厳密モード (シェーダ描画) が有効だったか。
    /// false のまま シェーダ側も走ると**二重描画**になり色が飛ぶので、診断で見えるようにする。</summary>
    public bool LastExact { get; private set; }
    public string? LastError { get; private set; }
    public IReadOnlyList<string> Diagnostics => _diag;

    /// <summary>画像ノードを <see cref="AtkQuadRenderer"/> が描くか (= <see cref="Draw"/> は描かない)。
    ///
    /// **この判定を「前フレームの結果」で行ってはいけない。**
    /// 1 フレームの流れは Draw() → Before() の順なので、Before() が立てた値を Draw() が読むと
    /// 常に 1 フレーム古い値を見ることになる。厳密モードが落ちたフレームでは
    /// 「ImGui も描かない・シェーダも描かない」となり、**手元から UI が丸ごと消える**。
    /// そのため毎回その場で条件を評価する。</summary>
    private bool ExactNow
        => _plugin.cfg.kamiExactColor
        && (
               // DComp: Before() が backbuffer へ描き、差分キャプチャが拾う
               (_plugin.cfg.dcompCaptureMode != 0 && (_plugin.dcompOverlay?.ExactMirrorReady ?? false))
               // GPU-GDI: オーバーレイ用テクスチャへ直接描く (リプレイより前)
            || (_plugin.gdiScrub?.ExactMirrorReady ?? false)
               // 検証モード: AtkMirrorPreview が backbuffer へ描く (キャプチャ除外は停止中)
            || (_plugin.mirrorPreview?.IsActive ?? false)
           );

    public KamiMirror(Plugin plugin) => _plugin = plugin;

    // ============================== 所有プラグインの特定 ==============================

    /// <summary>KamiToolKit がノードの所有者を記録している共有辞書 (ノード → 生成した Type)。
    ///
    /// KTK 側の実装:
    /// <code>
    ///   AllocatedNodes = PluginInterface.GetOrCreateData("TypeMappedCustomNodes",
    ///                        () => new ConcurrentDictionary&lt;nint, Type&gt;());
    ///   // NodeBase のコンストラクタで TryAdd((nint)Node, GetType())
    /// </code>
    /// Dalamud のプラグイン間共有データなので、**KamiToolKit へ依存せず読める**。</summary>
    private System.Collections.Concurrent.ConcurrentDictionary<nint, Type>? _ktkNodes;
    private bool _ktkLookupTried;
    private readonly Dictionary<Type, string> _ownerNameCache = new();
    /// <summary>[診断] 今フレームに見つけた所有プラグインと枝数。</summary>
    private readonly Dictionary<string, int> _owners = new();
    public IReadOnlyDictionary<string, int> DetectedOwners => _owners;

    /// <summary>[調査] 次のフレームで診断内容を全件ファイルへ書き出す要求。
    /// 画面の一覧は件数に上限があるため、解析にはこちらを使う。</summary>
    public bool DumpDiagRequested { get; set; }
    public string DumpDiagResult { get; private set; } = "";
    private bool _dumpAll;

    private void EnsureKtkTable()
    {
        if (_ktkLookupTried) return;
        _ktkLookupTried = true;
        try
        {
            if (Plugin.PluginInterface.TryGetData<System.Collections.Concurrent.ConcurrentDictionary<nint, Type>>(
                    "TypeMappedCustomNodes", out var d))
                _ktkNodes = d;
        }
        catch (Exception ex) { LastError = $"KTK ノード表の取得に失敗: {ex.Message}"; }
    }

    /// <summary>Type の置かれている場所からプラグイン名を割り出す。
    /// Dalamud は `installedPlugins\&lt;名前&gt;\&lt;版&gt;\*.dll` という構成なので
    /// DLL の 2 つ上のフォルダ名がプラグイン名になる。</summary>
    private string OwnerNameOf(Type t)
    {
        if (_ownerNameCache.TryGetValue(t, out var cached)) return cached;
        string name;
        try
        {
            var loc = t.Assembly.Location;
            if (string.IsNullOrEmpty(loc)) name = t.Assembly.GetName().Name ?? "(不明)";
            else
            {
                var verDir = System.IO.Path.GetDirectoryName(loc);
                var plugDir = verDir != null ? System.IO.Path.GetDirectoryName(verDir) : null;
                name = plugDir != null ? System.IO.Path.GetFileName(plugDir) : (t.Assembly.GetName().Name ?? "(不明)");
                // devPlugins 等で版フォルダが無い構成へのフォールバック
                if (string.IsNullOrEmpty(name) || name is "installedPlugins" or "devPlugins")
                    name = System.IO.Path.GetFileNameWithoutExtension(loc);
            }
        }
        catch { name = "(不明)"; }
        if (_ownerNameCache.Count < 256) _ownerNameCache[t] = name;
        return name;
    }

    /// <summary>枝の所有プラグイン名。枝の根から数階層を見て、最初に登録済みのノードで判定する
    /// (根そのものが KTK 管理とは限らないため)。</summary>
    private string? BranchOwner(AtkResNode* node, int depth = 0)
    {
        if (node == null || depth > 4 || _ktkNodes == null) return null;
        if (_ktkNodes.TryGetValue((nint)node, out var t)) return OwnerNameOf(t);
        for (var c = node->ChildNode; c != null; c = c->PrevSiblingNode)
        {
            var r = BranchOwner(c, depth + 1);
            if (r != null) return r;
        }
        return null;
    }

    public void Enable()
    {
        if (_active) return;
        _active = true;
        LastError = null;
        Plugin.Log.Info("[KamiMirror] 有効化 (ネイティブ透過 + ミラー描画)");
    }

    public void Disable()
    {
        if (!_active) return;
        _active = false;
        _suspended = false;
        lock (_gate) ClearItems();
        ClearReapplyListeners();
        RestoreAll();
        LastNodeCount = LastDrawCount = LastQuadCount = 0;
        _addonSummary = "";
        Plugin.Log.Info("[KamiMirror] 無効化・全ノード復元");
    }

    // ============================== フォント ==============================

    private readonly Dictionary<FontType, IFontHandle?> _fonts = new();
    private bool _fontFailed;

    /// <summary>KamiToolKit / Atk のフォント種別を Dalamud のゲームフォント族へ対応付ける。
    ///
    /// `AtkTextNode.FontType` は 6 種類あり、KamiToolKit も用途で使い分けている
    /// (TextNode 既定=Axis / TextNineGridNode 既定=TrumpGothic / 選択リスト=MiedingerMed)。
    /// すべて Axis で描くと**数字の字形が明確に違う**ので種別ごとに持つ。</summary>
    private static GameFontFamily MapFamily(FontType f) => f switch
    {
        FontType.MiedingerMed => GameFontFamily.MiedingerMid,
        FontType.Miedinger    => GameFontFamily.Meidinger,
        FontType.TrumpGothic  => GameFontFamily.TrumpGothic,
        FontType.Jupiter      => GameFontFamily.Jupiter,
        FontType.JupiterLarge => GameFontFamily.Jupiter,
        _                     => GameFontFamily.Axis,
    };

    /// <summary>フォント種別ごとのハンドルを遅延生成する。
    /// 大きめ (36px) で生成し、描画時に各ノードの FontSize へ縮小して使う
    /// (小さいフォントを拡大するとぼやけるため。DTR 実装時の知見 [[maskeddalamud-dtr-imgui]])。</summary>
    private IFontHandle? EnsureFont(FontType type)
    {
        if (_fontFailed) return null;
        if (_fonts.TryGetValue(type, out var cached)) return cached;
        IFontHandle? h = null;
        try
        {
            h = Plugin.PluginInterface.UiBuilder.FontAtlas
                .NewGameFontHandle(new GameFontStyle(MapFamily(type), 36f));
        }
        catch (Exception ex)
        {
            _fontFailed = true;
            LastError = $"ゲームフォント取得失敗: {ex.Message}";
            Plugin.Log.Warning($"[KamiMirror] ゲームフォントを取得できません → 既定フォントで描画: {ex.Message}");
        }
        _fonts[type] = h;
        return h;
    }

    /// <summary>テキストの配置に応じた描画開始位置のずれを求める。
    ///
    /// Atk はノードの矩形 (Width×Height) の中で <c>AlignmentType</c> に従って文字を置く。
    /// 我々はこれまでノード左上へ描いていたため、**右寄せ・中央寄せの文字がずれていた**
    /// (KamiToolKit の CounterNode / TextNineGridNode は既定が右寄せ)。</summary>
    private static System.Numerics.Vector2 AlignOffset(AlignmentType align, float boxW, float boxH,
                                                       System.Numerics.Vector2 textSize)
    {
        float dx = align switch
        {
            AlignmentType.Top or AlignmentType.Center or AlignmentType.Bottom => (boxW - textSize.X) * 0.5f,
            AlignmentType.TopRight or AlignmentType.Right or AlignmentType.BottomRight => boxW - textSize.X,
            _ => 0f,   // TopLeft / Left / BottomLeft
        };
        float dy = align switch
        {
            AlignmentType.Left or AlignmentType.Center or AlignmentType.Right => (boxH - textSize.Y) * 0.5f,
            AlignmentType.BottomLeft or AlignmentType.Bottom or AlignmentType.BottomRight => boxH - textSize.Y,
            _ => 0f,   // TopLeft / Top / TopRight
        };
        return new System.Numerics.Vector2(dx, dy);
    }

    // ============================== 色の合成 ==============================

    /// <summary>走査中に階層で積んでいく値。
    ///
    /// スケールは**祖先ぶんを掛けないと大きさが合わない**。`ScreenX/Y` は累積済みだが
    /// `Width × ScaleX` は自分のスケールしか見ていないため、HUD スケールの掛かった
    /// ゲームアドオン (`_ActionBar` 等) に相乗りしたノードで大きさがずれる。
    /// 専用オーバーレイは祖先が等倍なので、これまで表面化していなかった。</summary>
    private struct Ctx
    {
        public float Alpha, ScaleX, ScaleY;
        public static Ctx Root(float a, float sx, float sy) => new() { Alpha = a, ScaleX = sx, ScaleY = sy };
        /// <summary>子へ積む。スケールは**隠す前の値**を渡すこと
        /// (スケール 0 で隠している間、ノードから読むと倍率を見失う)。</summary>
        public Ctx With(float alpha, float sx, float sy) => new()
        {
            Alpha = alpha,
            ScaleX = ScaleX * (sx != 0 ? sx : 1f),
            ScaleY = ScaleY * (sy != 0 ? sy : 1f),
        };
    }

    /// <summary>Atk の色合成パラメータ。texel に対して <c>texel × M + A</c> を適用する。</summary>
    private struct Tint
    {
        public float MR, MG, MB, MA;
        public float AR, AG, AB;
        public static Tint Identity => new() { MR = 1f, MG = 1f, MB = 1f, MA = 1f };
    }

    /// <summary>描画に使う色合成パラメータ。
    ///
    /// **ゲームが継承計算済みの「実効値」をそのまま読む。**
    /// Atk は親→子の色伝播を毎フレーム自前で計算し、結果を別フィールドへ書き戻している
    /// (`sub_14066B1F0` を逆アセンブルして確定):
    /// <code>
    ///   実効Multiply(+9Bh) = 親の実効Multiply × 自分のMultiply(+98h) ÷ 100
    ///   実効Add(+92h)      = 親の実効Add      + 自分のAdd(+8Ch)        ← 掛け算ではない
    ///   実効Alpha(+9Eh)    = 親の実効Alpha    × 自分のColor.A ÷ 255
    /// </code>
    /// 以前は生の値 (+98h/+8Ch) を読んで**自前で違う式で合成**していたため色が一致しなかった
    /// (特に加算項を `A子 × M親 + A親` としていたのが誤り)。
    ///
    /// アルファだけは実効値 (+9Eh) を使えない。我々がルートの `Color.A` を 0 にして隠すため、
    /// ゲームの実効アルファも 0 に伝播してしまうから。アルファは退避値から自前で積む。</summary>
    private static Tint NodeTint(AtkResNode* node)
    {
        var c = node->Color;
        return new Tint
        {
            MR = c.R / 255f * (node->MultiplyRed_2 / 100f),
            MG = c.G / 255f * (node->MultiplyGreen_2 / 100f),
            MB = c.B / 255f * (node->MultiplyBlue_2 / 100f),
            MA = 1f,                       // アルファは呼び出し側がチェーンで積む
            AR = node->AddRed_2 / 255f,
            AG = node->AddGreen_2 / 255f,
            AB = node->AddBlue_2 / 255f,
        };
    }

    /// <summary>ノードの実効色に、祖先ぶんまで積んだアルファを載せる。
    ///
    /// 色 (Multiply/Add) の階層継承は**ゲーム側が済ませている**ので我々は計算しない。
    /// アルファだけは、我々がルートを透明化している都合で自前のチェーンを使う。</summary>
    private static Tint WithAlpha(in Tint t, float alpha) => new()
    {
        MR = t.MR, MG = t.MG, MB = t.MB, MA = alpha,
        AR = t.AR, AG = t.AG, AB = t.AB,
    };

    /// <summary>近似フォールバック用に ImGui の乗算 tint (ABGR) へ潰す。加算項は寄せるしかない。</summary>
    private static uint PackTint(in Tint t)
    {
        float r = Math.Clamp(t.MR + t.AR, 0f, 1f);
        float g = Math.Clamp(t.MG + t.AG, 0f, 1f);
        float b = Math.Clamp(t.MB + t.AB, 0f, 1f);
        byte a = (byte)Math.Clamp(t.MA * 255f + 0.5f, 0f, 255f);
        return ((uint)a << 24)
             | ((uint)(b * 255f + 0.5f) << 16)
             | ((uint)(g * 255f + 0.5f) << 8)
             | (uint)(r * 255f + 0.5f);
    }

    // ============================== アルファ操作 ==============================

    /// <summary>ノードをアルファ 0 で透明化する。初回だけ元のアルファを退避する。
    ///
    /// テキストノードは特別扱いが要る。<see cref="AtkTextNode"/> の文字は
    /// <c>TextColor</c> / <c>EdgeColor</c> という**別フィールド**の色で描かれるため、
    /// ノードの <c>Color.A</c> を 0 にしても**消えず配信側へ漏れる**
    /// (実機で「画像は消えたが数字だけ残る」症状として確認)。両方を 0 にする。</summary>
    private void Dim(AtkResNode* node)
    {
        var key = (nint)node;
        _seen.Add(key);          // 生存確認済み。復元対象として残してよい
        if (!_dimmed.ContainsKey(key))
        {
            byte orig = node->Color.A;
            // 既に 0 = 元々透明 (所有プラグインのフェード演出中など) なら退避しない。
            if (orig != 0) _dimmed[key] = orig;
        }
        if (_dimmed.ContainsKey(key)) node->Color.A = 0;


        if (node->Type == NodeType.Text)
        {
            var t = (AtkTextNode*)node;
            if (!_dimmedText.ContainsKey(key))
            {
                byte ta = t->TextColor.A, ea = t->EdgeColor.A;
                if (ta != 0 || ea != 0) _dimmedText[key] = (ta, ea);
            }
            if (_dimmedText.ContainsKey(key))
            {
                t->TextColor.A = 0;
                t->EdgeColor.A = 0;
            }
        }
    }

    /// <summary>透明化をやめて元へ戻す。
    ///
    /// **「隠すのをやめる」ときは必ずこれを通すこと。**
    /// ただ隠さなくなるだけでは、以前隠したアルファ 0 が残ったまま退避情報だけが
    /// 毎フレームの掃除で消え、**二度と戻せなくなる**
    /// (対象プラグイン選別を入れた際に、選んだプラグインが手元からも消える不具合の原因)。
    /// 走査中に呼ぶので、対象ノードが生きていることは確認済み。</summary>
    private void Undim(AtkResNode* node)
    {
        var key = (nint)node;
        if (_dimmed.TryGetValue(key, out var a))
        {
            node->Color.A = a;
            _dimmed.Remove(key);
        }
        if (node->Type == NodeType.Text && _dimmedText.TryGetValue(key, out var tv))
        {
            var t = (AtkTextNode*)node;
            t->TextColor.A = tv.Text;
            t->EdgeColor.A = tv.Edge;
            _dimmedText.Remove(key);
        }
    }

    // ===================== アドオン更新後の再適用 =====================
    //
    // 我々が隠すのは Framework.Update (= ゲームの UI 更新より前) なので、
    // **毎フレーム自分でアルファを書き直すプラグイン**が相手だと必ず上書きされる。
    //
    //   SimpleTweaks/Tweaks/UiAdjustment/CastBarAdjustments.cs
    //     private void CastBarOnUpdateDetour(AddonCastBar* castBar, void* a2) {
    //         castBarOnUpdateHook.Original(castBar, a2);
    //         UpdateCastBar(castBar);      // ← ここで Color.A を毎回書く
    //     }
    //
    // 結果、配信側には出たままになり、更新が走らないフレームだけ消えて**ちらつく**。
    // そこで対象ノードを含むアドオンの **PostUpdate** (そのアドオンの更新が終わった直後) で
    // もう一度アルファを 0 にする。描画はこの後なので、これで最終的な値が我々のものになる。

    /// <summary>再適用の監視を張っているアドオン名。</summary>
    private readonly HashSet<string> _reapplyAddons = new();

    /// <summary>今フレームに再適用が必要と分かったアドオン名。</summary>
    private readonly HashSet<string> _needReapply = new();

    /// <summary>監視対象を今フレームの結果へ合わせる。増減したぶんだけ登録/解除する。</summary>
    /// <summary>監視を張ったか。アドオン名ごとに登録すると取りこぼしが出るため、
    /// **名前を指定しない 1 本の監視**にして、対象かどうかは中で見る。</summary>
    private bool _listenerArmed;

    private void SyncReapplyListeners()
    {
        // 対象のアドオン名を入れ替える (実際に隠したものだけ)。
        _reapplyAddons.Clear();
        foreach (var n in _needReapply) _reapplyAddons.Add(n);

        bool want = _reapplyAddons.Count > 0;
        if (want == _listenerArmed) return;

        if (want)
        {
            try
            {
                Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, OnAddonPostUpdate);
                Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, OnAddonPreDraw);
                _listenerArmed = true;
            }
            catch (Exception ex) { LastError = $"再適用の監視に失敗: {ex.Message}"; }
        }
        else ClearReapplyListeners();
    }

    /// <summary>監視を全部外す。停止時と破棄時に必ず通す。</summary>
    private void ClearReapplyListeners()
    {
        if (_listenerArmed)
        {
            try { Plugin.AddonLifecycle.UnregisterListener(OnAddonPostUpdate); } catch { }
            try { Plugin.AddonLifecycle.UnregisterListener(OnAddonPreDraw); } catch { }
            _listenerArmed = false;
        }
        _reapplyAddons.Clear();
        _needReapply.Clear();
    }

    private void OnAddonPostUpdate(AddonEvent type, AddonArgs args) => ForEachAddonRoot(args, n => ReapplyDim((AtkResNode*)n, 0));

    /// <summary>描画に入る直前。ここで初めてスケールを 0 にする。
    /// 座標計算 (UI 更新) は既に終わっているので、手元の再現位置は正しいまま消せる。</summary>
    private void OnAddonPreDraw(AddonEvent type, AddonArgs args) => ForEachAddonRoot(args, n => HideForDraw(n, 0));

    private void ForEachAddonRoot(AddonArgs args, Action<nint> fn)
    {
        if (!_active) return;
        try
        {
            // 隠すものがあるアドオンだけを見る (全アドオンを毎フレーム辿らないため)。
            if (!_reapplyAddons.Contains(args.AddonName)) return;
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null || addon->RootNode == null) return;
            fn((nint)addon->RootNode);
        }
        catch { }
    }

    /// <summary>描画直前の非表示。
    ///
    /// Atk は親→子の色伝播を **UI 更新のときに計算し、結果を `*_2` フィールドへ書き戻す**。
    /// 実際に描画へ使われるのはこの実効値なので、更新が終わった後に `Color.A` を
    /// 書いてもその場では効かない (次の更新まで反映されない)。これが
    /// 「毎フレーム書き直してくる相手に対して点滅する」正体だった。
    ///
    /// ここでは実効値である <c>Alpha_2</c> を直接 0 にする。描画の直前なので
    /// 誰にも上書きされず、座標計算も終わっているため手元の再現位置もずれない。</summary>
    private void HideForDraw(nint root, int depth)
    {
        var node = (AtkResNode*)root;
        if (node == null || depth > 24) return;

        var key = (nint)node;
        if (_dimmed.ContainsKey(key)) node->Color.A = 0;
        if (_dimmed.ContainsKey(key)) node->Alpha_2 = 0;
        if (node->Type == NodeType.Text && _dimmedText.ContainsKey(key))
        {
            var t = (AtkTextNode*)node;
            t->TextColor.A = 0;
            t->EdgeColor.A = 0;
        }

        for (var c = node->ChildNode; c != null; c = c->PrevSiblingNode)
            HideForDraw((nint)c, depth + 1);
        if (node->Type == NodeType.Component)
        {
            var comp = (AtkComponentNode*)node;
            if (comp->Component != null)
            {
                var ul = &comp->Component->UldManager;
                for (var i = 0; i < ul->NodeListCount; i++)
                    HideForDraw((nint)ul->NodeList[i], depth + 1);
            }
        }
    }


    /// <summary>このアドオンの中で、我々が隠しているノードのアルファを 0 に戻す。
    /// 自分のコールバックの中なのでアドオンは生きており、木を辿るのは安全。</summary>
    private void ReapplyDim(AtkResNode* node, int depth)
    {
        if (node == null || depth > 24) return;

        var key = (nint)node;
        if (_dimmed.ContainsKey(key)) node->Color.A = 0;
        if (node->Type == NodeType.Text && _dimmedText.ContainsKey(key))
        {
            var t = (AtkTextNode*)node;
            t->TextColor.A = 0;
            t->EdgeColor.A = 0;
        }

        for (var c = node->ChildNode; c != null; c = c->PrevSiblingNode)
            ReapplyDim(c, depth + 1);

        if (node->Type == NodeType.Component)
        {
            var comp = ((AtkComponentNode*)node)->Component;
            if (comp != null && comp->UldManager.RootNode != null)
                ReapplyDim(comp->UldManager.RootNode, depth + 1);
        }
    }

    /// <summary>枝全体の透明化を解除する。退避が何も無ければ即座に打ち切る。</summary>
    private void UndimTree(AtkResNode* node, int depth)
    {
        if (node == null || depth > 24) return;
        if (_dimmed.Count == 0 && _dimmedText.Count == 0) return;
        Undim(node);
        for (var c = node->ChildNode; c != null; c = c->PrevSiblingNode)
            UndimTree(c, depth + 1);
        if (node->Type == NodeType.Component)
        {
            var comp = (AtkComponentNode*)node;
            if (comp->Component != null)
            {
                var ul = &comp->Component->UldManager;
                for (var i = 0; i < ul->NodeListCount; i++)
                    UndimTree(ul->NodeList[i], depth + 1);
            }
        }
    }

    /// <summary>既に隠しているノードを「隠したまま」保持する。
    ///
    /// 見えていないノードは描き直す必要が無いので捕捉はしないが、手放してしまうと
    /// <see cref="PruneDimmed"/> が退避情報ごと捨てて元へ戻してしまう。
    /// すると次に見え始めたとき、こちらが隠し直すまでの 1 フレームだけ素通しになる。
    /// 生存確認済みとして印を付け直し、値も書き直しておく。</summary>
    private void HoldTree(AtkResNode* node, int depth)
    {
        if (node == null || depth > 24) return;
        if (_dimmed.Count == 0 && _dimmedText.Count == 0) return;

        var key = (nint)node;
        bool held = false;
        if (_dimmed.ContainsKey(key)) { node->Color.A = 0; held = true; }
        if (node->Type == NodeType.Text && _dimmedText.ContainsKey(key))
        {
            var t = (AtkTextNode*)node;
            t->TextColor.A = 0;
            t->EdgeColor.A = 0;
            held = true;
        }
        if (held) _seen.Add(key);

        for (var c = node->ChildNode; c != null; c = c->PrevSiblingNode)
            HoldTree(c, depth + 1);
        if (node->Type == NodeType.Component)
        {
            var comp = (AtkComponentNode*)node;
            if (comp->Component != null)
            {
                var ul = &comp->Component->UldManager;
                for (var i = 0; i < ul->NodeListCount; i++)
                    HoldTree(ul->NodeList[i], depth + 1);
            }
        }
    }

    /// <summary>枝全体を透明化する (ゲーム既存アドオンに相乗りしたノード用)。</summary>
    private void DimTree(AtkResNode* node, int depth)
    {
        if (node == null || depth > 24) return;
        Dim(node);
        for (var c = node->ChildNode; c != null; c = c->PrevSiblingNode)
            DimTree(c, depth + 1);
        if (node->Type == NodeType.Component)
        {
            var comp = (AtkComponentNode*)node;
            if (comp->Component != null)
            {
                var ul = &comp->Component->UldManager;
                for (var i = 0; i < ul->NodeListCount; i++)
                    DimTree(ul->NodeList[i], depth + 1);
            }
        }
    }

    /// <summary>再現描画に使う元のアルファ。透明化済みなら退避値を返す
    /// (0 のまま再現すると何も映らないため)。</summary>
    private byte OriginalAlpha(AtkResNode* node)
        => _dimmed.TryGetValue((nint)node, out var a) ? a : node->Color.A;

    /// <summary>再現描画に使う元のスケール。</summary>
    private static (float X, float Y) OriginalScale(AtkResNode* node) => (node->ScaleX, node->ScaleY);

    /// <summary>テキストノードの「元の」文字色/縁色アルファ。透明化済みなら退避値を返す。</summary>
    private (byte Text, byte Edge) OriginalTextAlpha(AtkTextNode* t)
        => _dimmedText.TryGetValue((nint)t, out var v) ? v : (t->TextColor.A, t->EdgeColor.A);

    /// <summary>今フレーム見なかったノードの退避情報を捨てる。
    /// ポインタが既に無効かもしれないので、**二度と触らない**ようにするのが目的。
    /// 元の値は失われるが、ノード自体が消えているので復元先も存在しない。</summary>
    private void PruneDimmed()
    {
        _pruneTmp.Clear();
        foreach (var k in _dimmed.Keys) if (!_seen.Contains(k)) _pruneTmp.Add(k);
        foreach (var k in _dimmedText.Keys) if (!_seen.Contains(k) && !_dimmed.ContainsKey(k)) _pruneTmp.Add(k);
        if (_pruneTmp.Count == 0) return;

        // **捨てる前に、生きているノードなら必ず元へ戻す。**
        // ここを省くと「隠したまま復元情報だけ消える」= 二度と戻せないノードが生まれる
        // (対象プラグイン選別の切り替えで実際に発生した)。
        // 生存アドオンを走査して見つかったものだけ書き戻すので、死んだポインタには触らない。
        try
        {
            var stage = AtkStage.Instance();
            var mgr = stage != null ? stage->RaptureAtkUnitManager : null;
            if (mgr != null)
            {
                var list = &mgr->AtkUnitManager.AllLoadedUnitsList;
                for (var i = 0; i < list->Count; i++)
                {
                    var addon = list->Entries[i].Value;
                    if (addon != null && addon->RootNode != null)
                        RestoreTree(addon->RootNode, 0);
                }
            }
        }
        catch (Exception ex) { LastError = $"掃除前の復元で例外: {ex.Message}"; }

        // RestoreTree が戻せたものは辞書から消えている。残りは到達不能なので破棄。
        foreach (var k in _pruneTmp) { _dimmed.Remove(k); _dimmedText.Remove(k); }
    }

    /// <summary>透明化したノードのアルファを元へ戻す。
    ///
    /// **退避辞書のポインタを直接辿ってはいけない** (解放済みの可能性がある)。
    /// 生きているアドオンを改めて走査し、そこで見つかったノードだけを復元する。
    /// 既に破棄されたノードは復元先ごと存在しないので放置してよい。</summary>
    private void RestoreAll()
    {
        if (_dimmed.Count == 0 && _dimmedText.Count == 0) return;
        try
        {
            var stage = AtkStage.Instance();
            var mgr = stage != null ? stage->RaptureAtkUnitManager : null;
            if (mgr != null)
            {
                var list = &mgr->AtkUnitManager.AllLoadedUnitsList;
                var count = list->Count;
                for (var i = 0; i < count; i++)
                {
                    var addon = list->Entries[i].Value;
                    if (addon == null || addon->RootNode == null) continue;
                    RestoreTree(addon->RootNode, 0);
                }
            }
        }
        catch (Exception ex) { LastError = $"復元中の例外: {ex.Message}"; }
        _dimmed.Clear();
        _dimmedText.Clear();
        _seen.Clear();
    }

    /// <summary>生存しているノードツリーを辿り、退避してあった値を書き戻す。</summary>
    private void RestoreTree(AtkResNode* node, int depth)
    {
        if (node == null || depth > 24) return;
        // 戻せたものは辞書から除く (呼び出し側が「戻せなかったもの」を判別できるように)。
        Undim(node);

        for (var c = node->ChildNode; c != null; c = c->PrevSiblingNode)
            RestoreTree(c, depth + 1);
        if (node->Type == NodeType.Component)
        {
            var comp = (AtkComponentNode*)node;
            if (comp->Component != null)
            {
                var ul = &comp->Component->UldManager;
                for (var i = 0; i < ul->NodeListCount; i++)
                    RestoreTree(ul->NodeList[i], depth + 1);
            }
        }
    }

    /// <summary>[緊急] KamiToolKit ノードのアルファを強制的に 255 へ戻す。
    ///
    /// 退避情報を失って「透明のまま取り残された」ノードを救うための最後の手段。
    /// 元の値が分からないので一律 255 にする。所有プラグインが毎フレーム色を書くものは
    /// 次の更新で本来の値に戻る。生存アドオンの走査なので死んだポインタには触らない。</summary>
    public int ForceRestoreAll()
    {
        int n = 0;
        try
        {
            var stage = AtkStage.Instance();
            var mgr = stage != null ? stage->RaptureAtkUnitManager : null;
            if (mgr == null) return 0;
            var list = &mgr->AtkUnitManager.AllLoadedUnitsList;
            for (var i = 0; i < list->Count; i++)
            {
                var addon = list->Entries[i].Value;
                if (addon == null || addon->RootNode == null) continue;
                bool overlay = Array.IndexOf(OverlayAddonNames, addon->NameString) >= 0;
                ForceRestoreTree(addon->RootNode, 0, overlay, ref n);
            }
        }
        catch (Exception ex) { LastError = $"強制復元で例外: {ex.Message}"; }
        _dimmed.Clear();
        _dimmedText.Clear();
        _seen.Clear();
        Plugin.Log.Info($"[KamiMirror] 強制復元: {n} ノード");
        return n;
    }

    private void ForceRestoreTree(AtkResNode* node, int depth, bool overlay, ref int n)
    {
        if (node == null || depth > 24) return;
        // KamiToolKit のノード、または専用オーバーレイの中だけを対象にする。
        if (overlay || node->NodeId >= NodeIdBase)
        {
            if (node->Color.A == 0) { node->Color.A = 255; n++; }
            // 実効アルファを 0 にして隠しているものも戻す。
            if (node->Alpha_2 == 0) { node->Alpha_2 = 255; n++; }
            if (node->Type == NodeType.Text)
            {
                var t = (AtkTextNode*)node;
                if (t->TextColor.A == 0) { t->TextColor.A = 255; n++; }
                if (t->EdgeColor.A == 0) t->EdgeColor.A = 255;
            }
        }
        for (var c = node->ChildNode; c != null; c = c->PrevSiblingNode)
            ForceRestoreTree(c, depth + 1, overlay, ref n);
        if (node->Type == NodeType.Component)
        {
            var comp = (AtkComponentNode*)node;
            if (comp->Component != null)
            {
                var ul = &comp->Component->UldManager;
                for (var i = 0; i < ul->NodeListCount; i++)
                    ForceRestoreTree(ul->NodeList[i], depth + 1, overlay, ref n);
            }
        }
    }

    /// <summary>[調査] 診断内容を全件ファイルへ書き出す。画面の一覧は件数上限があるため、
    /// 解析にはこちらを使う。設定値も併記して状況ごと再現できるようにする。</summary>
    private string WriteDiagFile(bool suspended = false)
    {
        var dir = System.IO.Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "diag");
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "kami_diag.txt");

        var c = _plugin.cfg;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Masked Dalamud KamiMirror 診断  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        if (suspended)
        {
            sb.AppendLine("## ⚠ キャプチャ除外が停止中です");
            sb.AppendLine("  ミラーは待機しているため、ノードの検出・非表示は行っていません。");
            sb.AppendLine("  基本タブの「開始」を押してから、もう一度出力してください。");
            sb.AppendLine("  (停止中のまま調べたい場合は、試験機能タブの");
            sb.AppendLine("   「キャプチャ除外が停止中でもミラーを動かす」を ON にしてください)");
            sb.AppendLine();
        }
        sb.AppendLine("## 設定");
        sb.AppendLine($"  ミラー有効        = {_active}");
        sb.AppendLine($"  独立オーバーレイのみ = {c.kamiOverlayAddonsOnly}");
        sb.AppendLine($"  厳密な色          = {c.kamiExactColor}   (capture mode={c.dcompCaptureMode})");
        sb.AppendLine($"  ゲージ中身を加算    = {c.kamiRenderTypeAdditive}");
        sb.AppendLine($"  加算色を一律加算    = {c.kamiAdditiveOnAdd}");
        sb.AppendLine($"  文字サイズ係数     = {c.kamiFontScale}");
        sb.AppendLine($"  明るさ補正        = {c.kamiBrightness}");
        sb.AppendLine($"  素のテクセル描画   = {c.kamiRawTexel}");
        sb.AppendLine($"  可視判定を無視     = {c.kamiIgnoreVisible}");
        sb.AppendLine($"  対象プラグイン     = {(c.kamiTargetPlugins.Count == 0 ? "(すべて)" : string.Join(", ", c.kamiTargetPlugins))}");
        sb.AppendLine($"  描画直前の再適用   = {(_listenerArmed ? "監視中" : "停止")} 対象アドオン: {(_reapplyAddons.Count == 0 ? "(なし)" : string.Join(", ", _reapplyAddons))}");
        sb.AppendLine();
        sb.AppendLine("## 検出した所有プラグイン (枝数)");
        foreach (var kv in _owners.OrderByDescending(k => k.Value))
            sb.AppendLine($"  {kv.Key} = {kv.Value}");
        sb.AppendLine();
        sb.AppendLine($"## 対象アドオン: {_addonSummary}");
        sb.AppendLine();
        sb.AppendLine($"## 描画ノード ({_diag.Count} 件)");
        foreach (var l in _diag) sb.AppendLine("  " + l);
        sb.AppendLine();
        sb.AppendLine($"## 可視判定で除外 ({_skipped_n} 件 / 記録 {_skipped.Count} 件)");
        foreach (var l in _skipped) sb.AppendLine("  " + l);

        sb.AppendLine();
        sb.AppendLine("## プラグイン製ノードの全捜索 (可視・対象の別を問わず)");
        try { ScanPluginNodes(sb); } catch (Exception ex) { sb.AppendLine($"  捜索で例外: {ex.Message}"); }

        System.IO.File.WriteAllText(path, sb.ToString());
        Plugin.Log.Info($"[KamiMirror] 診断を出力: {path}");
        return path;
    }

    /// <summary>[調査] 読み込み済みアドオンを全部辿り、プラグインが足したノードを列挙する。
    ///
    /// 通常の走査は「アドオンが可視」「ノードが可視」で絞るため、
    /// 対象が見えていないタイミングでは何も残らず、**検出できていないのか
    /// たまたま出ていないだけなのか**が区別できない。ここでは絞り込みを一切かけない。</summary>
    private void ScanPluginNodes(System.Text.StringBuilder sb)
    {
        var stage = AtkStage.Instance();
        var mgr = stage != null ? stage->RaptureAtkUnitManager : null;
        if (mgr == null) { sb.AppendLine("  (UnitManager が取れません)"); return; }

        int found = 0;
        var list = &mgr->AtkUnitManager.AllLoadedUnitsList;
        for (var i = 0; i < list->Count; i++)
        {
            var addon = list->Entries[i].Value;
            if (addon == null || addon->RootNode == null) continue;
            ScanTree(addon->RootNode, addon->NameString, addon->IsVisible, 0, sb, ref found);
        }
        if (found == 0) sb.AppendLine("  (見つかりませんでした)");
        else sb.AppendLine($"  合計 {found} 件");
    }

    private void ScanTree(AtkResNode* node, string addon, bool addonVisible, int depth,
                          System.Text.StringBuilder sb, ref int found)
    {
        if (node == null || depth > 24 || found > 200) return;

        if (node->NodeId >= NodeIdBase)
        {
            bool vis;
            try { vis = node->IsVisible(); } catch { vis = false; }
            var owner = BranchOwner(node)
                     ?? (AddonOwners.TryGetValue(addon, out var byAddon) ? byAddon : null)
                     ?? OwnerByNodeId(node->NodeId)
                     ?? "(不明)";
            sb.AppendLine($"  [{addon}] #{node->NodeId} (0x{node->NodeId:X}) {node->Type}"
                        + $" 所有={owner} ノード可視={vis} アドオン可視={addonVisible}"
                        + $" a={node->Color.A} 位置={(int)node->ScreenX},{(int)node->ScreenY}");
            found++;
        }

        for (var c = node->ChildNode; c != null; c = c->PrevSiblingNode)
            ScanTree(c, addon, addonVisible, depth + 1, sb, ref found);
        if (node->Type == NodeType.Component)
        {
            var comp = (AtkComponentNode*)node;
            if (comp->Component != null)
            {
                var ul = &comp->Component->UldManager;
                for (var i = 0; i < ul->NodeListCount; i++)
                    ScanTree(ul->NodeList[i], addon, addonVisible, depth + 1, sb, ref found);
            }
        }
    }

    // ============================== 収集 + 透過 ==============================

    /// <summary>Framework (ゲーム) スレッドから毎フレーム呼ぶ。
    /// 読み込み済みアドオンを走査して KamiToolKit ノードを集め、透過しつつ描画用スナップショットを作る。
    /// 所有プラグインが毎フレーム値を書き戻す場合があるため、毎フレーム再アサートする。</summary>
    public void FrameworkTick()
    {
        if (!_active) return;

        // キャプチャ除外がどれも動いていないなら、隠す意味が無い (手元も配信も同じ絵)。
        // それどころか**ネイティブの本物を消して近似ミラーを見せる**ことになり、
        // 見た目が劣化するだけ。この間はノードに触らず本来の描画に任せる。
        if (!_plugin.CaptureScrubActive && !_plugin.cfg.kamiMirrorAlwaysRun)
        {
            if (!_suspended)
            {
                _suspended = true;
                lock (_gate) ClearItems();
                RestoreAll();
                LastNodeCount = LastDrawCount = LastQuadCount = 0;
                _addonSummary = "(キャプチャ除外が停止中のため待機)";
            }
            // 停止中でも診断の要求には応える。黙って何も出さないと、
            // 「ボタンを押したのにファイルが増えない」理由が分からなくなる。
            if (DumpDiagRequested)
            {
                DumpDiagRequested = false;
                try { DumpDiagResult = WriteDiagFile(suspended: true); }
                catch (Exception ex) { DumpDiagResult = $"出力失敗: {ex.Message}"; }
            }
            return;
        }
        _suspended = false;

        Monitor.Enter(_gate);
        ClearItems();
        _diag.Clear();
        _skipped.Clear();
        _skipped_n = 0;
        _owners.Clear();
        _dumpAll = DumpDiagRequested;
        EnsureKtkTable();
        _seen.Clear();
        _byAddon.Clear();
        _needReapply.Clear();
        int nodeCount = 0;
        // 走査が最後まで通ったときだけ退避情報を掃除する。途中で抜けた場合に掃除すると
        // 「元のアルファを忘れたまま隠れっぱなし」になり得るため。
        bool walkOk = false;
        try
        {
            var stage = AtkStage.Instance();
            if (stage == null) return;
            var mgr = stage->RaptureAtkUnitManager;
            if (mgr == null) return;

            var list = &mgr->AtkUnitManager.AllLoadedUnitsList;
            var count = list->Count;
            for (var i = 0; i < count; i++)
            {
                var addon = list->Entries[i].Value;
                if (addon == null || addon->RootNode == null) continue;
                if (!addon->IsVisible)
                {
                    // アドオンごと消えている間も、隠していたものは保持する
                    // (手放すと再表示の 1 フレーム目が素通しになる)。
                    HoldTree(addon->RootNode, 0);
                    continue;
                }

                var name = addon->NameString;
                bool isOverlayAddon = Array.IndexOf(OverlayAddonNames, name) >= 0;

                // 独立オーバーレイ (KTK_Overlay_*) のみを対象にする設定。
                if (_plugin.cfg.kamiOverlayAddonsOnly && !isOverlayAddon) continue;

                var root = addon->RootNode;
                int before = nodeCount;
                if (isOverlayAddon)
                {
                    // KamiToolKit 専用レイヤーは丸ごと我々の担当。
                    // 描画内容は子要素 (ゲージ等) から読み取り、**隠すのはルート 1 個だけ**にする。
                    // アルファは親から子へ乗算で伝播するので、これで配下すべてが消える。
                    // 書き込み箇所が数百 → 1 になり、触るポインタが激減する (クラッシュ対策)。
                    // プラグインを選別するときはルートごと隠せない (他プラグインの枝も消えるため)。
                    // その場合だけ枝単位で隠す。選別なしなら従来どおりルート 1 個で済ませる。
                    bool filtered = _plugin.cfg.kamiTargetPlugins.Count > 0;
                    // **`AtkUnitBase.Scale` を掛けてはいけない。**
                    // アドオンのスケールはルートノードの ScaleX/Y に入っており、同じ値である
                    // (実測: _PartyList は scale=0.900 / root=0.900)。両方掛けると 0.81 になり、
                    // 描画が 0.9 倍小さくなる。ルートの子から始めるのでルートぶんだけ種にする。
                    var rootCtx = Ctx.Root(OriginalAlpha(root) / 255f,
                                           root->ScaleX != 0 ? root->ScaleX : 1f,
                                           root->ScaleY != 0 ? root->ScaleY : 1f);
                    for (var c = root->ChildNode; c != null; c = c->PrevSiblingNode)
                        Walk(c, name, ref nodeCount, depth: 1, rootCtx, force: true, hide: filtered);
                    // 選別中はルートを隠さない。以前ルート単位で隠していた場合は必ず戻す
                    // (戻さないとアルファ 0 が残り、ミラーも透明になって両方から消える)。
                    if (filtered) Undim(root); else Dim(root);
                }
                else
                {
                    // ゲーム既存アドオンに相乗りしたノードは、その枝だけを個別に隠すしかない。
                    // ルート自身から辿るので、ルートのスケールは Walk の中で掛かる。
                    // ここで addon->Scale を足すと二重になる。
                    Walk(root, name, ref nodeCount, depth: 0, Ctx.Root(1f, 1f, 1f));

                    // 相乗り先はアドオンの更新でアルファを書き直されることがある
                    // (SimpleTweaks など)。更新直後に再適用するため監視対象にする。
                    if (nodeCount > before) _needReapply.Add(name);
                }
                if (nodeCount > before)
                {
                    var label = (isOverlayAddon ? name + "*" : name)
                              + $"(scale={addon->Scale:F3},root={root->ScaleX:F3})";
                    _byAddon[label] = _byAddon.TryGetValue(label, out var prev)
                                    ? prev + (nodeCount - before) : nodeCount - before;
                }
            }
            walkOk = true;
        }
        catch (Exception ex) { LastError = ex.Message; }
        finally
        {
            // 見なくなったノードの退避情報を捨てる (以後そのポインタには触らない)。
            if (walkOk) { try { PruneDimmed(); } catch { } }
            Monitor.Exit(_gate);
        }
        // 走査が通ったときだけ監視対象を更新する (途中で抜けた結果で外すと再適用が止まる)。
        if (walkOk) { try { SyncReapplyListeners(); } catch { } }
        LastNodeCount = nodeCount;
        LastSkippedCount = _skipped_n;
        if (_dumpAll)
        {
            _dumpAll = false;
            DumpDiagRequested = false;
            try { DumpDiagResult = WriteDiagFile(); }
            catch (Exception ex) { DumpDiagResult = $"出力失敗: {ex.Message}"; }
        }
        _addonSummary = _byAddon.Count == 0
            ? "(対象アドオンなし)"
            : string.Join(" / ", _byAddon.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    /// <param name="parent">祖先から受け継いだ色合成。</param>
    /// <param name="force">true = このノード以下を無条件に対象とする
    /// (KamiToolKit 専用オーバーレイアドオンの中身)。</param>
    /// <param name="hide">false = 見た目の読み取りだけ行い、ネイティブ側には書き込まない。
    /// 専用オーバーレイはルート 1 個を隠せば配下ごと消えるので、子には書き込まない。</param>
    private void Walk(AtkResNode* node, string addon, ref int count, int depth, in Ctx parent,
                      bool force = false, bool hide = true)
    {
        if (node == null || depth > 24) return;

        if (force || node->NodeId >= NodeIdBase)
        {
            // 所有プラグインが「今表示したい」ノードだけを扱う。可視フラグは変更しないので
            // この判定はマスク中も正しく機能し続ける。
            bool visible;
            try { visible = node->IsVisible(); } catch { return; }
            if (!visible)
            {
                // 見えていない間も、既に隠しているものは**隠したまま保持する**。
                // ここで手放すと退避情報が掃除で捨てられ、再び見え始めてから次に隠すまでの
                // 1 フレームだけ素通しになる (詠唱終了の瞬間に配信へ一瞬出ていた原因)。
                HoldTree(node, 0);
                return;
            }

            // 所有プラグインを特定し、選別が有効ならここで振り分ける。
            // KTK のノード表に載らないものは、アドオン名 → NodeId の順に引く。
            var owner = BranchOwner(node)
                     ?? (AddonOwners.TryGetValue(addon, out var byAddon) ? byAddon : null)
                     ?? OwnerByNodeId(node->NodeId)
                     ?? "(不明)";
            _owners[owner] = _owners.TryGetValue(owner, out var oc) ? oc + 1 : 1;
            var targets = _plugin.cfg.kamiTargetPlugins;
            if (targets.Count > 0 && !targets.Contains(owner))
            {
                // 対象外。以前隠していたなら元へ戻してから抜ける。
                try { UndimTree(node, 0); } catch { }
                return;
            }

            count++;
            // この枝の見た目を丸ごとスナップショットへ (Component は中身を再帰的に拾う)。
            _clip = null;
            try { CaptureTree(node, addon, 0, parent); } catch (Exception ex) { LastError = ex.Message; }
            _clip = null;
            if (hide) { try { DimTree(node, 0); } catch { } }
            return; // 子は辿らない (この枝ごと肩代わりする)
        }

        // ここは対象外のノード。アルファとスケールを子へ積む (色はゲームが継承済み)。
        var os = OriginalScale(node);
        var ctx = parent.With(parent.Alpha * (OriginalAlpha(node) / 255f), os.X, os.Y);

        for (var c = node->ChildNode; c != null; c = c->PrevSiblingNode)
            Walk(c, addon, ref count, depth + 1, ctx, force, hide);

        if (node->Type == NodeType.Component)
        {
            var comp = (AtkComponentNode*)node;
            if (comp->Component != null)
            {
                var ul = &comp->Component->UldManager;
                for (var i = 0; i < ul->NodeListCount; i++)
                    Walk(ul->NodeList[i], addon, ref count, depth + 1, ctx, force, hide);
            }
        }
    }

    /// <summary>対象枝の見た目を再帰的にスナップショットへ写す。
    /// Res / Component は自身に絵を持たないので子だけを辿り、描画可能な葉 (Text/Image/NineGrid) を拾う。</summary>
    /// <param name="parentAlpha">祖先ぶんまで積んだアルファ (0..1)。
    /// 色の階層継承はゲーム側が済ませているので受け渡し不要。アルファだけは
    /// 我々がルートを透明化している都合で自前に積む。</param>
    private void CaptureTree(AtkResNode* node, string addon, int depth, in Ctx parent)
    {
        if (node == null || depth > 24) return;

        // 可視フラグは我々が触らないため全階層でそのまま尊重できる
        // (所有プラグインが隠している子は再現しない)。
        // [診断] 何を描き落としているかを見るため、弾いたノードを記録する。
        bool vis = true;
        try { vis = node->IsVisible(); } catch { }
        if (!vis)
        {
            _skipped_n++;
            if (_skipped.Count < (_dumpAll ? 20000 : 12))
                _skipped.Add($"{node->Type} #{node->NodeId} {(int)node->ScreenX},{(int)node->ScreenY} "
                           + $"{(int)(node->Width * node->ScaleX)}x{(int)(node->Height * node->ScaleY)}"
                           + $" a={node->Color.A}");
            if (!_plugin.cfg.kamiIgnoreVisible) return;
        }

        var os = OriginalScale(node);
        var ctx = parent.With(parent.Alpha * (OriginalAlpha(node) / 255f), os.X, os.Y);

        switch (node->Type)
        {
            case NodeType.Text:
            case NodeType.Image:
            case NodeType.NineGrid:
                Capture(node, addon, WithAlpha(NodeTint(node), ctx.Alpha), ctx);
                break;
            case NodeType.Counter:
                CaptureCounter(node, addon, WithAlpha(NodeTint(node), ctx.Alpha), ctx);
                break;
            case NodeType.ClippingMask:
                // クリッピングマスクは「以降の兄弟をこの矩形に収める」指示。
                // 正確な再現には素材ごとのマスクが要るが、矩形での切り抜きで実用上そろう。
                _clip = (node->ScreenX, node->ScreenY,
                         node->ScreenX + node->Width * ctx.ScaleX,
                         node->ScreenY + node->Height * ctx.ScaleY);
                break;
        }

        for (var c = node->ChildNode; c != null; c = c->PrevSiblingNode)
            CaptureTree(c, addon, depth + 1, ctx);

        if (node->Type == NodeType.Component)
        {
            var comp = (AtkComponentNode*)node;
            if (comp->Component != null)
            {
                var ul = &comp->Component->UldManager;
                for (var i = 0; i < ul->NodeListCount; i++)
                    CaptureTree(ul->NodeList[i], addon, depth + 1, ctx);
            }
        }
    }

    /// <summary>ノードの見た目を描画用スナップショットへ写す。</summary>
    /// <param name="tint">祖先ぶんまで合成済みの色。ノード単体の色ではない。</param>
    private void Capture(AtkResNode* node, string addon, in Tint tint, in Ctx ctx)
    {
        // 祖先ぶんまで積んだスケールを使う (自分の ScaleX だけでは HUD スケールが抜ける)。
        float w = node->Width * ctx.ScaleX;
        float h = node->Height * ctx.ScaleY;
        if (w <= 0 || h <= 0) return;

        uint col = PackTint(tint);
        var item = new Item
        {
            X = node->ScreenX, Y = node->ScreenY, W = w, H = h,
            ScaleX = ctx.ScaleX, ScaleY = ctx.ScaleY,
            Color = col == 0 ? 0xFFFFFFFF : col,
            U0 = 0, V0 = 0, U1 = 1, V1 = 1,
            MR = tint.MR, MG = tint.MG, MB = tint.MB, MA = tint.MA,
            AR = tint.AR, AG = tint.AG, AB = tint.AB,
            ClipX = _clip?.X0 ?? 0f, ClipY = _clip?.Y0 ?? 0f,
            ClipW = _clip is { } cl ? cl.X1 - cl.X0 : 0f,
            ClipH = _clip is { } cl2 ? cl2.Y1 - cl2.Y0 : 0f,
            // Atk は OriginX/OriginY を軸に回す。スケールも掛かっている点に注意。
            Rotation = node->Rotation,
            OriginX = node->ScreenX + node->OriginX * ctx.ScaleX,
            OriginY = node->ScreenY + node->OriginY * ctx.ScaleY,
        };

        switch (node->Type)
        {
            case NodeType.Text:
            {
                var t = (AtkTextNode*)node;
                item.Text = t->NodeText.ToString();
                // ノードのスケールを効かせる (画像側で Width×ScaleX としているのと対応)。
                float baseSize = t->FontSize > 0 ? t->FontSize : 12f;
                item.FontSize = baseSize * (ctx.ScaleY > 0 ? ctx.ScaleY : 1f);
                item.Font = t->FontType;
                item.Align = t->AlignmentType;
                item.TextFlags = t->TextFlags;
                // 文字色/縁色は隠すときに 0 にしているので、退避しておいた元の値で再現する。
                // 祖先のアルファも掛ける (親をフェードさせている場合に追従するため)。
                var (ta, ea) = OriginalTextAlpha(t);
                byte ta2 = (byte)Math.Clamp(ta * tint.MA, 0f, 255f);
                byte ea2 = (byte)Math.Clamp(ea * tint.MA, 0f, 255f);
                var tc = t->TextColor;
                item.Color = ((uint)ta2 << 24) | ((uint)tc.B << 16) | ((uint)tc.G << 8) | tc.R;
                var ec = t->EdgeColor;
                if (ea2 > 0)
                {
                    item.HasEdge = true;
                    item.EdgeColor = ((uint)ea2 << 24) | ((uint)ec.B << 16) | ((uint)ec.G << 8) | ec.R;
                }
                break;
            }
            case NodeType.Image:
            {
                var img = (AtkImageNode*)node;
                item.WrapMode = img->WrapMode;
                item.TextureSrv = GetSrv(img->PartsList, img->PartId, ref item);
                // Tile 指定でノードがパーツより大きい場合は UV を伸ばして繰り返す。
                //
                // **比べるのはノードの素の大きさとパーツの大きさ。**
                // w / h は画面上の大きさ (祖先ぶんのスケールを掛けた後) なので、
                // ゲームの「高解像度時の UI サイズ設定」を上げるとその倍率がそのまま
                // 繰り返し回数に化け、アイコンが引き伸ばされて潰れる
                // (100% では倍率が 1 前後のため気付かなかった)。
                if (item.WrapMode == 1 && item.TextureSrv != 0 && item.PartW > 0 && item.PartH > 0)
                {
                    float rx = node->Width / item.PartW, ry = node->Height / item.PartH;
                    if (rx > 1.001f) item.U1 = item.U0 + (item.U1 - item.U0) * rx;
                    if (ry > 1.001f) item.V1 = item.V0 + (item.V1 - item.V0) * ry;
                    item.Tile = rx > 1.001f || ry > 1.001f;
                }
                break;
            }
            case NodeType.NineGrid:
            {
                var ng = (AtkNineGridNode*)node;
                item.TextureSrv = GetSrv(ng->PartsList, ng->PartId, ref item);
                // 9 分割: 四隅は原寸、辺は片方向、中央は両方向に伸ばす。
                // ゲージ中身 (BarMain/BarSecondary) はこの形式で描かれる。
                item.BlendMode = ng->BlendMode;
                item.PartsRenderType = ng->PartsTypeRenderType;
                if (item.TextureSrv != 0)
                {
                    item.Slice = true;
                    item.SliceL = ng->LeftOffset;
                    item.SliceR = ng->RightOffset;
                    item.SliceT = ng->TopOffset;
                    item.SliceB = ng->BottomOffset;
                }
                break;
            }
        }

        AddItem(item);
        if (_diag.Count < (_dumpAll ? 20000 : 48))
        {
            // 実際に読めている合成値を出す。元の見た目と合わないときの切り分けはここが起点。
            string extra = node->Type == NodeType.NineGrid || node->Type == NodeType.Image
                ? $" blend={item.BlendMode} prt={item.PartsRenderType}"
                + $" part=({item.PartU:F0},{item.PartV:F0},{item.PartW:F0},{item.PartH:F0})"
                + $" tex=({item.TexW:F0}x{item.TexH:F0} 公称{item.NomTex})"
                + $" uv=({item.U0:F3},{item.V0:F3})-({item.U1:F3},{item.V1:F3})"
                + $" off=(L{item.SliceL:F0} R{item.SliceR:F0} T{item.SliceT:F0} B{item.SliceB:F0})"
                : node->Type == NodeType.Text ? $" \"{item.Text}\" font={item.Font} align={item.Align}"
                                             + $" flags={item.TextFlags} fs={item.FontSize:F1}"
                : "";
            _diag.Add($"[{addon}] {node->Type} {(int)item.X},{(int)item.Y} {(int)w}x{(int)h}"
                    + $" 素={node->Width}x{node->Height} 自ScaleXY=({node->ScaleX:F3},{node->ScaleY:F3})"
                    + $" 累積Scale=({ctx.ScaleX:F3},{ctx.ScaleY:F3})"
                    + $" M=({item.MR:F2},{item.MG:F2},{item.MB:F2})"
                    + $" A=({item.AR:F2},{item.AG:F2},{item.AB:F2}) a={item.MA:F2}"
                    + $" rot={node->Rotation:F2}" + extra);
        }
    }

    /// <summary>SRV → 実際の D3D テクスチャ寸法。SRV ポインタで結果をキャッシュする。
    ///
    /// **UV の分母はここで得た実寸でなければならない。**
    /// `Kernel.Texture.ActualWidth/Height` は「画像の中身のサイズ」で、
    /// FFXIV の UI テクスチャは 2 の冪サイズの面に中身を置いていることがある
    /// (実測: 中身 160x152)。中身サイズで割ると**テクスチャの別の場所を読む**ことになり、
    /// 色がネイティブと合わない (2026-09-12 にピクセル実測で確定)。</summary>
    private static readonly Dictionary<nint, (float W, float H)> _texSize = new();
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    internal static bool TryGetRealTexSize(nint srvPtr, out float w, out float h)
    {
        w = h = 0f;
        if (srvPtr == 0) return false;
        lock (_texSize)
        {
            if (_texSize.TryGetValue(srvPtr, out var v)) { w = v.W; h = v.H; return w > 0 && h > 0; }
        }
        try
        {
            var srv = (ID3D11ShaderResourceView*)srvPtr;
            ID3D11Resource* res = null;
            srv->GetResource(&res);
            if (res == null) return false;
            try
            {
                ID3D11Texture2D* tex = null;
                var iid = IID_ID3D11Texture2D;
                if (res->QueryInterface(&iid, (void**)&tex) != 0 || tex == null) return false;
                try
                {
                    D3D11_TEXTURE2D_DESC td;
                    tex->GetDesc(&td);
                    w = td.Width; h = td.Height;
                }
                finally { tex->Release(); }
            }
            finally { res->Release(); }
        }
        catch { return false; }

        if (w <= 0 || h <= 0) return false;
        lock (_texSize)
        {
            if (_texSize.Count < 512) _texSize[srvPtr] = (w, h);
        }
        return true;
    }

    /// <summary>[AtkCounterNode] 数字を 1 文字ずつパーツから組み立てて描く。
    ///
    /// このノードは文字を**フォントではなくパーツ画像**で描く (ゲージの残り時間・所持金など)。
    /// 文字とパーツ番号の対応は Atk の規約:
    ///   '0'〜'9' → パーツ 0〜9 / ',' → 10 / '.' → 11 / '+' → 12 / '-' → 13
    /// 幅は数字が <c>NumberWidth</c>、カンマが <c>CommaWidth</c>、空白が <c>SpaceWidth</c>。
    /// 実装していなかったため、これを使う表示は**丸ごと欠けていた**。</summary>
    private void CaptureCounter(AtkResNode* node, string addon, in Tint tint, in Ctx ctx)
    {
        var cn = (AtkCounterNode*)node;
        string text;
        try { text = cn->NodeText.ToString(); } catch { return; }
        if (string.IsNullOrEmpty(text)) return;

        float numW = cn->NumberWidth * ctx.ScaleX;
        float comW = cn->CommaWidth * ctx.ScaleX;
        float spcW = cn->SpaceWidth * ctx.ScaleX;
        float h = node->Height * ctx.ScaleY;
        if (h <= 0) return;

        // まず総幅を求める (右寄せ・中央寄せに必要)。
        float total = 0f;
        foreach (var ch in text)
            total += ch switch { >= '0' and <= '9' => numW, ',' or '.' => comW, ' ' => spcW, _ => numW };

        float w = node->Width * ctx.ScaleX;
        float x = node->ScreenX;
        // TextAlign: 0=左 / 1=中央 / 2=右 (Atk の規約)
        if (cn->TextAlign == 1) x += (w - total) * 0.5f;
        else if (cn->TextAlign >= 2) x += w - total;

        foreach (var ch in text)
        {
            int part = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                ',' => 10, '.' => 11, '+' => 12, '-' => 13,
                _ => -1,
            };
            float cw = ch switch { >= '0' and <= '9' => numW, ',' or '.' => comW, ' ' => spcW, _ => numW };
            if (part >= 0 && cw > 0)
            {
                var item = new Item
                {
                    X = x, Y = node->ScreenY, W = cw, H = h,
                    Color = PackTint(tint),
                    MR = tint.MR, MG = tint.MG, MB = tint.MB, MA = tint.MA,
                    AR = tint.AR, AG = tint.AG, AB = tint.AB,
                    Counter = true,
                    ClipX = _clip?.X0 ?? 0f, ClipY = _clip?.Y0 ?? 0f,
                    ClipW = _clip is { } c1 ? c1.X1 - c1.X0 : 0f,
                    ClipH = _clip is { } c2 ? c2.Y1 - c2.Y0 : 0f,
                    U0 = 0, V0 = 0, U1 = 1, V1 = 1,
                };
                item.TextureSrv = GetSrv(cn->PartsList, (uint)part, ref item);
                if (item.TextureSrv != 0) AddItem(item);
            }
            x += cw;
        }

        if (_diag.Count < (_dumpAll ? 20000 : 48))
            _diag.Add($"[{addon}] Counter {(int)node->ScreenX},{(int)node->ScreenY} \"{text}\""
                    + $" 幅=(数{cn->NumberWidth} 点{cn->CommaWidth} 空{cn->SpaceWidth}) align={cn->TextAlign}");
    }

    /// <summary>パーツリストからテクスチャの SRV と UV を取り出す。取得できなければ 0。</summary>
    /// <summary>テクスチャの素性。ファイル名から判る性質をまとめて持つ。
    ///
    /// 毎フレーム文字列を作ると重いので、ハンドルごとに一度だけ調べて覚えておく。</summary>
    private readonly record struct TexKind(float HiRes, bool IsIcon);

    private static readonly Dictionary<nint, TexKind> _texKind = new();

    private static TexKind KindOf(AtkTextureResource* res)
    {
        var h = res->TexFileResourceHandle;
        if (h == null) return new TexKind(1f, false);

        var key = (nint)h;
        if (_texKind.TryGetValue(key, out var cached)) return cached;

        var kind = new TexKind(1f, false);
        try
        {
            var name = h->ResourceHandle.FileName.ToString();
            // 高解像度 UI では末尾 _hr1 のテクスチャが使われる。
            float hr = name.Contains("_hr1", StringComparison.OrdinalIgnoreCase) ? 2f : 1f;
            // ui/icon/ 以下は 1 枚 1 絵のアイコン。ui/uld/ 以下は複数の絵を詰めた板。
            bool icon = name.Contains("ui/icon/", StringComparison.OrdinalIgnoreCase);
            kind = new TexKind(hr, icon);
        }
        catch { }

        if (_texKind.Count < 4096) _texKind[key] = kind;
        return kind;
    }

    private static nint GetSrv(AtkUldPartsList* parts, uint partId, ref Item item)
    {
        try
        {
            if (parts == null || partId >= parts->PartCount) return 0;
            var part = &parts->Parts[partId];
            var asset = part->UldAsset;
            if (asset == null) return 0;

            // AtkTexture は「ゲームのテクスチャ資産 (Resource)」と
            // 「自前で作った Kernel テクスチャ (KernelTexture)」のどちらかを持つ共用体。
            // 通常の UI 部品は前者だが、BossModReborn の 3D 投影のように
            // レンダーターゲットを直接貼るものは後者なので、両方を見る。
            FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* kernel = null;
            float hr = 1f;     // テクスチャが等倍の何倍で用意されているか
            bool isIcon = false;
            if (asset->AtkTexture.TextureType == TextureType.KernelTexture)
                kernel = asset->AtkTexture.KernelTexture;
            else
            {
                var res = asset->AtkTexture.Resource;
                if (res != null)
                {
                    kernel = res->KernelTextureObject;
                    var kind = KindOf(res);
                    hr = kind.HiRes;
                    isIcon = kind.IsIcon;
                }
            }
            if (kernel == null) return 0;
            var srv = (nint)kernel->D3D11ShaderResourceView;
            if (srv == 0) return 0;

            // パーツ座標 (U,V,W,H) は「画像の中身」基準だが、**サンプリングの分母は
            // 実際に確保されたテクスチャ面**でなければならない。
            // Kernel.Texture は中身 (ActualWidth/Height) と確保面 (AllocatedWidth/Height) を
            // 別々に持ち、FFXIV の UI テクスチャは中身より大きい面に置かれることがある
            // (実測: 中身 160x152)。中身サイズで割ると**テクスチャの別の場所を読む**ため、
            // 色がネイティブと一致しない (2026-09-12 にピクセル実測で確定)。
            float actW = kernel->ActualWidth, actH = kernel->ActualHeight;
            float allocW = kernel->AllocatedWidth, allocH = kernel->AllocatedHeight;

            // 最も信頼できるのは D3D に直接聞いた寸法。駄目なら Allocated → Actual の順に落とす。
            float tw, th;
            if (TryGetRealTexSize(srv, out var rw, out var rh)) { tw = rw; th = rh; }
            else if (allocW > 0 && allocH > 0) { tw = allocW; th = allocH; }
            else { tw = actW; th = actH; }

            // ゲームの「高解像度時の UI サイズ設定」が 100% より大きいと、
            // ゲームは末尾が _hr1 の**2 倍解像度テクスチャ**を読み込むことがある。
            // このとき AtkUldPart の U/V/Width/Height が**等倍基準のまま**なら、
            // 実テクスチャの寸法で割ると UV が半分になり、左上 1/4 を拡大表示してしまう。
            //
            // ただしパーツ座標が実寸基準で作られていることもある
            // (プラグインが KamiToolKit 等で自前にパーツを組む場合)。
            // その場合に割ると逆に壊れるので、**等倍換算した面にパーツが収まるときだけ**
            // 補正する。収まらない = 実寸基準とみなして触らない。
            float rawW = tw, rawH = th;   // 補正前の実寸 (診断用)
            bool corrected = false;
            if (hr > 1f)
            {
                float baseW = tw / hr, baseH = th / hr;

                // パーツが等倍基準か実寸基準かを、**大きさの桁で**見分ける。
                //
                // 「等倍換算の面にぴったり収まるか」で判定してはいけない。
                // アイコンのパーツは**テクスチャより一回り大きい**ことがあり
                // (実測: part=44x46 に対しテクスチャ 40x40)、収まらない=実寸基準と
                // 誤判定して補正をやめてしまう (JobBars のホットバーで再発した)。
                // 一方、実寸基準で組まれたものは**倍のオーダー**で大きいので、
                // 25% ぶんの余裕を見れば両者は充分に分かれる。
                const float Slack = 1.25f;
                if (part->Width <= baseW * Slack && part->Height <= baseH * Slack)
                {
                    tw = baseW;
                    th = baseH;
                    corrected = true;
                }
                else hr = 1f;   // 実寸基準とみなして触らない (診断にも残る)
            }

            if (tw > 0 && th > 0)
            {
                // パーツ矩形がテクスチャの外へはみ出すことがある。
                // 実測: アイコン (IconImageNode) は part=(0,0,44,46) なのに実テクスチャは 40x40。
                // そのまま割ると UV が 1.1/1.15 まで伸び、中身が左上へ圧縮されて右下が
                // 端の引き伸ばしになる。**ゲームは中身が枠いっぱいに収まるように描く**ので、
                // パーツ矩形をテクスチャの範囲へ収めるのが正しい。
                // (静止状態での実ピクセル比較: 最適倍率 0.90 ≒ 40/44 → 1.1 倍に広げる必要あり)
                float pw = MathF.Min(part->Width, MathF.Max(1f, tw - part->U));
                float ph = MathF.Min(part->Height, MathF.Max(1f, th - part->V));

                // **アイコン**で、パーツが 1 つだけ・原点から・テクスチャより小さい場合は、
                // テクスチャ全体を指しているとみなす。
                //
                // KamiToolKit はノードを作るときにパーツの寸法を決め、後から
                // テクスチャを差し替えても更新しない。ゲームはテクスチャ全体を
                // 描いているので、パーツの寸法どおりに切り出すと一部しか映らない
                // (実測: 素 71x71 のノードで part=32x32 / テクスチャ 128x128 →
                //  左上 1/16 だけを拡大表示していた)。
                //
                // **アイコンに限る**のが要点。ui/uld/ の板 (チェックボックス等) は
                // 1 枚に複数の絵が入っており、パーツ 1 つでも一部を正しく切り出している。
                // ここを巻き込むと設定画面の部品が塗り潰しになる (実際に起きた)。
                if (isIcon && parts->PartCount == 1 && part->U == 0 && part->V == 0
                    && (part->Width < tw || part->Height < th))
                {
                    pw = tw;
                    ph = th;
                }

                item.U0 = part->U / tw;
                item.V0 = part->V / th;
                item.U1 = (part->U + pw) / tw;
                item.V1 = (part->V + ph) / th;
                // 9 分割の計算に使う実ピクセル値。
                item.TexW = tw; item.TexH = th;
                item.PartU = part->U; item.PartV = part->V;
                item.PartW = pw; item.PartH = ph;
                // どの寸法で割ったのかを完全に残す。ここが合っていないと UV がずれる。
                item.NomTex = $"中身{actW:F0}x{actH:F0}/確保{allocW:F0}x{allocH:F0}/実寸{rawW:F0}x{rawH:F0}"
                            + (hr > 1f ? "/hr1" : "") + (isIcon ? "/icon" : "/uld")
                            + (corrected ? "/補正済" : "/無補正");
            }
            return srv;
        }
        catch { return 0; }
    }

    // ============================== 分割計算 ==============================

    /// <summary>ノードを 3x3 に分解した分割位置を求める。
    ///
    /// 9 分割 (NineGrid) は四隅を原寸のまま、辺を片方向、中央を両方向へ伸ばす描画方式で、
    /// ゲージバーのように幅だけが変化するノードはこれで描かれている。
    ///
    /// 9 分割でないノードも同じ 3x3 の形で返す (左右/上下の帯を幅 0 にする) ので、
    /// 呼び出し側は分岐なしに同じループで扱える。</summary>
    private static void ComputeSlices(in Item it,
                                      Span<float> xs, Span<float> ys,
                                      Span<float> us, Span<float> vs)
    {
        bool slice = it.Slice && it.PartW > 0 && it.PartH > 0 && it.TexW > 0 && it.TexH > 0;
        if (!slice)
        {
            // 中央セルだけが面積を持つ縮退形。
            xs[0] = xs[1] = it.X; xs[2] = xs[3] = it.X + it.W;
            ys[0] = ys[1] = it.Y; ys[2] = ys[3] = it.Y + it.H;
            us[0] = us[1] = it.U0; us[2] = us[3] = it.U1;
            vs[0] = vs[1] = it.V0; vs[2] = vs[3] = it.V1;
            return;
        }

        // マージンはテクスチャのピクセル数なので、画面上の大きさへ直してから使う。
        // 直さないと UI 拡大時に四隅だけ元の大きさのままになり、枠が崩れる。
        float sx = it.ScaleX > 0 ? it.ScaleX : 1f, sy = it.ScaleY > 0 ? it.ScaleY : 1f;
        float l = MathF.Max(0, it.SliceL) * sx, r = MathF.Max(0, it.SliceR) * sx;
        float t = MathF.Max(0, it.SliceT) * sy, b = MathF.Max(0, it.SliceB) * sy;
        if (l + r > it.W) { float k = it.W / (l + r); l *= k; r *= k; }
        if (t + b > it.H) { float k = it.H / (t + b); t *= k; b *= k; }

        xs[0] = it.X; xs[1] = it.X + l; xs[2] = it.X + it.W - r; xs[3] = it.X + it.W;
        ys[0] = it.Y; ys[1] = it.Y + t; ys[2] = it.Y + it.H - b; ys[3] = it.Y + it.H;

        // テクスチャ側のマージンはパーツ内のピクセル数そのまま (伸ばさない)。
        float pu = it.PartU, pv = it.PartV;
        us[0] = pu / it.TexW;
        us[1] = (pu + MathF.Min(it.SliceL, it.PartW)) / it.TexW;
        us[2] = (pu + it.PartW - MathF.Min(it.SliceR, it.PartW)) / it.TexW;
        us[3] = (pu + it.PartW) / it.TexW;
        vs[0] = pv / it.TexH;
        vs[1] = (pv + MathF.Min(it.SliceT, it.PartH)) / it.TexH;
        vs[2] = (pv + it.PartH - MathF.Min(it.SliceB, it.PartH)) / it.TexH;
        vs[3] = (pv + it.PartH) / it.TexH;
    }

    /// <summary>[厳密モード] 描画スレッドから呼ぶ。画像ノードを Atk の合成式つきクアッド列へ展開する。
    /// テキストは ImGui 側 (<see cref="Draw"/>) が担当するのでここには含めない。</summary>
    /// <param name="additiveOnAdd">加算色を持つノードを加算合成で描く。</param>
    public int SnapshotQuads(List<AtkQuadRenderer.Quad> dst, bool additiveOnAdd)
    {
        dst.Clear();
        if (!_active) { LastQuadCount = 0; return 0; }

        Span<float> xs = stackalloc float[4], ys = stackalloc float[4];
        Span<float> us = stackalloc float[4], vs = stackalloc float[4];

        lock (_gate)
        {
            foreach (var it in _items)
            {
                if (it.TextureSrv == 0) continue;
                if (dst.Count >= AtkQuadRenderer.MaxQuads) break;

                ComputeSlices(it, xs, ys, us, vs);
                // 合成方法の判定。
                //
                // `AtkNineGridNode.PartsTypeRenderType` の bit2 (=4) は JobBars が
                // ゲージ中身 (BarMain / BarSecondary) にだけ明示的に立てている値で、
                // KamiToolKit では `PartsRenderType.RenderType` と呼ばれる。
                //
                // 当初「blend=0 なので通常合成」と解釈したが、**実機で見比べると
                // 加算合成のほうがネイティブに近い**ことが確認できた (ユーザ検証)。
                // よって BlendMode の値ではなく「RenderType が立っているか」で判定する。
                // 対象は実測でゲージ中身だけなので、他のノードを巻き込まない。
                bool additive = _plugin.cfg.kamiRenderTypeAdditive && (it.PartsRenderType & 0x4) != 0;
                if (additiveOnAdd && (it.AR != 0f || it.AG != 0f || it.AB != 0f))
                    additive = true;   // [実験] 加算色を持つノードを一律で加算合成 (既定 OFF)

                for (var row = 0; row < 3; row++)
                {
                    for (var col = 0; col < 3; col++)
                    {
                        float x0 = xs[col], x1 = xs[col + 1];
                        float y0 = ys[row], y1 = ys[row + 1];
                        if (x1 - x0 <= 0.01f || y1 - y0 <= 0.01f) continue;
                        if (dst.Count >= AtkQuadRenderer.MaxQuads) break;
                        // [調査] 素のテクセルを見たいときは色変換を単位元にする。
                        bool raw = _plugin.cfg.kamiRawTexel;
                        var q = new AtkQuadRenderer.Quad
                        {
                            X0 = x0, Y0 = y0, X1 = x1, Y1 = y1,
                            U0 = us[col], V0 = vs[row], U1 = us[col + 1], V1 = vs[row + 1],
                            Srv = it.TextureSrv,
                            MR = raw ? 1f : it.MR, MG = raw ? 1f : it.MG,
                            MB = raw ? 1f : it.MB, MA = raw ? 1f : it.MA,
                            AR = raw ? 0f : it.AR, AG = raw ? 0f : it.AG, AB = raw ? 0f : it.AB,
                            Bright = raw ? 0f : _plugin.cfg.kamiBrightness,
                            Tile = it.Tile,
                            ClipX = it.ClipX, ClipY = it.ClipY, ClipW = it.ClipW, ClipH = it.ClipH,
                            Additive = !raw && additive,
                        };
                        // 回転ノードは 4 隅を回して渡す (軸並行のままだと向きが違う)。
                        if (MathF.Abs(it.Rotation) > 0.0005f)
                        {
                            float cs = MathF.Cos(it.Rotation), sn = MathF.Sin(it.Rotation);
                            void Rot(float px, float py, out float rx, out float ry)
                            {
                                float dx = px - it.OriginX, dy = py - it.OriginY;
                                rx = it.OriginX + dx * cs - dy * sn;
                                ry = it.OriginY + dx * sn + dy * cs;
                            }
                            q.Rotated = true;
                            Rot(x0, y0, out q.Cx0, out q.Cy0);
                            Rot(x1, y0, out q.Cx1, out q.Cy1);
                            Rot(x0, y1, out q.Cx2, out q.Cy2);
                            Rot(x1, y1, out q.Cx3, out q.Cy3);
                        }
                        dst.Add(q);
                    }
                }
            }
        }
        LastQuadCount = dst.Count;
        return dst.Count;
    }

    // ============================== ImGui 描画 ==============================

    /// <summary>UiBuilder.Draw から呼ぶ。背景ドローリストへ再現描画する
    /// (ネイティブ UI は ImGui プラグイン窓より奥に描かれるため背景が適切)。
    /// 厳密モードのときは画像を <see cref="AtkQuadRenderer"/> が描くので、ここは文字だけを担当する。</summary>
    public void Draw()
    {
        if (!_active) { LastDrawCount = 0; return; }
        int drawn = 0;
        try
        {
            var dl = ImGui.GetBackgroundDrawList();
            bool exact = ExactNow;
            LastExact = exact;

            lock (_gate)
            {
                foreach (var it in _items)
                {
                    var p0 = new System.Numerics.Vector2(it.X, it.Y);
                    var p1 = new System.Numerics.Vector2(it.X + it.W, it.Y + it.H);

                    if (it.Text != null)
                    {
                        if (it.Text.Length == 0) continue;
                        // Atk の FontSize は px 高さではないので係数を掛ける (既定 1.3)。
                        float fs = (it.FontSize > 0 ? it.FontSize : 12f)
                                 * MathF.Max(0.1f, _plugin.cfg.kamiFontScale);

                        // ゲーム本体と同じフォント族で描く。既定の ImGui フォントのままだと
                        // 字形もサイズも元と一致しない (特に数字)。
                        // テキストノードは 1 フレームに数個しか無いので、種別ごとに都度 Push してよい。
                        IDisposable? scope = null;
                        ImFontPtr font = default;
                        bool haveFont = false;
                        var fh = EnsureFont(it.Font);
                        if (fh != null && fh.Available)
                        {
                            try { scope = fh.Push(); font = ImGui.GetFont(); haveFont = !font.IsNull; }
                            catch { haveFont = false; }
                        }
                        try
                        {
                            // 矩形内の寄せを反映する (右寄せ・中央寄せだと左上のままではずれる)。
                            // CalcTextSize は現在フォントの素のサイズで返るので実描画サイズへ換算する。
                            float atlasSize = ImGui.GetFontSize();
                            var raw = ImGui.CalcTextSize(it.Text);
                            var size = atlasSize > 0 ? raw * (fs / atlasSize) : raw;
                            var pt = p0 + AlignOffset(it.Align, it.W, it.H, size);

                            void Put(System.Numerics.Vector2 p, uint col)
                            {
                                if (haveFont) dl.AddText(font, fs, p, col, it.Text);
                                else dl.AddText(p, col, it.Text);
                            }

                            var tf = it.TextFlags;

                            // 縁取りの太さは**文字サイズに比例させない**。
                            // 「大きい字ほど縁も太い」と仮定して fs/12 にしたところ、ゲージの数字
                            // (30px) で縁が 2〜3px になり、Emboss の影と重なって数字が黒い箱に
                            // 潰れた (実機で確認)。ネイティブは大きい字でも 1px 相当だった。
                            const float edge = 1f;
                            const float glare = 2f;

                            // Emboss / Glare は**推測による近似**なので既定では描かない。
                            bool fx = _plugin.cfg.kamiTextEffects;

                            // Emboss: 明るいテーマの浮き出し。先に暗い影を斜め下へ敷く。
                            if (fx && (tf & TextFlags.Emboss) != 0)
                                Put(new System.Numerics.Vector2(pt.X + edge, pt.Y + edge), 0xC0000000);

                            // Glare: 発光。本体色を薄く広めに重ねる。縁取りより外側に出す。
                            if (fx && (tf & TextFlags.Glare) != 0)
                            {
                                uint glow = (it.Color & 0x00FFFFFF) | 0x40000000;
                                for (var k = 0; k < 8; k++)
                                {
                                    float a = k * MathF.PI / 4f;
                                    Put(new System.Numerics.Vector2(pt.X + MathF.Cos(a) * glare,
                                                                   pt.Y + MathF.Sin(a) * glare), glow);
                                }
                            }

                            // 縁取りは 8 方向へずらす近似。
                            // ゲームは距離フィールドで輪郭を解析的に求めるので完全一致はしない。
                            if (it.HasEdge || (tf & TextFlags.Edge) != 0)
                            {
                                for (var k = 0; k < 8; k++)
                                {
                                    float a = k * MathF.PI / 4f;
                                    Put(new System.Numerics.Vector2(pt.X + MathF.Cos(a) * edge,
                                                                   pt.Y + MathF.Sin(a) * edge), it.EdgeColor);
                                }
                            }

                            Put(pt, it.Color);
                            // Bold: 横へ重ねて太らせる。太さも文字サイズに比例。
                            if ((tf & TextFlags.Bold) != 0)
                                Put(new System.Numerics.Vector2(pt.X + edge, pt.Y), it.Color);
                            drawn++;
                        }
                        finally { try { scope?.Dispose(); } catch { } }
                    }
                    else if (it.TextureSrv != 0)
                    {
                        // 厳密モードでは画像は AtkQuadRenderer が描いているので触らない。
                        if (exact) continue;
                        if (it.Slice) DrawNineSlice(dl, it);
                        else
                            dl.AddImage(new ImTextureID(it.TextureSrv), p0, p1,
                                        new System.Numerics.Vector2(it.U0, it.V0),
                                        new System.Numerics.Vector2(it.U1, it.V1),
                                        it.Color);
                        drawn++;
                    }
                }
            }
        }
        catch (Exception ex) { LastError = ex.Message; }
        LastDrawCount = drawn;
    }

    /// <summary>[近似フォールバック] ImGui で 9 分割を描く。厳密モードが使えない環境用。</summary>
    private static void DrawNineSlice(ImDrawListPtr dl, Item it)
    {
        var tex = new ImTextureID(it.TextureSrv);
        Span<float> xs = stackalloc float[4], ys = stackalloc float[4];
        Span<float> us = stackalloc float[4], vs = stackalloc float[4];
        ComputeSlices(it, xs, ys, us, vs);

        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                float x0 = xs[col], x1 = xs[col + 1];
                float y0 = ys[row], y1 = ys[row + 1];
                if (x1 - x0 <= 0.01f || y1 - y0 <= 0.01f) continue;   // 潰れた領域は描かない
                dl.AddImage(tex,
                            new System.Numerics.Vector2(x0, y0),
                            new System.Numerics.Vector2(x1, y1),
                            new System.Numerics.Vector2(us[col], vs[row]),
                            new System.Numerics.Vector2(us[col + 1], vs[row + 1]),
                            it.Color);
            }
        }
    }

    public void Dispose()
    {
        try { ClearReapplyListeners(); } catch { }
        try { if (_active) { _active = false; RestoreAll(); } } catch { }
        try { lock (_gate) ClearItems(); } catch { }
        foreach (var f in _fonts.Values) { try { f?.Dispose(); } catch { } }
        _fonts.Clear();
    }
}
