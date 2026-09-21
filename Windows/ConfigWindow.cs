using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

using EstellUtils.UI;
using EstellUtils.UI.Layout;
using EstellUtils.UI.Theming;
using EstellUtils.UI.Widgets;

namespace MaskedDalamud.Windows;

public partial class ConfigWindow : EstellUtils.UI.Windowing.EuWindow, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration cfg;

    /// <summary>アセンブリから取ったプラグインのバージョン。タイトルに出す。</summary>
    private static readonly string VersionText =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";

    public ConfigWindow(Plugin plugin) : base("Masked Dalamud 設定")
    {
        this.plugin = plugin;
        this.cfg = plugin.cfg;
        this.Size = new Vector2(460f, 360f);
        this.MinSize = new Vector2(400f, 280f);
        this.MaxSize = new Vector2(900f, 700f);
    }

    /// <summary>見出しにはバージョンを添える。
    /// Name はウィンドウ位置の保存キーなので変えない
    /// (変えるとバージョンを上げるたびに位置と大きさが初期化されてしまう)。</summary>
    public override string GetTitle() => $"Masked Dalamud 設定  v{VersionText}";

    public void Dispose() { }

    public override void Draw()
    {
        // === 説明ヘッダ === (常時表示)
        EUi.Paragraph(
            "OBS / Discord などの配信キャプチャから、プラグイン UI だけ、または "
          + "FFXIV ウィンドウ全体を隠します。");

        // タブはラベルを先に宣言する形。条件付きのタブは配列を組み立ててから渡す。
        var labels = new List<string> { "基本", "推奨プラグイン", "設定" };
        if (cfg.showWdaTab) labels.Add("予備 (WDA) / 注意");
        if (cfg.debugEnabled) labels.Add("試験機能 (危険)");
        labels.Add("ご支援");

        var tabs = EUi.TabBar("##maskedTabs", System.Runtime.InteropServices.CollectionsMarshal.AsSpan(labels));

        if (tabs.IsSelected("基本"))
        {
            if (cfg.newBasicLayout) DrawBasicTab();
            else
            {
                using (var st = EUi.Section("現在の状態", defaultOpen: false, id: "statusFold"))
                    if (st.IsVisible) DrawStatus();
                EUi.Separator();
                DrawCommonSettings();
                EUi.Separator();
                DrawScrub();
                EUi.Separator();
                DrawJobBarsSimple();
            }
        }
        else if (tabs.IsSelected("推奨プラグイン")) DrawRecommendedTab();
        else if (tabs.IsSelected("設定")) DrawDisplaySettings();
        else if (tabs.IsSelected("予備 (WDA) / 注意")) DrawWdaTab();
        else if (tabs.IsSelected("試験機能 (危険)")) DrawExperimentalTab();
        else if (tabs.IsSelected("ご支援")) DrawDonationTab();
    }

    /// <summary>基本タブ: JobBars を隠す簡易スイッチ。
    ///
    /// KamiToolKit を使うプラグインは他にもあるが、**実機で見た目まで詰めたのは JobBars だけ**
    /// なので、表向きの対応はここに限定する。他プラグインを対象にしたい場合は
    /// 試験機能タブの詳細設定から選べる。</summary>
    private void DrawJobBarsSimple()
    {
        const string JobBars = "JobBars";

        EUi.SyncFromImGui();
        EUi.Separator("JobBars の UI を配信から隠す");
        EUi.Paragraph(
            "JobBars のゲージやアイコンは、ゲームの UI レイヤーへ直接描かれるため "
          + "通常のキャプチャ除外では隠せません。これを有効にすると、ゲームと同じ描き方で "
          + "手元にだけ再現し、配信からは消します。");

        if (ToggleRow("JobBars を配信から隠す##jobbarsSimple", cfg.kamiMirrorEnabled, "動作中", out bool on))
        {
            cfg.kamiMirrorEnabled = on;
            // 対象が未設定なら JobBars だけに絞る (表向きの対応範囲)。
            if (on && cfg.kamiTargetPlugins.Count == 0)
                cfg.kamiTargetPlugins.Add(JobBars);
            cfg.Save();
            try { if (on) plugin.kamiMirror?.Enable(); else plugin.kamiMirror?.Disable(); } catch { }
        }
        EUi.WrapColored("隠した UI は「見えるが押せない」状態になります (ゲージ・アイコン等の表示専用 UI 向け)。", new Vector4(1f, 0.8f, 0.3f, 1f));

        if (cfg.kamiMirrorEnabled)
        {
            var t = cfg.kamiTargetPlugins;
            EUi.Label(
                  t.Count == 1 && t[0] == JobBars ? "対象: JobBars"
                : t.Count == 0 ? "対象: KamiToolKit を使う全プラグイン (試験機能タブで変更)"
                : $"対象: {string.Join(", ", t)} (試験機能タブで変更)");

            if (plugin.kamiMirror is { } m && !string.IsNullOrEmpty(m.LastError))
                EUi.WrapColored($"警告: {m.LastError}", new Vector4(1f, 0.5f, 0.4f, 1f));
        }
    }

    /// <summary>方式の ON/OFF と状態表示を 1 行にまとめる。
    ///
    /// 以前は「有効にするチェック」と「ON にするボタン」で**同じ操作が二重**にあり、
    /// さらに状態が別行に出ていたため縦に散らかっていた。ここでトグル 1 つへ集約し、
    /// 状態は右隣に色付き (動作中=緑 / 停止中=赤) で出す。
    /// </summary>
    /// <param name="label">トグルのラベル (##id を含む)。</param>
    /// <param name="active">現在の状態。</param>
    /// <param name="runningText">動作中に出す文言。</param>
    /// <param name="newValue">操作後の値。</param>
    /// <returns>ユーザーが操作したら true。</returns>
    private static bool ToggleRow(string label, bool active, string runningText, out bool newValue)
    {
        bool clicked;
        using (EUi.HStack())
        {
            // 押すと何が起きるかを文言に出す。色は「今の状態」ではなく
            // 「これから行う操作」に付ける (ON にする=アクセント / OFF にする=警告)。
            clicked = EUi.Button((active ? "OFF にする" : "ON にする") + IdOf(label),
                                 active ? ButtonStyle.Danger : ButtonStyle.Primary,
                                 width: SizeSpec.Px(130));

            // HStack が縦中央で揃えてくれるので、高さを合わせる細工は不要。
            EUi.Label(active ? runningText : "停止中",
                      active ? EUi.Colors.Success : EUi.Colors.Danger);
        }
        newValue = clicked ? !active : active;
        return clicked;
    }

    /// <summary>"表示名##id" から "##id" 部分だけ取り出す。無ければ空。</summary>
    private static string IdOf(string label)
    {
        var i = label.IndexOf("##", StringComparison.Ordinal);
        return i < 0 ? "" : label[i..];
    }

    /// <summary>ツールチップ付きのチェックボックス。
    /// EstellUtils はツールチップを戻り値の .Tip() で付けるため、
    /// 「チェック → 直後に説明」という既存の書き方をこの形に集約する。</summary>
    private static bool Check(string label, ref bool value, string tip)
        => EUi.Checkbox(label, ref value).Tip(tip);

    /// <summary>基本タブ最上段の共通設定。オーバーレイ更新間隔 (GPU-GDI / CPU 時のみ表示) と
    /// DTR 非表示自動連携 (DComp 含む全方式) を含む。
    /// 自動軽量化 (X/Y) は 0.1.5.3 で削除。</summary>
    private void DrawCommonSettings()
    {
        EUi.TextColored("共通設定", new Vector4(0.7f, 0.9f, 1f, 1f));

        // ── オーバーレイ更新間隔 (GPU-GDI / CPU 起動時のみ表示) ──
        // DComp は swapchain Present 経路で更新頻度を別管理 (vsync 待ちなし)
        // のためこの設定は適用されない → 動作中は UI を非表示にして混乱を避ける。
        bool showInterval = plugin.GdiScrubActive || plugin.ScrubActive;
        if (showInterval)
        {
            int interval = cfg.methodAUpdateIntervalFrames;
            if (EUi.SliderInt("オーバーレイ更新間隔 (フレーム)##scrubIv", ref interval, 1, 6))
            {
                cfg.methodAUpdateIntervalFrames = Math.Clamp(interval, 1, 6);
                cfg.Save();
            }
            EUi.Tip("GPU-GDI / CPU 方式に適用 (DComp 方式は別経路のため非適用)。");
        }

        // ── Dalamud サーバー情報バーへの自前エントリ ──
        EUi.Separator();
        var dSelf = cfg.dtrShowSelfEntry;
        if (EUi.Checkbox("Dalamud のサーバー情報バーに Masked を表示##dtrSelf", ref dSelf))
        {
            cfg.dtrShowSelfEntry = dSelf;
            cfg.Save();
        }
        EUi.Tip("Dalamud の DTR (サーバー情報バー) に「● Masked (方式名)」を表示します。");

    }

    /// <summary>スクラブ方式 (主機能)。UI だけ配信から隠す / Dalamud 無改変。
    /// 推奨: DComp 方式 (UpdateLayeredWindow + DWM 撤廃で軽量)。
    /// 副: GPU-GDI 方式 (旧推奨・互換性高)。
    /// CPU 高負荷版 (ScrubOverlay) は折り畳み表示。</summary>
    private void DrawScrub()
    {
        // ===== GPU 検出 + 推奨方式の自動選択 =====
        plugin.EnsureGpuVendorDetected();
        plugin.EnsureReshadeDetected();
        // 迂回が有効かつ成立するなら ReShade でも DComp 推奨、でなければ GPU-GDI 推奨。
        bool bypassOk = cfg.dcompReshadeBypass && (plugin.dcompOverlay?.ProbeBypass() ?? false);
        bool reshade = plugin.IsReshadePresent && cfg.avoidDcompWithReshade && !bypassOk;
        var recommend = (plugin.IsAmdGpu || reshade) ? "GPU-GDI" : "DComp";
        EUi.TextColored($"検出GPU: {plugin.GpuVendorName}　推奨方式: {recommend}", new Vector4(0.7f, 0.85f, 1f, 1f));
        if (plugin.IsAmdGpu)
            EUi.Muted("AMD は MPO が効きにくく DComp が重くなりがち → GPU-GDI 推奨");
        if (plugin.IsReshadePresent)
        {
            if (bypassOk)
                EUi.WrapColored("ReShade / GShade 検出: 迂回 DComp が成立しているため、DComp を安全に使用できます"
                  + "(合成SCを本物 dxgi で生成し ReShade/REST にラップさせない)。", new Vector4(0.5f, 1f, 0.7f, 1f));
            else
                EUi.WrapColored("ReShade / GShade 検出: DComp は破棄・表示モード切替時に ReShade と衝突しクラッシュ"
                  + "する事例があるため、GPU-GDI を推奨します (迂回 DComp が不成立の環境)。", new Vector4(1f, 0.8f, 0.3f, 1f));
        }
        var av = cfg.autoSelectByVendor;
        if (EUi.Checkbox("GPUベンダーで推奨方式を自動選択 (AMD→GPU-GDI / 他→DComp)", ref av))
        {
            cfg.autoSelectByVendor = av;
            cfg.Save();
        }
        EUi.Tip("起動時自動復元の際、GPU が AMD なら GPU-GDI、それ以外 (NVIDIA 等)");
        var ar = cfg.avoidDcompWithReshade;
        if (EUi.Checkbox("ReShade / GShade 検出時は DComp を避け GPU-GDI にする", ref ar))
        {
            cfg.avoidDcompWithReshade = ar;
            cfg.Save();
        }
        EUi.Tip("ReShade は DXGI スワップチェインをフックするため、DComp の合成スワップチェイン破棄と");

        // [試験] ReShade 迂回 DComp。
        var bp = cfg.dcompReshadeBypass;
        if (EUi.Checkbox("[試験] ReShade 迂回 DComp を使う (本物 dxgi で合成SC生成)", ref bp))
        {
            cfg.dcompReshadeBypass = bp;
            cfg.Save();
        }
        EUi.Tip("合成スワップチェインを ReShade のプロキシ dxgi ではなく、本物の System32\\dxgi.dll から");
        if (cfg.dcompReshadeBypass && plugin.dcompOverlay != null)
            EUi.Muted($"迂回状態: {plugin.dcompOverlay.BypassStatus}");
        EUi.Separator();

        // 各方式セクションを描画 (DComp → GPU-GDI → CPU 高負荷)。出力順は分割前と同一。
        DrawDCompSection();
        EUi.Separator();
        DrawGdiScrubSection();
        DrawCpuScrubSection();
    }

    // ===== 推奨: DComp 方式 (DirectComposition・最軽量) =====
    private void DrawDCompSection()
    {
        EUi.TextColored("キャプチャ除外 (推奨) — DComp 方式", new Vector4(0.5f, 1f, 0.9f, 1f));
        EUi.Paragraph(
            "プラグイン UI だけを OBS / Discord のキャプチャから隠し、ゲーム画面は "
          + "配信に残します。手元では今までどおり UI が見え、操作もできます。\n"
          + "DirectComposition + DXGI Composition Swapchain による最軽量方式です。");
        bool dc = plugin.DCompActive;
        if (ToggleRow("有効にする##dcompChk", dc, "動作中: DComp", out bool newDc))
        {
            if (newDc) plugin.EnableDComp();
            else       plugin.DisableDComp();
        }
        EUi.Tip("ON: DComp 方式で UI を配信から隠す。");

        var dcRestore = cfg.dcompRestoreOnLoad;
        if (EUi.Checkbox("起動時に自動で有効化する##dcompAuto", ref dcRestore))
        {
            cfg.dcompRestoreOnLoad = dcRestore;
            cfg.Save();
        }
        EUi.Tip("ON にすると、FFXIV 起動 / プラグイン読込時に毎回自動で有効化します");

        // DComp 有効時のみ: 手元 UI 更新間隔 (GPU-GDI / CPU の共通間隔とは独立した DComp 専用)。
        if (dc)
        {
            int dcIv = Math.Clamp(cfg.dcompUpdateIntervalFrames, 1, 5);
            if (EUi.SliderInt("更新間隔 (フレーム)##dcompIv", ref dcIv, 1, 5))
            {
                cfg.dcompUpdateIntervalFrames = Math.Clamp(dcIv, 1, 5);
                cfg.Save();
            }
            EUi.Tip("DComp の手元 UI 更新 (リプレイ + Present) を何フレームに 1 回行うか。");

            // AMD (Radeon) 向け試験オプション: 合成 Present を非ブロッキング化して vsync 待ちを外す。
            var amd = cfg.dcompAmdMode;
            if (EUi.Checkbox("AMD 最適化モード [試験]##dcompAmd", ref amd))
            {
                cfg.dcompAmdMode = amd;
                cfg.Save();
            }
            EUi.Tip("Radeon (AMD) 向けの試験オプション。合成 Present を非ブロッキング (sync interval 0)");
        }
        if (plugin.DCompLastError != null && !dc)
            EUi.WrapColored($"直近エラー: {plugin.DCompLastError}", new Vector4(1f, 0.5f, 0.4f, 1f));
        // ── 注意事項 (折り畳み・既定 閉) ──
        // 中身は移行前と同じく赤文字で出す (見落とすと配信事故に繋がる内容のため)。
        var redNote = new Vector4(1f, 0.4f, 0.3f, 1f);
        using var notes = EUi.Section("⚠ 注意事項", defaultOpen: false);
        if (notes.IsVisible)
        {
            EUi.WrapColored("注意: フルスクリーン排他では Windows の制限により手元の UI が出ません"
              + "(配信非表示は維持)。ボーダーレス/ウィンドウモードでご使用ください。", redNote);
            EUi.WrapColored("注意 (Splatoon): Splatoon による VFX 表示は配信に映ってしまうのため、"
              + "レンダリング設定で有効化している場合は無効にして使用してください。"
              + "(ImGui ベースのシェイプ系は本プラグインで隠せます)", redNote);
            EUi.WrapColored("注意 (BossModReborn の 3D 投影): 『Enable projecting radar into the 3D world』を "
              + "ON にすると、レーダーがゲームの 3D 描画へ直接描かれるため配信に映ります。"
              + "レーダーのウィンドウ自体は隠せますが、3D 投影は隠せません。"
              + "配信中は本オプションを OFF にしてご使用ください。", redNote);
            EUi.WrapColored("注意 (DComp 固有): 環境によっては、Windows 表示ドライバの更新直後や "
              + "TDR (GPU リセット) 発生時に DXGI 経路で再生成が必要になることがあります。"
              + "本実装は Present 異常検知 + swapchain 自動再生成で大半をリカバリしますが、"
              + "万一手元の UI が消えた場合は ON/OFF トグルで初期化してください。", redNote);
            EUi.WrapColored("注意 (NVIDIA 純正リプレイ): キャプチャ除外オーバーレイは OS に「保護コンテンツ」"
              + "として登録されるため、NVIDIA の純正リプレイをデスクトップキャプチャ経路で使うと "
              + "「デスクトップキャプチャが妨げられています」と表示され、インスタントリプレイ等が "
              + "使用できません。NVIDIA 純正リプレイを使う場合は、ビデオ設定からデスクトップ "
              + "キャプチャーをオフにしてください (ゲーム内キャプチャなら本プラグインと併用できます)。"
              + "OBS / Discord のゲームキャプチャ・画面共有には影響しません。", redNote);
        }
    }

    // ===== 副: GPU-GDI 方式 (旧推奨・互換性) =====
    private void DrawGdiScrubSection()
    {
        EUi.TextColored("キャプチャ除外 (GPU-GDI 方式)", new Vector4(0.4f, 1f, 0.7f, 1f));
        EUi.Paragraph(
            "GPU-GDI レイヤード窓 + UpdateLayeredWindow による方式 (旧推奨)。"
          + "DComp が動作しない環境のフォールバックとしてご利用ください。"
          + "DComp 方式とは排他 (ON で DComp は自動 OFF)。");
        bool gd = plugin.GdiScrubActive;
        if (ToggleRow("有効にする##gdiScrubChk", gd, "動作中", out bool newGd))
        {
            if (newGd) plugin.EnableGdiScrub();
            else       plugin.DisableGdiScrub();
        }
        EUi.Tip("ON: GPU-GDI 方式で UI を配信から隠す (旧推奨)。");

        var gdrestore = cfg.gdiScrubRestoreOnLoad;
        if (EUi.Checkbox("起動時に自動で有効化する##gdiScrubAuto", ref gdrestore))
        {
            cfg.gdiScrubRestoreOnLoad = gdrestore;
            cfg.Save();
        }
        EUi.Tip("DComp 自動有効化が ON のときはそちらが優先。");

        // 領域限定ULW (試験): DWM の per-pixel alpha 再合成を UI 矩形だけに絞る。AMD で効く可能性。
        var rl = cfg.gdiScrubRegionLimit;
        if (EUi.Checkbox("領域限定ULW [試験]##gdiRegionLimit", ref rl))
        {
            cfg.gdiScrubRegionLimit = rl;
            cfg.Save();
        }
        EUi.Tip("UpdateLayeredWindowIndirect の prcDirty に UI 矩形を渡し DWM の per-pixel alpha 再合成を絞る)。チャットメッセージに動作中方式名 (GPU-GDI / DComp) を併記。\n0.1.7.0: 他プラグイン (BossMod / Artisan 等) のシステムメッセージをチャットに表示しない機能を追加 (設定タブ『他プラグインのシステムメッセージを非表示にする』。既定 OFF)。プラグインが出力する Dalamud のチャンネル (GeneralChatType・通常 Debug) をまとめて抑制するため、個別登録なしで他プラグインのチャットを消せる。配信中やチャットログ取得中にチャットを汚さないための機能。自分でプラグインのコマンドを打った時の返答も消えるため、常時ではなく必要時に ON 推奨 (Masked Dalamud 自身のメッセージは残る)。サブ設定でエラーメッセージ系も含められる (ゲーム本体のエラーと共有のため既定 OFF・警告付き)。\n0.1.6.0: チャット出力を設定で一括 ON/OFF 可能に (設定タブ『メッセージをゲームチャットに出力する』。OFF で /xllog のみへ記録しチャットログを汚さない)。プラグインのチャット出力を全て集約し、配信中もチャットログを流し続けられるよう改善。DTR ではなく専用の常駐ステータスウィンドウ (『● 有効化中 / ○ 無効化中』+ 動作中の方式名) を追加 — 任意の位置へ配置でき、位置ロック (移動不可・クリック透過)・コンパクト表示 (タイトルバー非表示) に対応。キャプチャ除外が有効な間はプラグイン UI として配信に映らないため、チャットを汚さず手元だけで状態を確認できる。設定タブ『ステータスウィンドウを表示する』および /md ui (window/win) で表示切替。\n0.1.5.17: DComp 方式の『起動時自動有効化』『/md コマンド』『ホットキー』で GShade (ReShade) が再初期化される不具合を修正しました (設定画面のチェックと同じ描画フェーズで有効化するよう統一)。トグルのホットキーを Ctrl+Shift+M → Ctrl+Shift+L に変更。DComp の手元 UI 更新間隔を設定で調整可能に (基本タブ・1〜5 フレーム。間引くと FPS 低下を緩和。配信非表示は不変)。無効化後のウィンドウリサイズで描画サイズが固着した場合に自動で再同期する保険を追加。CPU 方式の設定 UI の体裁を整理。\n0.1.5.13: DComp 方式が排他フルスクリーンでも手元 UI を表示できるようにしました (実機確認済)。DirectComposition 経路により、フルスクリーンのままでも手元の UI が見え、配信側には映りません。これまでフルスクリーンでは手元 UI が自動で隠れていました。\n0.1.5.12: プラグインのホットリロード (セッション中の更新など) で旧オーバーレイが残り続け、無効化しても配信側で UI が消えたまま / ウィンドウリサイズ時に『invalid call to resizeBuffers』が出る不具合を修正。プロセス共通の世代トークンで旧インスタンスを自己停止させるよう堅牢化 (孤児オーバーレイの自滅)。試験機能として『排他フルスクリーン → ボーダレスウィンドウ自動変換』を追加 (既定 OFF・設定『デバッグを有効』ON で『試験機能 (危険)』タブに表示)。排他フルスクリーンは DWM をバイパスするため手元 UI が映らないが、本機能 ON でボーダレス化し手元 UI を表示できる (見た目はフルスクリーンのまま)。GShade 等と競合する可能性があるため試験扱い・問題時は OFF で復元。最も安全なのはゲーム設定で『ボーダレスウィンドウ』を選ぶこと。\n0.1.5.6: GShade (ReShade) 環境で DComp 方式の手元 UI が表示されない不具合を修正。原因は FPS 最適化のために入れていた Present(0, DXGI_PRESENT_DO_NOT_WAIT) のノンブロッキング Present で、GShade 環境下で composition swapchain の表示が不成立になっていた。Present(1, 0) (vsync 同期) に固定して解決。あわせて GShade 検証用に一時追加していた実験コード (Render Hijack 方式 / DComp 共存版 / D1〜D8 トグル / 検出ハイブリッド / /mdd コマンド / 関連の試験フラグ・試験タブ各セクション) を全て削除し、実装を素の DComp 方式へ整理 (試験タブは空)。\n0.1.5.5: /md コマンド (および Ctrl+Shift+M) から DComp 方式を ON にすると GShade (ReShade) が再初期化され画面が真っ暗になる不具合を修正。DComp は新しい DXGI swapchain を生成するため、コマンドのディスパッチスレッドから実行すると ReShade の swapchain フックと競合していた。Enable/Disable を必ず Framework (描画) スレッドへ移譲するよう修正し、設定画面チェックボックスと同じ安全な経路に統一。\n0.1.5.4: /md コマンドの推奨トグルを DComp 方式へ切替 (0.1.5.3 で GPU-GDI のままだった不整合を修正)。基本タブの注意事項 (フルスクリーン排他 / Splatoon / DComp 固有) を赤文字の折り畳みセクション (既定 閉) にまとめて初期表示をスッキリ化。\n0.1.5.3: 推奨方式を **DComp Composition Swapchain** に刷新 (基本タブ『キャプチャ除外 (DComp 方式) — 推奨』)。DirectComposition Visual + DXGI Composition Swapchain で UpdateLayeredWindow + DWM の per-pixel alpha 合成コストを撤廃し、Splatoon 等の重い半透明 UI でも FPS 低下を抑制。独立 DXGI factory (CreateDXGIFactory2) でゲーム swapchain と分離し、ウィンドウ↔仮想フルスクリーン切替時の ResizeBuffers 衝突を回避。WS_EX_NOREDIRECTIONBITMAP + 三重保険 (WS_EX_TRANSPARENT + WM_NCHITTEST=HTTRANSPARENT + WM_MOUSEACTIVATE=NOACTIVATE) で入力安全。Disable 時に COM オブジェクトを順序立てて Release し HWND 占有を確実に解放 (旧 GpuScrub のリーク問題を解消)。Present 異常検知 (DXGI_ERROR_DEVICE_RESET 等) で次フレーム自動 swapchain 再生成。起動時自動有効化 (dcompRestoreOnLoad 既定 ON)。GPU-GDI 方式は副に降格 (互換用)、CPU 高負荷版は折り畳み表示。基本タブ「現在の状態」セクションに動作中方式の実効 FPS (EMA) を表示するトグルを設定タブに追加 (既定 OFF)。共通設定の自動軽量化 (X: 描画量自動 / Y: FPS 追従) を削除 (実用性低のため)。共通設定のオーバーレイ更新間隔は GPU-GDI / CPU 動作中のみ表示 (DComp は別経路)。試験機能タブの中身を全クリアアップ (タブだけ維持)。DTR 非表示連携を DComp を含む全方式に適用。\n0.1.5.2: 旧 WDA 方式を自プロセスの FFXIV 窓のみへ適用するよう修正 (多重起動時に他プロセスへの WDA 試行で出ていた err=5 ERROR_ACCESS_DENIED を抑止)。WDA 自動再有効化の堅牢化 (HWND/affinity が EXCLUDE になるまで最大約 30 秒リトライ・FFXIV 再起動直後の救済)。WDA とスクラブ系 (推奨/CPU/旧GPU合成) の相互排他を統一化: 一方を ON にすると他方は自動 OFF。予備(WDA) タブのレイアウトを基本タブと同じ構造に整理 (有効化チェック+状態+ボタン / 起動時自動有効化 / 詳細設定・クイック操作・運用上の注意は折りたたみ)。設定画面の長い注意文を全て折り返し表示 (TextColored の見切れ問題を解消)。Splatoon の Pictomancy 経由ネイティブ VFX は構造的に隠せないため Description / 基本タブに注意書きを追記 (Splatoon 側でレンダリング設定を無効化することを推奨)。 試験的に追加していた FS試験版 / ミラースワップチェイン / VFX 抑制実験 (StackTrace 走査による呼出元判定) は実機検証で目的を達成できないことが判明し全削除。\n0.1.5.1: 推奨方式 (GPU-GDI) の『起動時に自動で有効化』チェックを入れても次回起動時に有効化されない不具合を修正。チェック単独で毎回 ON に復帰するようロジックを単純化し、ログも追加 (Dalamud ログで [GdiScrub] 起動時自動復元 試行/成功 を確認可)。設定 UI のツールチップも実挙動に合わせて更新。\n0.1.5.0: 推奨方式を **GPU-GDI レイヤード方式** に刷新 (基本タブ『キャプチャ除外 (GPU処理版) — 推奨 | GPU-GDI方式』)。CPU 読み戻し/変換を撤廃し FPS 低下を大幅に抑制しつつ、入力安全なレイヤード窓のままで動作。/md・Ctrl+Shift+M は推奨方式のトグルに統一 (/mdg・/mdl は廃止)。DTR 自動連携の条件を整理 (キャプチャ除外中は強制非表示・除外OFF時は手動切替可)。DTR 永続残留バグ修正 (再読込後にチェック項目が常に隠れたままになる問題)。CPU 方式の警告文を全文表示に修正・現在の方式表示を追加。設定タブにデバッグトグル (試験タブの表示制御)、ウィンドウ折りたたみ既定 ON、ご支援タブを追加。排他フルスクリーン下では Windows 仕様により手元 UI は表示されません (ボーダーレス/ウィンドウ推奨。配信非表示は維持)。\n0.1.4.0: スクラブ式キャプチャ除外で Splatoon など半透明の描画が暗く/色化けして表示される不具合を修正。Dalamud のレンダラ出力(premultiplied alpha)に対する二重 premultiply を解消し、半透明塗り・アンチエイリアス縁の色が正しく表示されるようになりました。不透明 UI の見た目は不変です。\n0.1.3.0: 設定タブ再編(主機能=スクラブ/予備=WDA)。新機能『DTR編集』(サーバー情報バーのプラグイン項目を非表示。DtrIgnore 方式で BossMod 等も確実)。基本タブに『DTR 非表示と自動連携』(キャプチャ除外 ON/OFF に連動)。表示モード/解像度変更後も再有効化不要で追従。ウィンドウ移動時のオーバーレイ追従カクつきを解消(独立タイマー)。安定性・操作性の総合改善。\n0.1.2.0: プラグイン UI だけを配信から隠す方式を安定実装 (主機能)。クリーン映像退避→Dalamud 通常描画→最終提示から UI をピクセル消去し、UI は 専用のキャプチャ除外オーバーレイへ独立リプレイ。Dalamud のレンダラ状態には 一切触れない設計で安定動作。OBS ゲームキャプチャ / Discord 画面共有 両対応。/md・Ctrl+Shift+M がトグル。起動時自動有効化・更新間隔設定・多重起動対応。設定のクイック操作から従来の WDA(ウィンドウ全体除外) も利用可。\n0.0.0.5: 2 垢以上の多重起動対応 (EnumWindows で全 FFXIV HWND に WDA 適用) + キャプチャ再認識を 3 段階 (NONE→MONITOR→EXCLUDE) + RedrawWindow + 複数ループ (デフォルト 3 回) に強化。フレンド環境のように 1 段切替では効かない頑固な WGC セッションを救済。HWND セット変化検知で自動 RefreshCapture 発火。\n0.0.0.4: キャプチャ再認識機能 (配信中ゲーム再起動の救済)。");

        if (plugin.GdiScrubLastError != null && !gd)
            EUi.WrapColored($"直近エラー: {plugin.GdiScrubLastError}", new Vector4(1f, 0.5f, 0.4f, 1f));
    }

    // ===== 折り畳み: CPU 高負荷版 =====
    private void DrawCpuScrubSection()
    {
        EUi.Separator();
        using (var _sec = EUi.Section("キャプチャ除外 (CPU 高負荷版・互換用)", defaultOpen: false, id: "cpuScrubFold")) if (_sec.IsVisible)
        {
            // DComp / GPU-GDI セクションと左端を揃える (Indent しない)。
            EUi.Paragraph(
                "同じ効果を CPU 処理で行う従来方式です。DComp / GPU-GDI が動作しない "
              + "環境の最終フォールバック。これら 2 方式とは排他 (ON で自動 OFF)。");
            EUi.WrapColored("注意: CPU 負荷が高いため、FPS 低下を招きます。クラッシュ等はせず安定"
              + "しますが 10〜50FPS 低下する恐れがあります。戦闘時の FPS 低下を確認して"
              + "から、60FPS 以上であれば使用することをおすすめ致します。", new Vector4(1f, 0.55f, 0.1f, 1f));

            bool active = plugin.ScrubActive;
            if (ToggleRow("有効にする##scrubChk", active, "動作中", out bool newActive))
            {
                if (newActive) plugin.EnableScrub();
                else           plugin.DisableScrub();
            }
            EUi.Tip("ON: CPU 処理で UI を配信から隠す (高負荷)。\nOFF: 通常表示へ。");

            var restore = cfg.scrubRestoreOnLoad;
            if (EUi.Checkbox("起動時に自動で有効化する##scrubAuto", ref restore))
            {
                cfg.scrubRestoreOnLoad = restore;
                cfg.Save();
            }
            EUi.Tip("DComp / GPU-GDI の自動有効化が両方 OFF のときに限り、本トグル ON で");

            if (plugin.ScrubLastError != null && !active)
                EUi.WrapColored($"直近エラー: {plugin.ScrubLastError}", new Vector4(1f, 0.5f, 0.3f, 1f));
        }
    }

    /// <summary>Method A (プラグイン UI だけを配信から隠す) ─ 既存 WDA とは独立系統。</summary>
    private void DrawMethodA()
    {
        EUi.WrapColored("Method A (実験的・不安定) — UI だけを配信から隠す", new Vector4(1f, 0.4f, 0.3f, 1f));
        EUi.WrapColored("⚠ 警告: この方式は GPU デバイス除去でゲームごとクラッシュする場合が "
          + "あります。自己責任でご使用ください。配信中の使用は非推奨です。"
          + "安定動作が必要な場合は下の「ウィンドウ全体をキャプチャ除外」を使用してください。", new Vector4(1f, 0.55f, 0.1f, 1f));
        EUi.Paragraph(
            "ImGui (プラグイン UI) を専用オーバーレイへ退避し、ゲームキャプチャに "
          + "UI だけ映らないようにする実験実装。既定オフ・起動時自動開始なし・"
          + "コマンド/ホットキーの既定からも外しています (明示操作時のみ起動)。");

        bool active = plugin.MethodAActive;
        if (ToggleRow("Method A を有効にする (実験・自己責任)", active, "動作中", out bool newActive))
        {
            if (newActive) plugin.EnableMethodA();
            else           plugin.DisableMethodA();
        }
        EUi.Tip("ON: プラグイン UI を配信(ゲームキャプチャ)から隠す実験機能。");

        // ── 負荷最適化 (更新間隔) ──
        // ※ Method A も基本タブ『共通設定 → オーバーレイ更新間隔』を共有して使う。
        //   UI の重複を避けるためここではスライダーを置かず、現在値の表示と誘導だけ。
        EUi.Muted(
            $"オーバーレイ更新間隔: {cfg.methodAUpdateIntervalFrames} フレーム "
          + "(設定は基本タブの『共通設定』で変更)");

        if (plugin.MethodALastError != null && !active)
        {
            EUi.WrapColored($"直近エラー: {plugin.MethodALastError}", new Vector4(1f, 0.5f, 0.3f, 1f));
            EUi.Muted("(有効化に失敗した理由です。失敗時も WDA 機能は影響を受けません)");
        }
    }

    /// <summary>表示設定タブ: ウィンドウの折りたたみ可否と起動時自動表示の ON/OFF。</summary>
    private void DrawDisplaySettings()
    {
        // ====== 画面の作り ======
        EUi.Separator("基本タブの表示");
        var newLayout = cfg.newBasicLayout;
        if (EUi.Checkbox("新しいレイアウトを使う##newBasicLayout", ref newLayout))
        {
            cfg.newBasicLayout = newLayout;
            cfg.Save();
        }
        EUi.Tip("ON: 方式をラジオから 1 つ選ぶ形。詳細設定は折り畳みの中に入ります。\n"
              + "OFF: 方式ごとにチェックが並ぶ従来の形に戻します。");
        EUi.Separator();

        // ====== チャット出力 / ステータス表示 ======
        EUi.Label("── チャット出力 / 状態表示 ──");

        var chat = cfg.chatNotifications;
        if (EUi.Checkbox("メッセージをゲームチャットに出力する", ref chat))
        {
            cfg.chatNotifications = chat;
            cfg.Save();
        }
        EUi.Muted("(デフォルト: ON)");
        EUi.Paragraph(
            "OFF にすると、プラグインのメッセージ (キャプチャ除外 ON/OFF・診断等) を"
          + "一切チャットへ流さず /xllog のみへ記録します。チャットログを汚したくない場合に OFF。"
          + "状態は下の「ステータスウィンドウ」で確認できます。");

        var showStatus = cfg.showStatusWindow;
        if (EUi.Checkbox("ステータスウィンドウを表示する (有効化中/無効化中)", ref showStatus))
        {
            cfg.showStatusWindow = showStatus;
            cfg.Save();
        }
        EUi.Muted("(デフォルト: OFF)");
        EUi.Paragraph(
            "DTR ではなく専用 UI で現在の状態を常時表示します。任意の位置に配置でき、"
          + "キャプチャ除外が有効な間は配信に映りません (手元のみ)。");

        if (cfg.showStatusWindow)
        {
            var locked = cfg.statusWindowLocked;
            if (EUi.Checkbox("位置をロック (移動不可・クリック透過)##statusLock", ref locked))
            {
                cfg.statusWindowLocked = locked;
                cfg.Save();
            }
            var compact = cfg.statusWindowCompact;
            if (EUi.Checkbox("コンパクト表示 (タイトルバー非表示)##statusCompact", ref compact))
            {
                cfg.statusWindowCompact = compact;
                cfg.Save();
            }
            EUi.Muted("配置するときはロックを外し、決まったらロックすると邪魔になりません。");
        }
        EUi.Separator();

        // ====== 他プラグインのチャット非表示 ======
        EUi.Label("── 他プラグインのチャット ──");

        var hidePlugin = cfg.hidePluginChat;
        if (EUi.Checkbox("他プラグインのシステムメッセージを非表示にする", ref hidePlugin))
        {
            cfg.hidePluginChat = hidePlugin;
            cfg.Save();
        }
        EUi.Muted("(デフォルト: OFF)");
        EUi.Paragraph(
            "BossMod・Artisan など他プラグインがチャットへ流すメッセージ (Dalamud のプラグイン"
          + "出力チャンネル) をまとめて非表示にします。配信中やチャットログ取得中にチャットを"
          + "汚さないための機能です。自分でプラグインのコマンドを打った時の返答も消える点に注意"
          + " (常時ではなく必要時に ON 推奨)。Masked Dalamud 自身のメッセージは残ります。");

        if (cfg.hidePluginChat)
        {
            var inclErr = cfg.hidePluginChatIncludeErrors;
            if (EUi.Checkbox("エラーメッセージも非表示にする##hidePluginErr", ref inclErr))
            {
                cfg.hidePluginChatIncludeErrors = inclErr;
                cfg.Save();
            }
            EUi.Muted("(デフォルト: OFF)");
            EUi.TextColored("⚠ エラー種別はゲーム本体のエラー (「ここでは使用できません」等) と共有のため、"
              + "ON にするとゲームのエラーも消えます。", new Vector4(1f, 0.75f, 0.4f, 1f));
        }
        EUi.Separator();

        EUi.Paragraph("設定ウィンドウの表示に関するオプションです。");

        var collapsible = cfg.windowCollapsible;
        if (EUi.Checkbox("設定ウィンドウを折りたたみ可能にする", ref collapsible))
        {
            cfg.windowCollapsible = collapsible;
            cfg.Save();
            // PreDraw が次フレームで Flags に反映する
        }
        EUi.Muted("(デフォルト: ON)");
        EUi.Paragraph(
            "ON にするとタイトルバーに折りたたみ矢印が表示され、ウィンドウを畳めるようになります。");

        var autoOpen = cfg.autoOpenConfigOnStartup;
        if (EUi.Checkbox("プラグイン起動時に設定ウィンドウを自動で開く", ref autoOpen))
        {
            cfg.autoOpenConfigOnStartup = autoOpen;
            cfg.Save();
        }
        EUi.Muted("(デフォルト: ON)");
        EUi.Paragraph(
            "ON の場合、FFXIV 起動 / プラグイン読込時にこの設定ウィンドウが自動的に開きます。");
        EUi.Separator();

        var debug = cfg.debugEnabled;
        if (EUi.Checkbox("デバッグを有効", ref debug))
        {
            cfg.debugEnabled = debug;
            cfg.Save();
        }
        EUi.Muted("(デフォルト: OFF)");
        EUi.Paragraph(
            "ON にすると「試験機能 (危険)」タブが表示されます。0.1.5.3 では試験項目を全て"
          + "クリアアップしたため空タブですが、将来の試験機能はここに配置されます。"
          + "通常は OFF のままで問題ありません。");
        EUi.Separator();

        var statusFps = cfg.showStatusFps;
        if (EUi.Checkbox("基本タブの「現在の状態」に実効 FPS を表示", ref statusFps))
        {
            cfg.showStatusFps = statusFps;
            cfg.Save();
        }
        EUi.Muted("(デフォルト: OFF)");
        EUi.Paragraph(
            "ON にすると、基本タブの「現在の状態」セクションに動作中方式 "
          + "(DComp / GPU-GDI / CPU) の実効 FPS (EMA) が表示されます。"
          + "OFF (既定) では FPS 行は出ません。");
    }

    /// <summary>試験機能 (危険) タブ。</summary>
    /// <summary>[調査] VFX の観測。NyaDraw / Splatoon の VFX 描画を隠せるかの見極めに使う。
    ///
    /// ここでやるのは**記録だけ**。ゲームには一切干渉しない。
    /// 対象プラグインを ON にした状態と OFF にした状態で 2 回書き出し、
    /// 差分に出たものが「プラグインが作った VFX」になる。
    /// それが安定して言い当てられるなら、隠して描き直す道が開ける。</summary>
    private void DrawVfxProbe()
    {
        EUi.TextColored("VFX の出どころ調査", new Vector4(0.6f, 1f, 0.8f, 1f));
        EUi.Muted("ゲームに作られた VFX を記録します。記録するだけで、表示は変わりません。", wrap: true);

        var probe = plugin.vfxProbe;
        bool on = probe is { IsActive: true };

        using (EUi.HStack())
        {
            if (EUi.Button(on ? "観測を止める##vfxProbeToggle" : "観測を始める##vfxProbeToggle",
                           on ? ButtonStyle.Danger : ButtonStyle.Primary, width: SizeSpec.Px(140)))
            {
                if (on) probe!.Disable();
                else
                {
                    plugin.vfxProbe ??= new VfxProbe();
                    plugin.vfxProbe.Enable();
                }
            }

            if (probe != null)
                EUi.Label($"{probe.TotalCreated} 件 / {probe.DistinctPaths} 種");
        }

        if (probe == null)
        {
            EUi.Muted("未開始");
            return;
        }

        EUi.Muted(probe.Status);

        using (EUi.HStack())
        {
            if (EUi.Button("記録を消す##vfxProbeClear", width: SizeSpec.Px(120)))
                probe.Clear();
            EUi.Tip("対象プラグインを切り替える前に押して、記録を分けます。");

            if (EUi.Button("ON 側を保存##vfxProbeDumpOn", width: SizeSpec.Px(140)))
                _vfxDumpResult = probe.Dump("plugin_on");

            if (EUi.Button("OFF 側を保存##vfxProbeDumpOff", width: SizeSpec.Px(140)))
                _vfxDumpResult = probe.Dump("plugin_off");
        }

        if (!string.IsNullOrEmpty(_vfxDumpResult))
            EUi.Muted($"出力: {_vfxDumpResult}", wrap: true);

        EUi.Muted("手順: 記録を消す → 対象を ON にして数分遊ぶ → 「ON 側を保存」 → "
                + "記録を消す → 対象を OFF にして同じ場所で遊ぶ → 「OFF 側を保存」", wrap: true);
    }

    private string _vfxDumpResult = "";

    private void DrawExperimentalTab()
    {
        // 移行前と同じ赤文字。Note の枠囲みは見た目が変わりすぎるため使わない。
        EUi.WrapColored("⚠ 試験機能です。問題が出たら OFF に戻してください。", new Vector4(1f, 0.4f, 0.4f, 1f));
        EUi.Separator();

        DrawVfxProbe();
        EUi.Separator();

        EUi.TextColored("排他フルスクリーン → 強制ボーダレス化", new Vector4(1f, 0.8f, 0.3f, 1f));
        var fb = cfg.forceBorderlessExperimental;
        if (EUi.Checkbox("排他フルスクリーンを自動でボーダレスウィンドウへ変換", ref fb))
        {
            cfg.forceBorderlessExperimental = fb;
            cfg.Save();
        }
        EUi.Muted("(デフォルト: OFF)");
        EUi.Paragraph(
            "排他フルスクリーンは DWM をバイパスするため、手元 UI (DComp / GPU-GDI) が"
          + "画面に表示されません。この項目を ON にすると、排他フルスクリーンを検知した際に"
          + "自動でゲームを『ボーダレスウィンドウ全画面』へ切り替え、手元 UI を表示できるように"
          + "します (見た目はフルスクリーンのまま)。\n\n"
          + "■ 注意\n"
          + "・GShade / ReShade など swapchain をフックする他ツールと競合する可能性があります"
          + " (暗転等)。問題が出たら OFF に戻してください。\n"
          + "・OFF に戻すと元のウィンドウスタイルへ復元します。\n"
          + "・そもそもゲームのグラフィック設定で『ボーダレスウィンドウ』を選べば本機能なしで"
          + "手元 UI を表示できます (こちらが最も安全)。");
        EUi.Separator();

        // ===== DComp キャプチャ方式 (ネイティブ直描きプラグイン対応) =====
        EUi.TextColored("DComp キャプチャ方式", new Vector4(0.6f, 1f, 0.8f, 1f));
        EUi.WrapColored("BossModReborn 7.5.1.9 以降、レーダー等の描画が ImGui のコールバック経由の "
          + "ネイティブ D3D11 直描きへ移行しました。このコールバックは『一度きり』しか描画しない "
          + "実装のため、従来方式では手元のオーバーレイに再現できずレーダーの一部が消えます。"
          + "既定の『差分キャプチャ』はこれを解消します。通常は変更不要です。", new Vector4(0.8f, 0.85f, 0.95f, 1f));

        int cm = cfg.dcompCaptureMode;
        if (EUi.Radio("2: 差分キャプチャ (既定・推奨)##capMode2", cm == 2))
        { cfg.dcompCaptureMode = 2; cfg.Save(); }
        EUi.Tip("UI を描く前と後の画面を比較し、変化した部分 (＝UI) だけを取り出します。");

        if (EUi.Radio("0: 従来 (DrawData リプレイ)##capMode0", cm == 0))
        { cfg.dcompCaptureMode = 0; cfg.Save(); }
        EUi.Tip("2.0.0.0 より前の方式。ImGui の頂点データを再描画します。");

        if (EUi.Radio("1: 全画面ミラー (切り分け用)##capMode1", cm == 1))
        { cfg.dcompCaptureMode = 1; cfg.Save(); }
        EUi.Tip("UI 描画後の画面を丸ごとオーバーレイへコピーします。");

        if (cfg.dcompCaptureMode == 1)
            EUi.WrapColored("※ 全画面ミラーは常用向けではありません。確認が済んだら 2 に戻してください。", new Vector4(1f, 0.8f, 0.3f, 1f));
        EUi.WrapColored("※ この設定は DComp 方式でのみ有効です (GPU-GDI / CPU 方式には影響しません)。"
          + "差分キャプチャが使えない環境では自動的に従来方式へフォールバックします。", new Vector4(0.75f, 0.8f, 0.9f, 1f));
        EUi.Separator();
        // ===== [試験] KamiToolKit ネイティブ UI (詳細設定) =====
        // 基本タブの「JobBars を配信から隠す」で足りる場合はここを触る必要はない。
        // 対象プラグインの追加や見た目の微調整、調査ツールはすべてここに集約する。
        EUi.SyncFromImGui();
        using (var kamiAll = EUi.Section("KamiToolKit ネイティブ UI の詳細設定", defaultOpen: false, id: "kamiAll"))
        if (kamiAll.IsVisible)
        {
            // ===== KamiToolKit ネイティブ UI を配信から隠す =====
            EUi.TextColored("KamiToolKit ネイティブ UI を配信から隠す", new Vector4(1f, 0.6f, 0.9f, 1f));
            EUi.WrapColored("JobBars など KamiToolKit を使うプラグインの UI は、ゲームの UI レイヤーへ直接描かれるため "
              + "通常のキャプチャ除外では隠せません。この機能は対象をネイティブ側で透明にし、"
              + "ゲームと同じ合成規則で描き直してキャプチャ除外オーバーレイに乗せます。", new Vector4(0.85f, 0.85f, 0.95f, 1f));

            var km = cfg.kamiMirrorEnabled;
            if (EUi.Checkbox("KamiToolKit UI を配信から隠す##kamiMirror", ref km))
            {
                cfg.kamiMirrorEnabled = km;
                cfg.Save();
                try { if (km) plugin.kamiMirror?.Enable(); else plugin.kamiMirror?.Disable(); } catch { }
            }
            EUi.WrapColored("⚠ 対象の UI は「見えるが押せない」状態になります (ゲージ・アイコン等の表示専用 UI 向け)。", new Vector4(1f, 0.5f, 0.5f, 1f));

            // ── 対象プラグインの選別 ──
            if (plugin.kamiMirror is { } km2)
            {
                var owners = km2.DetectedOwners;
                EUi.Label(cfg.kamiTargetPlugins.Count == 0
                    ? "対象プラグイン: すべて"
                    : $"対象プラグイン: {cfg.kamiTargetPlugins.Count} 個を選択中")
                   .Tip("KamiToolKit が記録している「ノード → 生成したプラグイン」の対応から判別しています。\n"
                  + "何も選ばなければ全部が対象です。");

                if (owners.Count == 0)
                    EUi.Label("  (検出なし — 有効にすると一覧に出ます)");
                else
                {
                    foreach (var kv in owners.OrderByDescending(k => k.Value))
                    {
                        bool on = cfg.kamiTargetPlugins.Contains(kv.Key);
                        if (EUi.Checkbox($"{kv.Key} ({kv.Value})##kamiOwner{kv.Key}", ref on))
                        {
                            if (on) { if (!cfg.kamiTargetPlugins.Contains(kv.Key)) cfg.kamiTargetPlugins.Add(kv.Key); }
                            else cfg.kamiTargetPlugins.Remove(kv.Key);
                            cfg.Save();
                        }
                    }
                    if (cfg.kamiTargetPlugins.Count > 0 && EUi.Button("選択を解除して全部対象にする##kamiOwnerAll"))
                    {
                        cfg.kamiTargetPlugins.Clear();
                        cfg.Save();
                    }
                }
            }

            var koa = cfg.kamiOverlayAddonsOnly;
            if (EUi.Checkbox("独立オーバーレイのみ対象にする##kamiOverlayOnly", ref koa))
            {
                cfg.kamiOverlayAddonsOnly = koa;
                cfg.Save();
            }
            EUi.Tip("ON: JobBars/AetherBars のゲージなど、専用レイヤーに描かれる UI だけを対象にします。");

            // ── 困ったとき ──
            if (plugin.kamiMirror is { } kmR)
            {
                if (EUi.Button("UI が消えたまま戻らないとき: 強制復元##kamiForceRestore"))
                {
                    try
                    {
                        var n = kmR.ForceRestoreAll();
                        Plugin.ChatGui.Print($"[Masked Dalamud] 強制復元: {n} ノードのアルファを戻しました。");
                    }
                    catch { }
                }
                EUi.Tip("KamiToolKit ノードのアルファを一律 255 に戻します。");
            }

            // ── 見た目の調整 (通常は触らなくてよい) ──
            using (var tune = EUi.Section("見た目の調整", defaultOpen: false, id: "kamiTune"))
            if (tune.IsVisible)
            {
                EUi.Label("既定値は実機でネイティブと見比べて決めた値です。通常は変更不要です。");

                var kec = cfg.kamiExactColor;
                if (EUi.Checkbox("色を厳密に再現する (専用シェーダ)##kamiExact", ref kec))
                {
                    cfg.kamiExactColor = kec;
                    cfg.Save();
                }
                EUi.Tip("ON: ゲームと同じ合成式を専用シェーダで再現します (既定)。");

                float fsc = cfg.kamiFontScale;
                if (EUi.SliderFloat("文字サイズ係数##kamiFontScale", ref fsc, 0.8f, 2.0f))
                {
                    cfg.kamiFontScale = fsc;
                    cfg.Save();
                }
                EUi.Tip("ゲームの FontSize はピクセル高さではないため係数が必要です (既定 1.30)。");

                float br = cfg.kamiBrightness;
                if (EUi.SliderFloat("明るさ補正##kamiBright", ref br, 0f, 0.2f))
                {
                    cfg.kamiBrightness = br;
                    cfg.Save();
                }
                EUi.Tip("ゲージ等がネイティブより暗い場合に持ち上げます (既定 0.07)。");

                var tfx = cfg.kamiTextEffects;
                if (EUi.Checkbox("文字の発光 (Glare) を再現する##kamiTextFx", ref tfx))
                {
                    cfg.kamiTextEffects = tfx;
                    cfg.Save();
                }
                EUi.Tip("切るとネイティブから離れるため、通常は ON のままにしてください。");

                var rta = cfg.kamiRenderTypeAdditive;
                if (EUi.Checkbox("ゲージ中身を加算合成で描く##kamiRtAdd", ref rta))
                {
                    cfg.kamiRenderTypeAdditive = rta;
                    cfg.Save();
                }
                EUi.Tip("ゲージの中身の合成方法です。ネイティブの明るさに近いほうを選んでください。");

                var kaa = cfg.kamiAdditiveOnAdd;
                if (EUi.Checkbox("加算色のノードを一律で加算合成にする##kamiAdditive", ref kaa))
                {
                    cfg.kamiAdditiveOnAdd = kaa;
                    cfg.Save();
                }
                EUi.Tip("通常は不要です。特定の表示だけ暗い場合に試してください。");
            }

            // ── 調査ツール (開発者向け・デバッグ有効時のみ) ──
            using (var dbg = EUi.Section("調査ツール (開発者向け)", defaultOpen: false, id: "kamiDebug"))
            if (cfg.debugEnabled && dbg.IsVisible)
            {
                EUi.WrapColored("不具合調査用です。通常の利用では触る必要はありません。", new Vector4(1f, 0.8f, 0.4f, 1f));

                EUi.Muted("「キャプチャ除外が停止中でもミラーを動かす」は基本タブへ移動しました。", wrap: true);

                var iv = cfg.kamiIgnoreVisible;
                if (EUi.Checkbox("可視判定を無視して全部描く##kamiIgnoreVis", ref iv))
                {
                    cfg.kamiIgnoreVisible = iv;
                    cfg.Save();
                }

                var rt = cfg.kamiRawTexel;
                if (EUi.Checkbox("色変換を無効にして素のテクセルを描く##kamiRawTexel", ref rt))
                {
                    cfg.kamiRawTexel = rt;
                    cfg.Save();
                }

                var pp = cfg.kamiPixelProbe;
                if (EUi.Checkbox("ピクセル値を実測する##kamiProbe", ref pp))
                {
                    cfg.kamiPixelProbe = pp;
                    cfg.Save();
                }
                if (cfg.kamiPixelProbe)
                {
                    // 座標はピクセル単位で狙って入れるため、スライダーではなく数値入力にする。
                    int px = cfg.kamiProbeX, py = cfg.kamiProbeY;
                    if (EUi.InputInt("X##kamiProbeX", ref px, min: 0, max: 7680)) { cfg.kamiProbeX = px; cfg.Save(); }
                    EUi.Tip("調べたい画面上の X 座標 (ピクセル)。");
                    if (EUi.InputInt("Y##kamiProbeY", ref py, min: 0, max: 4320)) { cfg.kamiProbeY = py; cfg.Save(); }
                    EUi.Tip("調べたい画面上の Y 座標 (ピクセル)。");

                    int rw = cfg.kamiRegionW, rh = cfg.kamiRegionH;
                    if (EUi.InputInt("幅##kamiRegW", ref rw, min: 1, max: 1024)) { cfg.kamiRegionW = rw; cfg.Save(); }
                    if (EUi.InputInt("高さ##kamiRegH", ref rh, min: 1, max: 1024)) { cfg.kamiRegionH = rh; cfg.Save(); }
                    EUi.Tip("上の座標を左上として切り出す範囲の大きさ。");

                    if (plugin.mirrorPreview is { IsActive: true } mpr)
                    {
                        if (EUi.Button("この位置から画像を保存##kamiRegionDump"))
                            mpr.RegionDumpRequested = true;
                        if (EUi.Button("テクスチャを吸い出す##kamiTexDump"))
                            mpr.DumpRequested = true;
                        if (mpr.RegionDumpResult.Length > 0) EUi.Label(mpr.RegionDumpResult);
                        if (mpr.DumpResult.Length > 0) EUi.Label(mpr.DumpResult);
                        if (mpr.CbText.Length > 0)
                            EUi.WrapColored($"ゲームのCB: {mpr.CbText}", new Vector4(0.8f, 0.9f, 1f, 1f));
                        if (mpr.ProbeText.Length > 0)
                            EUi.WrapColored($"実測: {mpr.ProbeText}", new Vector4(0.8f, 0.9f, 1f, 1f));
                    }
                    else EUi.Label("実測: (キャプチャ除外を止めてください)");
                }

                var shc = cfg.kamiShaderCapture;
                if (EUi.Checkbox("ゲームのピクセルシェーダを採取する##kamiShCap", ref shc))
                {
                    cfg.kamiShaderCapture = shc;
                    cfg.Save();
                    try
                    {
                        if (shc) plugin.TryEnableShaderCapture();
                        else plugin.shaderCapture?.Disable();
                    }
                    catch { }
                }
                EUi.Tip("ゲームは UI シェーダを起動時に 1 回だけ作るため、\n792");
                if (plugin.shaderCapture is { } sc2)
                {
                    EUi.Label($"採取: {sc2.Status}");
                    if (sc2.Captured > 0 && EUi.Button("保存先を開く##shCapDir"))
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(sc2.Directory) { UseShellExecute = true }); }
                        catch { }
                    }
                }

                var kd = cfg.kamiMirrorDiagnostics;
                if (EUi.Checkbox("検出ノード一覧を表示##kamiDiag", ref kd))
                {
                    cfg.kamiMirrorDiagnostics = kd;
                    cfg.Save();
                }
                if (cfg.kamiMirrorDiagnostics && plugin.kamiMirror != null)
                {
                    var m = plugin.kamiMirror;
                    EUi.Label($"active={m.Active} / 検出={m.LastNodeCount}"
                                     + $" / ImGui描画={m.LastDrawCount} / クアッド={m.LastQuadCount}"
                                     + $" / exact={m.LastExact}"
                                     + (m.Suspended ? " / 待機中" : ""));
                    var ov = plugin.dcompOverlay;
                    var gd = plugin.gdiScrub;
                    string colorState =
                          ov is { IsActive: true } ? $"[DComp] {ov.KamiStatus}"
                        : gd is { IsActive: true } ? $"[GPU-GDI] {gd.KamiStatus}"
                        : "(キャプチャ除外が停止中)";
                    EUi.Label($"色: {colorState}");
                    EUi.Paragraph($"対象: {m.AddonSummary}");
                    if (!string.IsNullOrEmpty(m.LastError))
                        EUi.WrapColored($"last error: {m.LastError}", new Vector4(1f, 0.5f, 0.4f, 1f));

                    if (EUi.Button("診断をファイルに出力##kamiDumpDiag"))
                        m.DumpDiagRequested = true;
                    if (m.DumpDiagResult.Length > 0) EUi.Label(m.DumpDiagResult);

                    // 診断は座標や数値が縦に並ぶので等幅で桁を揃える。
                    using (EUi.Scroll("##kamiDiagList", 200f))
                    using (EUi.PushFont(FontRole.Mono))
                    {
                        foreach (var line in m.Diagnostics) EUi.Label(line);
                    }
                    if (m.LastSkippedCount > 0)
                    {
                        EUi.TextColored($"可視判定で除外: {m.LastSkippedCount} 個", new Vector4(1f, 0.8f, 0.5f, 1f));
                        foreach (var line in m.SkippedDiagnostics)
                            EUi.Label($"  × {line}");
                    }
                }
            }

        }
        EUi.Separator();

        // ===== 予備 (WDA) タブの表示切替 =====
        EUi.TextColored("予備 (WDA) / 注意 タブの表示", new Vector4(0.7f, 0.85f, 1f, 1f));
        var showWda = cfg.showWdaTab;
        if (EUi.Checkbox("「予備 (WDA) / 注意」タブを表示する##showWda", ref showWda))
        {
            cfg.showWdaTab = showWda;
            cfg.Save();
        }
        EUi.Muted("(デフォルト: OFF)");
        EUi.Paragraph(
            "ウィンドウ全体をキャプチャ除外する旧 WDA 方式のタブを表示します。"
          + "通常は推奨方式 (基本タブ) で十分なため既定で非表示です。");
    }

    /// <summary>予備 (WDA) タブを基本タブと同じレイアウト構造で描く。</summary>
    private void DrawWdaTab()
    {
        // ===== 予備: ウィンドウ全体をキャプチャ除外 (旧 WDA 方式) =====
        EUi.TextColored("キャプチャ除外 (旧 WDA 方式) — 画面全体を除外", new Vector4(0.7f, 0.85f, 1f, 1f));
        EUi.Paragraph(
            "推奨方式 (基本タブ) が環境的に使えない / うまく動かない場合の保険です。"
          + "FFXIV ウィンドウ全体に WDA_EXCLUDEFROMCAPTURE を適用し、OBS / Discord "
          + "などのキャプチャに映らないようにします。スクラブ方式とは排他 (ON で "
          + "推奨/CPU 方式は自動 OFF)。");

        bool wda = plugin.GetCurrentAffinity() == Plugin.WDA_EXCLUDEFROMCAPTURE;
        if (ToggleRow("有効にする##wdaChk", wda, "動作中", out bool newWda))
        {
            if (newWda) plugin.EnableWda();   // 内部でスクラブ系を自動 OFF (排他)
            else        plugin.DisableWda();
        }
        EUi.Tip("ON: FFXIV ウィンドウ全体を配信/録画から除外 (画面ごと黒くなる)。");

        var restoreOnLoad = cfg.restoreOnLoad;
        if (EUi.Checkbox("起動時に自動で有効化する##wdaAuto", ref restoreOnLoad))
        {
            cfg.restoreOnLoad = restoreOnLoad;
            cfg.Save();
        }
        EUi.Tip("ON にすると、前回 WDA が有効だった場合 FFXIV 起動 / プラグイン読込時に");

        EUi.WrapColored("注意: 適用すると配信側だけでなく Windows 標準のスクリーンショットや"
          + "録画ツールでも画面が黒く映ります (画面ごと除外されます)。", new Vector4(1f, 0.55f, 0.1f, 1f));

        // ===== 詳細設定 =====
        EUi.Separator();
        using (var _sec = EUi.Section("詳細設定", defaultOpen: false, id: "wdaAdv")) if (_sec.IsVisible)
        {
            var useHotkey = cfg.useHotkey;
            if (EUi.Checkbox("ホットキー Ctrl+Shift+L でトグル##wdaHotkey", ref useHotkey))
            {
                cfg.useHotkey = useHotkey;
                cfg.Save();
            }

            var refreshOnLoad = cfg.refreshOnLoad;
            if (EUi.Checkbox("起動時に自動でキャプチャ再認識を実行##wdaRefresh", ref refreshOnLoad))
            {
                cfg.refreshOnLoad = refreshOnLoad;
                cfg.Save();
            }
            EUi.Paragraph(
                "配信中に FFXIV を落として再起動した場合、OBS / Discord がオーバーレイを掴んだままになることがあります。"
              + "この自動再認識でその状況を救済できます (通常はオンのままで OK)。");
            var removeOnUnload = cfg.removeWdaOnUnload;
            if (EUi.Checkbox("プラグイン終了時に WDA を自動解除する##wdaRemove", ref removeOnUnload))
            {
                cfg.removeWdaOnUnload = removeOnUnload;
                cfg.Save();
            }
            EUi.Tip("既定 OFF を推奨。");
            if (!removeOnUnload)
                EUi.Muted("(OFF: 配信への一瞬の漏れを防止 ─ 推奨)");
        }

        // ===== クイック操作 =====
        EUi.Separator();
        using (var _sec = EUi.Section("クイック操作", defaultOpen: false, id: "wdaQuick")) if (_sec.IsVisible)
        {
            if (EUi.Button("HWND 再取得##wdaHwnd"))
            {
                plugin.ApplyState(cfg.enabled);
            }
            EUi.Tip("FFXIV のウィンドウハンドルを取り直して現在の設定を再適用します。");
            if (EUi.Button("キャプチャ再認識##wdaRefreshBtn"))
            {
                plugin.RefreshCapture();
            }
            EUi.Tip("OBS / Discord にキャプチャの再評価を強制します。");
        }

        // ===== 運用上の注意 =====
        EUi.Separator();
        using (var _sec = EUi.Section("運用上の注意", defaultOpen: false, id: "wdaNotes")) if (_sec.IsVisible)
            DrawOperationNotes();
    }

    private void DrawStatus()
    {
        // 現在のキャプチャ除外方式 (DComp 推奨 / GPU-GDI / CPU / WDA / 停止中)
        string method;
        Vector4 mcol;
        double? fps = null;
        if (plugin.DCompActive)
        {
            method = "キャプチャ除外 (推奨: DComp 方式) 動作中";
            mcol = new Vector4(0.5f, 1f, 0.9f, 1f);
            fps = plugin.DCompDiag.FpsEma;
        }
        else if (plugin.GdiScrubActive)
        {
            method = "キャプチャ除外 (GPU-GDI 方式) 動作中";
            mcol = new Vector4(0.4f, 1f, 0.5f, 1f);
            fps = plugin.gdiScrub?.LastFpsEma;
        }
        else if (plugin.ScrubActive)
        {
            method = "キャプチャ除外 (CPU 高負荷版) 動作中";
            mcol = new Vector4(1f, 0.7f, 0.4f, 1f);
            fps = plugin.scrub?.LastFpsEma;
        }
        else if (plugin.GetCurrentAffinity() == Plugin.WDA_EXCLUDEFROMCAPTURE)
        {
            method = "予備 WDA (ウィンドウ全体除外) 動作中";
            mcol = new Vector4(1f, 0.7f, 0.4f, 1f);
        }
        else
        {
            method = "停止中 (キャプチャ除外なし)";
            mcol = new Vector4(0.7f, 0.7f, 0.7f, 1f);
        }
        EUi.Label($"現在の方式: {method}", mcol);

        // 動作中の方式について実効 FPS (EMA) を表示 (設定タブで ON のときのみ)。
        if (fps.HasValue && cfg.showStatusFps)
            EUi.Label($"実効 FPS (EMA): {fps.Value:F1}");

        var hwnd = plugin.GetFfxivHwnd();
        EUi.Label($"FFXIV HWND: {(hwnd == IntPtr.Zero ? "未検出" : $"0x{hwnd.ToInt64():X}")}");

        var aff = plugin.GetCurrentAffinity();
        string affStr = aff switch
        {
            0u                            => "WDA_NONE (キャプチャ可)",
            Plugin.WDA_MONITOR            => "WDA_MONITOR (キャプチャ時に黒)",
            Plugin.WDA_EXCLUDEFROMCAPTURE => "WDA_EXCLUDEFROMCAPTURE (キャプチャ対象外)",
            uint.MaxValue                 => "取得不可",
            _                             => $"0x{aff:X8}",
        };
        EUi.Label($"現在の Affinity: {affStr}");

        // 期待と実際の不一致を強調表示 (Discord/OBS 管理者 bypass の早期発見用)
        bool intended = cfg.enabled;
        bool actuallyOn = aff == Plugin.WDA_EXCLUDEFROMCAPTURE;
        if (intended != actuallyOn && hwnd != IntPtr.Zero)
        {
            EUi.WrapColored("⚠ 設定と実際の状態が一致していません。HWND 再取得を試してください。", new Vector4(1f, 0.3f, 0.2f, 1f));
        }

        // プロセス昇格状態
        bool elevated = plugin.IsRunningElevated();
        var elevColor = elevated ? new Vector4(0.4f, 1f, 0.5f, 1f) : new Vector4(1f, 0.7f, 0.2f, 1f);
        EUi.Label($"FFXIV プロセス権限: {(elevated ? "管理者" : "通常")}", elevColor);

        if (!elevated)
        {
            EUi.WrapColored("⚠ Discord / OBS を管理者権限で起動している場合、WDA_EXCLUDEFROMCAPTURE は bypass されオーバーレイが配信に映ります。", new Vector4(1f, 0.55f, 0.1f, 1f));
            EUi.Paragraph(
                "対策: FFXIV (Launcher) も管理者権限で起動するか、Discord / OBS を通常権限で起動してください。");
        }
    }

    private void DrawPhaseE()
    {
        EUi.Label("Phase E (試験版) — Layered Window 分離方式");
        EUi.Paragraph(
            "独立した HWND を作成して WDA_EXCLUDEFROMCAPTURE を適用するプロトタイプ。\n" +
            "現在は基盤検証用に簡単なテストオーバーレイを表示します。本人画面では見え、配信側では映らないことを確認してください。\n" +
            "※ この機能は試験段階のため、リポジトリ配布はしていません。");
        bool phaseE = plugin.PhaseEActive;
        string phaseEBtn = phaseE ? "Phase E を停止" : "Phase E を開始";
        if (EUi.Button(phaseEBtn))
        {
            plugin.TogglePhaseE();
        }
        EUi.Tip("旧 Phase E プロトタイプ (基盤検証用テストオーバーレイ) の開始/停止です。"
              + "現行方式とは無関係の検証用なので、通常は使用しません。");
        EUi.Muted(phaseE ? "(動作中)" : "(停止中)");
    }

    private static void DrawOperationNotes()
    {
        EUi.Paragraph(
            "配信中に FFXIV を落として再起動すると、OBS / Discord がオーバーレイ込みのキャプチャを掴むことがあります。"
          + "そのような時は「キャプチャ再認識」ボタン (または /md refresh) を実行してください。");
        EUi.Paragraph(
            "確実な手順: 配信を一旦停止 → FFXIV を落とす → FFXIV を起動 → 配信再開。");
    }

    private void DrawDonationTab()
    {
        // 見出しは金色、本文は淡い青白、締めの一文は金色。他タブの配色に揃える。
        EUi.TextColored("Masked Dalamud をご利用いただき、誠にありがとうございます", new Vector4(1f, 0.85f, 0.4f, 1f));
        EUi.WrapColored("皆さまの温かいご支援が、本プラグインの開発・メンテナンスを支える大きな力となっております。\n"
          + "頂いたサポートは新機能の開発、不具合修正、FFXIV のメジャーパッチへの追従に大切に使わせていただきます。\n"
          + "今後ともどうぞよろしくお願いいたします。", new Vector4(0.85f, 0.88f, 0.95f, 1f));
        EUi.WrapColored("いつもご支援くださり、心より感謝申し上げます。", new Vector4(1f, 0.85f, 0.4f, 1f));
        EUi.Separator();

        const string url = "https://www.patreon.com/c/SuppotToEstell";
        if (EUi.Button("Patreon で支援する##openpatreon", width: 240))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"Patreon URL を開けませんでした: {ex.Message}");
            }
        }
        EUi.Tip("既定のブラウザで Patreon ページを開きます。");
        if (EUi.Button("URL をコピー##copypatreon", width: 160)
               .Tip("Patreon の URL をクリップボードにコピーします。"))
        {
            EUi.SetClipboard(url);
            EUi.Toast("URL をコピーしました");
        }
        EUi.LabelClipped(url, SizeSpec.Ratio(1f), EUi.Colors.TextMuted);
        EUi.Separator();
        EUi.WrapColored("※ Patreon サイトの利用は外部サービスとして行われます。"
          + "Masked Dalamud は寄付処理には一切関与しません。", new Vector4(0.7f, 0.72f, 0.78f, 1f));
    }
}
