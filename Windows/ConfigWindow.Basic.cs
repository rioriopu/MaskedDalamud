using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using EstellUtils.UI;
using EstellUtils.UI.Layout;
using EstellUtils.UI.Widgets;

namespace MaskedDalamud.Windows;

/// <summary>
/// 基本タブの新レイアウト。
///
/// 旧レイアウトは「DComp を有効にする」「GPU-GDI を有効にする」「CPU を有効にする」が
/// それぞれ独立したチェックとして縦に並んでいた。しかし <c>DisableOtherCaptureMethods</c>
/// のとおり **3 方式は排他** で、同時に 2 つ有効にはできない。
/// つまり画面の形 (独立したチェック) が中身 (排他な 1 択) と食い違っていた。
///
/// ここでは切替ボタン型の <see cref="EUi.SegmentedControl"/> で 1 つ選ぶ形に改めている。
/// あわせて、
///   ・今どうなっているか  → カードにまとめて最上段へ固定
///   ・何を選ぶか          → 切替ボタン
///   ・細かい調整          → 折り畳みの中
/// という順に並べ替えた。説明文は選んだ方式のものだけ出す。
/// </summary>
public partial class ConfigWindow
{
    /// <summary>方式の選択肢。並び順が切替ボタンの並び順になる。
    /// ON/OFF はここに混ぜず、独立したスイッチとして持つ。</summary>
    private enum Pick
    {
        Auto = 0,
        DComp = 1,
        GdiScrub = 2,
        Cpu = 3,
    }

    private static readonly string[] PickLabels = { "自動", "DComp", "GPU-GDI", "CPU" };

    /// <summary>いずれかの方式が動いているか。</summary>
    private bool AnyActive => plugin.DCompActive || plugin.GdiScrubActive || plugin.ScrubActive;

    /// <summary>今動いている方式。止まっていれば null。</summary>
    private Pick? ActivePick =>
          plugin.DCompActive    ? Pick.DComp
        : plugin.GdiScrubActive ? Pick.GdiScrub
        : plugin.ScrubActive    ? Pick.Cpu
        : null;

    /// <summary>切替ボタンで選ばれている方式。動作中はそれが優先。</summary>
    private Pick SelectedPick
    {
        get
        {
            // 動作中は実際に動いているものを見せる。ただし自動選択が ON で
            // 推奨どおりに動いているなら「自動」のままにしておく。
            if (ActivePick is { } a)
                return cfg.autoSelectByVendor && a == Recommended ? Pick.Auto : a;

            return (Pick)Math.Clamp(cfg.basicMethod, 0, PickLabels.Length - 1);
        }
    }

    /// <summary>この環境で薦められる方式。AMD と ReShade 環境は GPU-GDI。</summary>
    private Pick Recommended
    {
        get
        {
            bool bypassOk = cfg.dcompReshadeBypass && (plugin.dcompOverlay?.ProbeBypass() ?? false);
            bool reshade = plugin.IsReshadePresent && cfg.avoidDcompWithReshade && !bypassOk;
            return (plugin.IsAmdGpu || reshade) ? Pick.GdiScrub : Pick.DComp;
        }
    }

    private void DrawBasicTab()
    {
        plugin.EnsureGpuVendorDetected();
        plugin.EnsureReshadeDetected();

        DrawStatusCard();
        DrawMethodPicker();
        DrawPluginTargets();
    }

    // ─────────────────────────────────────────────
    //  状態カード
    // ─────────────────────────────────────────────

    /// <summary>今の状態と主スイッチ。ON/OFF はここだけで行う。</summary>
    private void DrawStatusCard()
    {
        bool running = AnyActive;

        using var card = EUi.Card("##basicStatus");

        using (EUi.HStack())
        {
            // 主スイッチ。方式の切替ボタンでは ON にならないので、入口はここ 1 つ。
            // 押す対象を探さずに済むよう、状態表示の左端に置く。
            if (EUi.Button(running ? "停止##basicMaster" : "開始##basicMaster",
                           running ? ButtonStyle.Danger : ButtonStyle.Primary,
                           width: SizeSpec.Px(80)))
            {
                if (running) StopAll();
                else         ApplyPick(SelectedPick);
            }
            EUi.Tip(running
                ? "配信からの非表示を止めます。"
                : $"選んでいる方式 ({PickLabels[(int)SelectedPick]}) で、配信からの非表示を始めます。");

            // 動作中は点が脈打つ。色だけより気づきやすい。
            EUi.StatusDot(running,
                          running ? $"動作中 — {PickLabels[(int)SelectedPick]}" : "停止中",
                          pulse: running);

        }

        // 環境と実効 FPS。Spacer による右寄せは後ろの要素を消してしまうため使わない。
        using (EUi.HStack())
        {
            EUi.Badge(plugin.GpuVendorName, NoteKind.Info);

            if (plugin.IsReshadePresent)
                EUi.Badge("ReShade", NoteKind.Warning);

            var fps = CurrentFps();
            if (fps is > 0)
                EUi.Muted($"{fps.Value:0} fps");
        }

        if (plugin.DCompLastError != null && !plugin.DCompActive)
            EUi.WrapColored($"直近エラー: {plugin.DCompLastError}", new Vector4(1f, 0.5f, 0.4f, 1f));
    }

