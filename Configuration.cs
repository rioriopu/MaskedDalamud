using Dalamud.Configuration;
using System;

namespace MaskedDalamud;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>WDA_EXCLUDEFROMCAPTURE を適用中かどうか。</summary>
    public bool enabled { get; set; } = false;

    /// <summary>ホットキー Ctrl+Shift+L でトグルする。</summary>
    public bool useHotkey { get; set; } = true;

    /// <summary>プラグイン読込時に最後の状態を自動復元する。</summary>
    public bool restoreOnLoad { get; set; } = true;

    /// <summary>プラグイン読込時にキャプチャ再認識 (WDA NONE→MONITOR→EXCLUDE + RedrawWindow) を自動実行する。
    /// 配信中に FFXIV を落として再起動した直後のオーバーレイ漏れを救済する。</summary>
    public bool refreshOnLoad { get; set; } = true;

    /// <summary>キャプチャ再認識のラウンド数 (1 回では効かない頑固なキャプチャセッション対策)。</summary>
    public int refreshRounds { get; set; } = 3;

    /// <summary>キャプチャ再認識のラウンド間隔 (ミリ秒)。</summary>
    public int refreshRoundIntervalMs { get; set; } = 500;

    /// <summary>プラグイン破棄/アンロード時に WDA を自動解除するか。
    /// 既定 OFF: FF14 を閉じた瞬間に Dispose で WDA 解除されると、ウィンドウが
    /// まだ描画中の一瞬だけキャプチャに戻り Dalamud UI が配信へ漏れるため。
    /// OFF でも明示的な OFF 操作 (/md off・UI・ホットキー) では解除される。</summary>
    public bool removeWdaOnUnload { get; set; } = false;

    // ====== Dalamud サーバー情報バーへの自前エントリ ======
    // 注: 「DTR の ImGui 置き換え」と「DTR 項目編集」は 2.0.0.1 で廃止した。
    /// <summary>Dalamud の DTR (サーバー情報バー) に「Masked Dalamud」エントリを登録する。
    /// 「● Masked (方式名)」で状態を表示し、左クリックでキャプチャ除外トグル・右クリックで設定。
    /// Dalamud 標準のため並び順・表示は Dalamud 設定の「サーバー情報バー」タブで制御できる。既定 ON。</summary>
    public bool dtrShowSelfEntry { get; set; } = true;


    // ====== [試験] Render Hijack 方式 (RTV リダイレクト・低負荷新方式) ======
    // ====== スクラブ方式 (新・主機能 / Dalamud 無改変) ======
    /// <summary>スクラブ方式 (最終提示スクラブ＋独立 UI リプレイ) を有効化。
    /// /md・ホットキーの主機能。</summary>
    public bool scrubEnabled { get; set; } = false;

    /// <summary>起動時に scrubEnabled が true なら自動で有効化する。</summary>
    public bool scrubRestoreOnLoad { get; set; } = true;

    // ====== スクラブ 負荷軽減 (チェックで個別 ON/OFF・テスト用) ======
    /// <summary>P1: 重い UI リプレイ＋読み戻しを更新間隔にまとめる
    /// (毎フレームは退避/復元のみ)。Splatoon 等の二重描画負荷を直接削減。
    /// 試験機能のため既定 OFF (OFF=従来どおり毎フレーム リプレイ)。</summary>
    public bool scrubThrottleReplay { get; set; } = false;

    /// <summary>P3: CPU のピクセル処理 (約200万px) を行単位で並列化する。
    /// 低スペ多コア CPU で有効。試験機能のため既定 OFF。</summary>
    public bool scrubParallelCopy { get; set; } = false;

    /// <summary>P5: 軽量モード。更新間隔を強制的に引き上げ (最低4) て
    /// 負荷を大きく下げる (描画の滑らかさは低下)。</summary>
    public bool scrubLightweight { get; set; } = false;

    /// <summary>Z [試験]: 読み戻し/CPU処理を UI 使用矩形に限定して負荷を下げる。
    /// 全画面描画プラグインには効果が薄い。残像等のリスクがあるため試験機能。</summary>
    public bool scrubRegionLimit { get; set; } = false;

    // ====== DComp Composition Swapchain (推奨・低負荷新方式) ======
    /// <summary>DComp 方式 (推奨)。DirectComposition Visual + DXGI Composition
    /// Swapchain (premultiplied alpha) で表示し、UpdateLayeredWindow + DWM の
    /// per-pixel alpha 合成コストを撤廃する低負荷新方式。WS_EX_NOREDIRECTIONBITMAP +
    /// 三重入力透過対策で入力問題を回避。他方式と排他。既定 OFF (起動時自動有効化で
    /// dcompRestoreOnLoad を見るためここは前回状態保存用)。</summary>
    public bool dcompOverlayEnabled { get; set; } = false;

    /// <summary>ON のとき、FFXIV 起動 / プラグイン読込時に毎回 DComp を自動で
    /// 有効化する。GPU-GDI と同じく「前回状態 (dcompOverlayEnabled) は関与せず
    /// チェック単独で真実」。既定 ON (推奨方式として常に動かす想定)。</summary>
    public bool dcompRestoreOnLoad { get; set; } = true;


    /// <summary>基本タブ「現在の状態」セクションに動作中方式の実効 FPS (EMA) を
    /// 表示するか。既定 OFF (表示なし)。設定タブから ON/OFF 可能。</summary>
    public bool showStatusFps { get; set; } = false;

    /// <summary>GPU-GDI レイヤードスクラブ (キャプチャ除外 低負荷/GPU処理・推奨)。
    /// 入力安全なレイヤード窓のまま GPU 描画→GetDC→UpdateLayeredWindow で
    /// CPU 読み戻し/変換を撤廃。既定 OFF。他方式とは排他。</summary>
    public bool scrubGdiGpu { get; set; } = false;

    /// <summary>ON のとき、FFXIV 起動 / プラグイン読込時に毎回 GPU-GDI スクラブを
    /// 自動で有効化する。前回状態 (scrubGdiGpu) は関与しない (チェック単独で真実)。
    /// 既定 ON。</summary>
    public bool gdiScrubRestoreOnLoad { get; set; } = true;

    /// <summary>[試験] GPU-GDI 領域限定 ULW。UpdateLayeredWindowIndirect の prcDirty に
    /// 「ImDrawData の触れた矩形」を渡して DWM 再合成領域を UI 領域に絞る軽量化。
    /// 配信側の動作は不変 (backbuffer save/restore は維持)。Splatoon 等の重い半透明 UI
    /// 以外で効果が大きい。既定 OFF (動作確認後に推奨化を検討)。</summary>
    public bool gdiScrubRegionLimit { get; set; } = false;

    // ====== Method A: プラグイン UI だけを配信から隠す方式 ======
    // 既存の WDA (ウィンドウ全体をキャプチャ除外) とは独立した別系統。
    // 一方が失敗してももう一方に影響しないよう完全分離して扱う。
    /// <summary>Method A (ImGui を専用 Layered オーバーレイへ退避) を有効化。</summary>
    public bool methodAEnabled { get; set; } = false;

    /// <summary>(現在未使用) Method A は不安定のため起動時自動開始は行わない。</summary>
    public bool methodARestoreOnLoad { get; set; } = false;

    /// <summary>Layered オーバーレイの更新間隔 (フレーム)。2 = ゲーム60fps時 ≈30fps。
    /// 大きいほど軽いが UI 反応がカクつく。1〜6 程度。</summary>
    public int methodAUpdateIntervalFrames { get; set; } = 2;

    /// <summary>ImGui が何も描いていないフレームは転送をスキップして負荷を抑える。</summary>
    public bool methodASkipWhenEmpty { get; set; } = true;

    // ====== [試験・独立] KamiToolKit ネイティブ UI ミラー (案A) ======
    /// <summary>[試験] KamiToolKit 製のネイティブ UI (JobBars 等) をネイティブ側で非表示にし、
    /// 見た目を ImGui で再現してキャプチャ除外オーバーレイに乗せる。これにより
    /// 「手元には見えるが配信には映らない」をネイティブ UI でも成立させる試み。
    /// ⚠ ノードを隠すため対象 UI の**クリック操作はできなくなる** (表示専用 UI 向け)。既定 OFF。</summary>
    public bool kamiMirrorEnabled { get; set; } = false;

    /// <summary>[試験] KamiToolKit ミラーの診断表示 (検出ノード一覧) を試験タブに出す。既定 OFF。</summary>
    public bool kamiMirrorDiagnostics { get; set; } = false;

    /// <summary>[試験] ミラーの画像を Atk の合成式どおりに専用シェーダで描く。
    /// OFF にすると従来の ImGui 近似 (乗算 tint のみ) に戻る。
    /// 近似では加算色を表現できず「色があせる・発光部分が出ない」原因になるため既定 ON。
    /// capture mode 0 (DrawData リプレイ) では成立しないため自動的に近似へ落ちる。</summary>
    public bool kamiExactColor { get; set; } = true;

    /// <summary>[試験] 加算色を持つノードを**強制的に**加算合成で描く。
    ///
    /// 通常は不要。合成方法は `AtkNineGridNode.BlendMode` から読み取れることが分かったため、
    /// 既定ではそちらに従う (実測: JobBars のゲージ中身は blend=0 = 通常合成)。
    /// これを ON にすると推測で加算にするので**色が薄くなる**。比較用に残してあるだけ。既定 OFF。</summary>
    public bool kamiAdditiveOnAdd { get; set; } = false;

    /// <summary>[調査] 可視判定を無視して、対象枝のノードをすべて描く。
    /// 「描き落としているレイヤーがあるか」を切り分けるための診断用。既定 OFF。</summary>
    public bool kamiIgnoreVisible { get; set; } = false;

    /// <summary>[調査] 画面の一部を切り出して保存するときの大きさ。</summary>
    public int kamiRegionW { get; set; } = 200;
    public int kamiRegionH { get; set; } = 60;

    /// <summary>[調査] 色変換を無効 (M=1, A=0) にして素のテクセルを描く。
    /// ピクセル実測と併用すると「我々が読んでいるテクセル」が直接分かる。既定 OFF。</summary>
    public bool kamiRawTexel { get; set; } = false;

    /// <summary>[調査] 指定座標の backbuffer ピクセル値を読み出して表示する。
    /// ミラー OFF なら「ネイティブが描いた色」、ON なら「我々が描いた色」が出るので、
    /// 目視ではなく**数値で**差を比較できる。既定 OFF。</summary>
    public bool kamiPixelProbe { get; set; } = false;
    public int kamiProbeX { get; set; } = 280;
    public int kamiProbeY { get; set; } = 288;

    /// <summary>[調査] ゲームのピクセルシェーダ バイトコードを採取する。
    ///
    /// Atk の UI シェーダは .shpk にも実行ファイル埋め込み DXBC にも無いことを確認済み。
    /// `ID3D11Device::CreatePixelShader` にはバイトコードが引数で渡ってくるので、そこで捕まえる。
    /// ⚠ ゲームは UI シェーダを**起動時に 1 回だけ**作るため、
    /// ON にしたまま**ゲームを再起動**しないと起動時ぶんは採れない。既定 OFF。</summary>
    public bool kamiShaderCapture { get; set; } = false;

    /// <summary>[調整] 文字サイズの係数。
    ///
    /// Atk の `AtkTextNode.FontSize` は**ピクセル高さではない**。DTR を本家一致まで
    /// 作り込んだ際、そのまま px として渡すと小さくなり、約 1.3 倍が必要と判明している
    /// ([[maskeddalamud-dtr-imgui]])。ネイティブと見比べて調整するための係数。</summary>
    public float kamiFontScale { get; set; } = 1.3f;

    /// <summary>TextFlags の Emboss / Glare を近似表現する。
    ///
    /// 実測で確定: JobBars の文字ノードは**すべて `Glare` のみ**が立っており
    /// (`Edge`/`Emboss` は無し)、Glare を描いた状態でネイティブとの差が
    /// **平均 1.8/765 = 0.2%** まで一致した。**切ると逆に悪化する**ため既定 ON。
    /// Emboss は本環境では出現しなかったため未検証 (KTK は明るいテーマのときだけ付ける)。</summary>
    public bool kamiTextEffects { get; set; } = true;

    /// <summary>[調整] ゲージ等の明るさ補正 (加算・0〜0.2)。
    ///
    /// ⚠ これは**原因が特定できていないための暫定調整**。
    /// ピクセル実測で、ゲームの出力が我々より一定量 (実測 +18/255 前後) 明るいことが分かっている。
    /// シェーダ式・テクスチャ・UV・色の継承規則は IDA で確認して一致させており、
    /// レイヤーの欠落も無いことを確認済みだが、この差だけ説明できていない。
    /// 原因が判明したら本項目は削除すること。
    /// 既定 0.07 は実機でネイティブと見比べて決めた値 (2026-09-13)。</summary>
    public float kamiBrightness { get; set; } = 0.07f;

    /// <summary>[調整] `PartsTypeRenderType` に RenderType (bit2) が立つノードを加算合成で描く。
    ///
    /// JobBars がゲージ中身 (BarMain/BarSecondary) にだけ立てている値。
    /// Atk がこのとき実際にどう合成しているかは読めないため、実機比較で決める。
    /// ON = 加算 (明るい) / OFF = 通常アルファ合成。既定 ON。</summary>
    public bool kamiRenderTypeAdditive { get; set; } = true;

    /// <summary>[検証用] キャプチャ除外 (DComp / GPU-GDI) が停止中でもミラーを動かす。
    ///
    /// 通常は停止中なら待機する (隠す意味が無く、本物を消して近似を見せるだけ損なため)。
    /// ON にすると停止中でも「ネイティブを隠してミラーを描く」状態になるので、
    /// **配信側に出る絵を DComp を切ったままスクリーンショットで確認できる**。
    /// ⚠ この状態では厳密モード (専用シェーダ) が使えないため、画像は ImGui 近似で描かれる。
    /// 色の比較には使えない。配置・文字・隠せているかの確認用。既定 OFF。</summary>
    public bool kamiMirrorAlwaysRun { get; set; } = false;

    /// <summary>[試験] 対象にするプラグイン名の一覧。**空なら全部が対象**。
    ///
    /// KamiToolKit は生成した全ノードを Dalamud の共有データ `TypeMappedCustomNodes`
    /// (ノードポインタ → 生成した Type) に登録している。Type のアセンブリ位置から
    /// どのプラグインのノードかを特定できるため、プラグイン単位で選別できる。</summary>
    public System.Collections.Generic.List<string> kamiTargetPlugins { get; set; } = new();

    /// <summary>[試験] KamiToolKit 専用オーバーレイ (KTK_Overlay_*) のノードだけを対象にする。
    /// ON = JobBars/AetherBars 等の独立ゲージだけを扱う (安全)。
    /// OFF = 既存アドオンに相乗りしたノードも対象にする (取りこぼしが減るが影響範囲が広い)。既定 OFF。</summary>
    public bool kamiOverlayAddonsOnly { get; set; } = false;


    // ====== 設定ウィンドウの表示 ======
    /// <summary>設定ウィンドウを折りたたみ可能にする (タイトルバーの折りたたみ矢印を表示)。
    /// デフォルト OFF (= 折りたたみ不可)。</summary>
    public bool windowCollapsible { get; set; } = true;

    /// <summary>デバッグ用タブ (試験機能/危険) の表示。既定 OFF。</summary>
    public bool debugEnabled { get; set; } = false;

    /// <summary>「予備 (WDA) / 注意」タブを設定ウィンドウに表示するか。既定 OFF (非表示)。
    /// 旧 WDA 方式 (ウィンドウ全体除外) は通常不要なため、試験機能タブのトグルで出す。</summary>
    public bool showWdaTab { get; set; } = false;

    /// <summary>プラグイン起動時に設定ウィンドウを自動で開く。デフォルト ON。</summary>
    public bool autoOpenConfigOnStartup { get; set; } = true;

    // ====== Phase E (試験版): Layered Window 分離方式 ======
    /// <summary>Phase E プロトタイプを有効化する (試験機能)。</summary>
    public bool phaseEEnabled { get; set; } = false;

    // ====== 試験: DComp パフォーマンス調整 ======
    /// <summary>DComp の合成 swapchain Present の sync interval。
    /// 1 = vsync 同期 (既定・安定だが描画スレッドを vblank までブロックし FPS 低下要因)。
    /// 0 = 非ブロッキング (DO_NOT_WAIT フラグは付けない。軽いが GShade で映るか要確認)。</summary>
    public int dcompSyncInterval { get; set; } = 1;

    /// <summary>DComp のオーバーレイ表示 (UI リプレイ + Present) を何フレームに1回行うか。
    /// 1 = 毎フレーム (既定)。2 以上で間引き → FPS 改善。配信非表示のための backbuffer
    /// 退避/復元は間引きに関わらず毎フレーム継続する。</summary>
    public int dcompUpdateIntervalFrames { get; set; } = 1;

    /// <summary>[試験] DComp のオーバーレイ生成方式 (キャプチャモード)。
    /// 0 = 従来 (DrawData を RenderDrawDataInternal でリプレイ)。
    /// 1 = 全画面ミラー (UI 描画後の backbuffer を丸ごとオーバーレイへコピー)。
    /// 2 = 差分キャプチャ (UI 描画後と描画前の差分だけを抽出＝UI レイヤーのみ透過付きで再現)。
    ///
    /// 背景: BossModReborn 7.5.1.9 以降、レーダー等が ImDrawList.AddCallback による
    /// ネイティブ D3D11 直描きへ移行し、しかもコールバックが「一度きり消費」される実装のため、
    /// 従来のリプレイ方式では 2 回目の描画で何も出ず手元から消えてしまう。
    /// 1/2 は ImGui の頂点データに依存しないためこの種の描画も取りこぼさない。
    /// 1 はオーバーレイが不透明全画面になる (検証・切り分け用)。
    /// 既定 2 (差分)。準備に失敗した環境では自動的に 0 (従来) へフォールバックする。</summary>
    public int dcompCaptureMode { get; set; } = 2;

    /// <summary>[試験] AMD (Radeon) 最適化モード。ON のとき DComp の合成 Present を
    /// 非ブロッキング (sync interval 0) に強制し、vsync 待ちによる FPS 低下を回避する。
    /// AMD は MPO が効きにくく DWM 合成へフォールバックして重くなりがちなため、Present の
    /// vblank 待ちを外して緩和する狙い。DO_NOT_WAIT フラグは付けない (GShade 互換維持)。
    /// NVIDIA では通常 OFF。既定 OFF。</summary>
    public bool dcompAmdMode { get; set; } = false;

    /// <summary>GPUベンダーを自動判定し、起動時自動復元で推奨方式を選ぶ。
    /// AMD (Radeon) は MPO が効きにくく DComp が重いため GPU-GDI を選択、
    /// それ以外 (NVIDIA 等・MPO が効く環境) は DComp を選択する。既定 ON。</summary>
    public bool autoSelectByVendor { get; set; } = true;

    /// <summary>ReShade / GShade が検出された環境では DComp 方式を自動選択せず GPU-GDI を選ぶ。
    /// DComp は独立した合成スワップチェインを生成/破棄するため、ReShade の DXGI スワップチェイン
    /// フックと衝突し、無効化・設定変更・表示モード切替時に NVIDIA ドライバごとクラッシュする
    /// 事例があるため (GPU-GDI はレイヤード窓でスワップチェインを作らず安全)。既定 ON。</summary>
    public bool avoidDcompWithReshade { get; set; } = true;

    /// <summary>ReShade 迂回 DComp。合成スワップチェインを ReShade のプロキシ dxgi ではなく
    /// 本物の System32\dxgi.dll の CreateDXGIFactory2 で生成し、ReShade / REST にラップさせない。
    /// これにより ReShade 環境でも DComp の破棄/リサイズが衝突せず安全に使える (実機検証済)。
    /// ON かつ迂回が成立する環境では avoidDcompWithReshade より優先し DComp を使う。迂回が
    /// 成立しない環境 (本物 dxgi 未ロード等) では自動的に GPU-GDI へフォールバックする。既定 ON。</summary>
    public bool dcompReshadeBypass { get; set; } = true;

    // ====== 試験: 排他フルスクリーン → 強制ボーダレス化 ======
    /// <summary>排他フルスクリーンを検知したら自動でボーダレスウィンドウへ変換する (試験・既定OFF)。
    /// 排他FSでは手元UIが出ない制約を回避する狙い。GShade等の swapchain フックと競合する
    /// 可能性があるため試験扱い。</summary>
    public bool forceBorderlessExperimental { get; set; } = false;

    // ====== チャット出力 / ステータス表示 ======
    /// <summary>プラグインのメッセージをゲームチャットに出力するか。
    /// OFF にすると一切チャットへ流さず /xllog のみへ記録する (チャットログ汚染対策)。
    /// 状態は常駐ステータスウィンドウで確認できる。既定 ON。</summary>
    public bool chatNotifications { get; set; } = true;

    /// <summary>常駐ステータスウィンドウ (現在 有効化中/無効化中 を表示) を出すか。
    /// DTR ではなく専用 UI。キャプチャ除外中は配信に映らない。既定 OFF。</summary>
    public bool showStatusWindow { get; set; } = false;

    /// <summary>ステータスウィンドウを移動/リサイズ不可にし、クリックを透過する (邪魔にならない常駐表示)。
    /// 位置を決めたら ON 推奨。既定 OFF (= 移動・配置可能)。</summary>
    public bool statusWindowLocked { get; set; } = false;

    /// <summary>ステータスウィンドウのタイトルバーを隠して小型化する。既定 ON。</summary>
    public bool statusWindowCompact { get; set; } = true;

    // ====== 他プラグインのチャット非表示 ======
    /// <summary>他プラグイン (BossMod / Artisan 等) のシステムメッセージをチャットに表示しない。
    /// プラグインが出力するチャンネル (Dalamud の GeneralChatType・通常 Debug) を抑制する。
    /// 配信中やチャットログ取得中にチャットを汚さないための機能。既定 OFF。
    /// 自分でプラグインコマンドを打った時の返答も消える点に注意 (常時ではなく必要時に ON 推奨)。</summary>
    public bool hidePluginChat { get; set; } = false;

    /// <summary>↑に加えてエラーメッセージ系も抑制する。エラー種別はゲーム本体のエラー
    /// (「ここでは使用できません」等) と共有のため、ON にするとゲームのエラーも消える。既定 OFF。</summary>
    public bool hidePluginChatIncludeErrors { get; set; } = false;

    /// <summary>基本タブの切替ボタンで選んでいる方式。0=自動 / 1=DComp / 2=GPU-GDI / 3=CPU。
    /// 「選んでいる方式」と「今動いているか」は別物として持つ。
    /// 切替ボタンを押しただけでは有効にならず、一番上の ON/OFF で初めて動く。</summary>
    public int basicMethod { get; set; } = 0;

    /// <summary>基本タブを新レイアウト (方式をラジオで 1 つ選ぶ形) で表示する。
    /// 旧レイアウトは方式ごとに独立したチェックが並ぶが、実際には 3 方式は排他なので
    /// 実態と食い違っていた。既定 ON。合わない場合は設定タブから旧レイアウトへ戻せる。</summary>
    public bool newBasicLayout { get; set; } = true;

    // ====== ウィンドウの位置・大きさ ======
    /// <summary>設定ウィンドウの位置/大きさ/折り畳み状態。
    /// EstellUtils の <c>EUi.Windows.BindLayout</c> に渡すと、以降は自動で保存・復元される。
    /// ウィンドウ名をキーにした入れ物なので、将来ウィンドウを増やしてもここは変えなくてよい。</summary>
    public EstellUtils.UI.Windowing.EuWindowLayout WindowLayout { get; set; } = new();

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