    /// <summary>動作中の方式の実効 FPS。止まっていれば null。</summary>
    private double? CurrentFps()
    {
        if (plugin.DCompActive) return plugin.DCompDiag.FpsEma;
        if (plugin.GdiScrubActive) return plugin.gdiScrub?.LastFpsEma;
        if (plugin.ScrubActive) return plugin.scrub?.LastFpsEma;
        return null;
    }

    // ─────────────────────────────────────────────
    //  方式の選択
    // ─────────────────────────────────────────────

    /// <summary>方式の選択。排他なので切替ボタン 1 つにまとめる。
    ///
    /// ここを押しても有効化はしない。止まっているときは選択を覚えるだけで、
    /// 動いているときだけ実際の方式を差し替える。ON/OFF の入口は上のスイッチ 1 つに保つ。</summary>
    private void DrawMethodPicker()
    {
        EUi.Separator("配信から隠す方式");

        int index = (int)SelectedPick;
        if (EUi.SegmentedControl("##basicMethod", ref index, PickLabels, width: SizeSpec.Ratio(1f)))
        {
            cfg.basicMethod = index;
            cfg.Save();

            // 動作中なら方式を差し替える。止まっているなら選択を覚えるだけ。
            if (AnyActive)
                ApplyPick((Pick)index);
        }

        var shown = (Pick)index;
        EUi.Paragraph(shown switch
        {
            Pick.Auto     => $"GPU と他ツールの検出結果に合わせて選びます (今回は {PickLabels[(int)Recommended]})。",
            Pick.DComp    => "DirectComposition による最軽量方式。特に理由がなければこれ。",
            Pick.GdiScrub => "レイヤード窓による方式。DComp が動かない環境向け (AMD / ReShade など)。",
            _             => "CPU 処理のため FPS が 10〜50 落ちます。他が動かないときの最終手段です。",
        }, EUi.Colors.TextMuted);

        if (shown == Pick.Cpu)
            EUi.Note("FPS 低下が大きい方式です。戦闘中の FPS を確認してからお使いください。",
                     NoteKind.Warning);

        // 「自動」は実体を持たないので、詳細設定では解決後の方式で出す。
        // そうしないと自動を選んだ途端に DComp 固有の項目 (ReShade 迂回) が消えてしまう。
        DrawMethodDetails(shown == Pick.Auto ? Recommended : shown);
    }

    /// <summary>方式ごとの細かい調整。既定は畳んでおく。
    /// 引数は解決済みの方式 (自動は渡ってこない)。</summary>
    private void DrawMethodDetails(Pick shown)
    {
        using var adv = EUi.Section("詳細設定", defaultOpen: false, id: "basicAdv");
        if (!adv.IsVisible)
            return;

        using var inset = EUi.Inset();

        // 自動を選んでいるときは、どの方式の設定を出しているかを明示する。
        if (SelectedPick == Pick.Auto)
            EUi.Muted($"自動選択の結果 {PickLabels[(int)shown]} の設定を表示しています。");

        var restore = shown switch
        {
            Pick.GdiScrub => cfg.gdiScrubRestoreOnLoad,
            Pick.Cpu      => cfg.scrubRestoreOnLoad,
            _             => cfg.dcompRestoreOnLoad,
        };
        if (EUi.Toggle("起動時に自動で有効化する##basicAuto", ref restore))
        {
            switch (shown)
            {
                case Pick.GdiScrub: cfg.gdiScrubRestoreOnLoad = restore; break;
                case Pick.Cpu:      cfg.scrubRestoreOnLoad = restore; break;
                default:            cfg.dcompRestoreOnLoad = restore; break;
            }
            cfg.Save();
        }
        EUi.Tip("FFXIV 起動 / プラグイン読込のたびに、この方式を自動で有効化します。");

        var dtr = cfg.dtrShowSelfEntry;
        if (EUi.Toggle("サーバー情報バーに状態を表示##basicDtr", ref dtr))
        {
            cfg.dtrShowSelfEntry = dtr;
            cfg.Save();
        }
        EUi.Tip("Dalamud のサーバー情報バーへ「● Masked (方式名)」を出します。");

        if (shown == Pick.DComp)
        {
            var bp = cfg.dcompReshadeBypass;
            if (EUi.Toggle("ReShade 迂回を使う##basicBypass", ref bp))
            {
                cfg.dcompReshadeBypass = bp;
                cfg.Save();
            }
            EUi.Tip("合成スワップチェインを ReShade のプロキシではなく本物の dxgi.dll から作ります。"
                  + "ReShade / GShade 環境で DComp を使いたい場合に。");
        }

        // 更新間隔。DComp だけ別経路なので設定項目が分かれている。
        EUi.Separator("負荷の調整");
        if (shown == Pick.DComp)
        {
            int iv = cfg.dcompUpdateIntervalFrames;
            if (EUi.SliderInt("更新間隔 (フレーム)##basicDcIv", ref iv, 1, 5))
            {
                cfg.dcompUpdateIntervalFrames = Math.Clamp(iv, 1, 5);
                cfg.Save();
            }
            EUi.Tip("手元 UI を何フレームに 1 回描き直すか。大きくすると軽くなりますが、"
                  + "手元の UI がわずかに遅れて見えます。");
        }
        else
        {
            int iv = cfg.methodAUpdateIntervalFrames;
            if (EUi.SliderInt("更新間隔 (フレーム)##basicIv", ref iv, 1, 6))
            {
                cfg.methodAUpdateIntervalFrames = Math.Clamp(iv, 1, 6);
                cfg.Save();
            }
            EUi.Tip("オーバーレイを何フレームに 1 回更新するか。");
        }

        DrawOperationNotes();
    }

    // ─────────────────────────────────────────────
    //  プラグイン別
    // ─────────────────────────────────────────────

    /// <summary>プラグインごとの上乗せ設定。今は JobBars のみ。</summary>
    private void DrawPluginTargets()
    {
        EUi.Separator("プラグイン別の追加対応");

        EUi.Muted("ゲームの UI レイヤーへ直接描くプラグインは、上の方式だけでは隠せません。", wrap: true);

        using var card = EUi.Card("##basicTargetsCard");

        // 動作確認済みのものは常に出す。実際に検出されたものはその後ろへ足していく
        // (KamiToolKit を使うプラグインは他にもあるが、実機で見た目まで詰めたのはこの 2 つ)。
        var rows = new List<(string Name, int Nodes)>
        {
            ("JobBars", 0),
            ("BossModReborn", 0),
            ("SimpleTweaksPlugin", 0),
        };

        if (plugin.kamiMirror is { } km)
        {
            foreach (var kv in km.DetectedOwners.OrderByDescending(k => k.Value))
            {
                int i = rows.FindIndex(r => r.Name == kv.Key);
                if (i >= 0) rows[i] = (kv.Key, kv.Value);
                else if (kv.Key != "(不明)") rows.Add((kv.Key, kv.Value));
            }
        }

        foreach (var row in rows)
            TargetRow(row.Name, row.Nodes);

        EUi.Muted("検出されたプラグインがここに並びます。動作確認済みは JobBars と BossModReborn です。", wrap: true);

        if (cfg.kamiMirrorEnabled && plugin.kamiMirror is { } m && !string.IsNullOrEmpty(m.LastError))
            EUi.WrapColored($"警告: {m.LastError}", new Vector4(1f, 0.5f, 0.4f, 1f));

        DrawMirrorInspectSwitch();
    }

    /// <summary>不具合を調べるための切り替え。カードの外へ出して、
    /// 「普段は触らないもの」と分かる位置に置く。</summary>
    private void DrawMirrorInspectSwitch()
    {
        EUi.WrapColored("不具合調査用です。通常の利用では触る必要はありません。",
                        new Vector4(1f, 0.8f, 0.4f, 1f));

        var kar = cfg.kamiMirrorAlwaysRun;
        if (EUi.Toggle("キャプチャ除外が停止中でもミラーを動かす##kamiAlwaysRun", ref kar))
        {
            cfg.kamiMirrorAlwaysRun = kar;
            cfg.Save();
        }
        EUi.Tip("キャプチャ除外を切ったまま『配信側に出る絵』を画面に出します。\n"
              + "見た目の確認や診断の出力に使います。");

        if (cfg.kamiMirrorAlwaysRun && !plugin.CaptureScrubActive)
            EUi.WrapColored("検証モード稼働中 — 配信保護は働いていません。",
                            new Vector4(1f, 0.6f, 0.3f, 1f));
    }

    /// <summary>対象プラグイン 1 行ぶん。トグルの実体は kamiTargetPlugins への出し入れで、
    /// 1 つでも選ばれていればミラー自体を動かす。</summary>
    private void TargetRow(string name, int nodes)
    {
        var targets = cfg.kamiTargetPlugins;
        bool on = targets.Contains(name);

        // Spacer は残り幅を全部取ってしまい、後ろの要素が幅 0 になって消える。
        // 右寄せは列幅を宣言した Row で行う。
        //
        // ただし Badge は内容ぴったりの幅で場所を取り、宣言した列幅に従わない。
        // そのまま並べると「隠す」と「そのまま」の字数差だけ後ろがずれるため、
        // Sized で列幅ぶんの枠を確保し、その中へ入れて位置を揃える。
        using (EUi.Row(SizeSpec.Ratio(0.46f), SizeSpec.Ratio(0.12f), SizeSpec.Ratio(0.24f), SizeSpec.Ratio(0.18f)))
        {
            EUi.Label(name);

            using (EUi.Sized(SizeSpec.Ratio(1f), EUi.Metrics.WidgetHeight))
                if (nodes > 0) EUi.Badge($"{nodes}", NoteKind.Info).Tip("検出したノード数。");

            using (EUi.Sized(SizeSpec.Ratio(1f), EUi.Metrics.WidgetHeight))
                EUi.Badge(on ? "隠す" : "そのまま", on ? NoteKind.Success : NoteKind.Info, filled: on);

            var v = on;
            if (EUi.Toggle($"##target_{name}", ref v))
            {
                if (v) { if (!targets.Contains(name)) targets.Add(name); }
                else targets.Remove(name);

                // 対象が 1 つでもあればミラーを動かす。全部外れたら止める。
                bool want = targets.Count > 0;
                cfg.kamiMirrorEnabled = want;
                cfg.Save();
                try { if (want) plugin.kamiMirror?.Enable(); else plugin.kamiMirror?.Disable(); } catch { }
            }
            EUi.Tip(name switch
            {
                "JobBars" => "ゲージやアイコンを、ゲームと同じ描き方で手元にだけ再現します。",
                "BossModReborn" => "『Enable projecting radar into the 3D world』で 3D 世界へ投影される"
                                 + "レーダーを隠します。レーダーのウィンドウ表示は方式側で隠れます。",
                "SimpleTweaksPlugin" => "SimpleTweaks の『滑り撃ちキャストバー』"
                                      + "(スライドキャストマーカー) を隠します。\n"
                                      + "※ 動作を確認できているのはこの項目だけです。\n"
                                      + "※ ゲーム本体の UI の位置や大きさを変えるだけの項目"
                                      + "(キャストバーの位置調整など) は、ゲームの UI そのものなので隠せません。",
                _ => "このプラグインがゲームの UI レイヤーへ描いている分を隠します (未検証)。",
            } + "\n隠した UI は「見えるが押せない」状態になります。");
        }
    }

    // ─────────────────────────────────────────────

    /// <summary>切替ボタンで選ばれたものを実際に適用する。</summary>
    private void ApplyPick(Pick pick)
    {
        // 「自動」は設定として持てないので、自動選択フラグを立てて推奨方式を起動する。
        bool auto = pick == Pick.Auto;
        if (cfg.autoSelectByVendor != auto)
        {
            cfg.autoSelectByVendor = auto;
            cfg.Save();
        }
        if (auto)
            pick = Recommended;

        switch (pick)
        {
            case Pick.DComp:    plugin.EnableDComp(); break;
            case Pick.GdiScrub: plugin.EnableGdiScrub(); break;
            case Pick.Cpu:      plugin.EnableScrub(); break;
            default:            StopAll(); break;
        }
    }

    /// <summary>動いている方式を止める。排他なので実際に動くのは 1 つだけ。</summary>
    private void StopAll()
    {
        if (plugin.DCompActive) plugin.DisableDComp();
        if (plugin.GdiScrubActive) plugin.DisableGdiScrub();
        if (plugin.ScrubActive) plugin.DisableScrub();
    }
}
