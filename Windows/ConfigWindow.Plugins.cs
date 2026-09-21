using System;
using System.Numerics;

using EstellUtils.UI;
using EstellUtils.UI.Layout;
using EstellUtils.UI.Widgets;

namespace MaskedDalamud.Windows;

/// <summary>推奨プラグインタブ。
///
/// リポジトリの追加までは代行するが、**インストールそのものは行わない**。
/// 何を入れるかは利用者が決めるべきで、勝手に導入するのは筋が悪い。
/// 「リポジトリを足して、インストール画面をその項目で開く」までを受け持つ。</summary>
public partial class ConfigWindow
{
    /// <summary>リポジトリ追加の結果メッセージ (内部名 → 文言)。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _repoResult = new();

    /// <summary>インストール実行中の内部名。二重押しを防ぐために持つ。
    /// 別スレッドから書き換わるので並行辞書を使う。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _installing = new();

    private void DrawRecommendedTab()
    {
        EUi.Paragraph("Masked Dalamud と相性の良いプラグインです。導入は任意です。");
        EUi.Separator();

        foreach (var e in PluginRecommender.Entries)
            DrawRecommendedEntry(e);
    }

    private void DrawRecommendedEntry(PluginRecommender.Entry e)
    {
        bool installed = PluginRecommender.IsInstalled(e.InternalName);

        using var card = EUi.Card($"##rec_{e.InternalName}");

        using (EUi.HStack())
        {
            EUi.TextColored(e.Title, new Vector4(0.7f, 0.9f, 1f, 1f));

            // 導入済み = 緑〇 / 未導入 = 赤〇
            EUi.TextColored(installed ? "〇 インストール済み" : "〇 未インストール",
                            installed ? new Vector4(0.4f, 0.9f, 0.5f, 1f)
                                      : new Vector4(1f, 0.45f, 0.45f, 1f));
        }

        EUi.Paragraph(e.Summary, EUi.Colors.TextMuted);

        if (installed)
            return;

        // 段取りを 1 つずつにする。
        //   リポジトリ未登録 → 「リポジトリを追加」だけ
        //   登録済み         → 「インストール」だけ
        // 判定できない場合 (Dalamud の作りが変わった等) は追加から始めてもらう。
        bool? hasRepo = PluginRecommender.HasRepo(e.RepoUrl);
        bool busy = _installing.ContainsKey(e.InternalName);
        _repoResult.TryGetValue(e.InternalName, out var msg);

        using (EUi.Disabled(busy))
        using (EUi.HStack())
        {
            if (hasRepo == true)
            {
                if (EUi.Button(busy ? "インストール中…##rec_install_" + e.InternalName
                                    : "インストール##rec_install_" + e.InternalName,
                               ButtonStyle.Primary, width: SizeSpec.Px(160)))
                {
                    _installing[e.InternalName] = true;
                    _repoResult[e.InternalName] = "インストールしています…";

                    // 時間が掛かるので描画スレッドは止めない。
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        var r = await PluginRecommender.InstallAsync(e).ConfigureAwait(false);
                        _repoResult[e.InternalName] = r;
                        _installing.TryRemove(e.InternalName, out _);
                    });
                }
                EUi.Tip("配布元からインストールします。");
            }
            else
            {
                if (EUi.Button("リポジトリを追加##rec_add_" + e.InternalName,
                               ButtonStyle.Primary, width: SizeSpec.Px(180)))
                {
                    _repoResult[e.InternalName] = PluginRecommender.TryAddRepo(e.RepoUrl, out var err)
                        ? "リポジトリを追加しました。続けてインストールできます。"
                        : $"リポジトリを追加できませんでした ({err})。";
                }
                EUi.Tip("Dalamud のカスタムプラグインリポジトリへ登録します。\n"
                      + "登録するとインストールできるようになります。");
            }

            // 自動でうまくいかなかったときだけ、手動の入口を出す。
            if (!string.IsNullOrEmpty(msg) && msg!.Contains("できませんでした"))
            {
                if (EUi.Button("インストール画面を開く##rec_open_" + e.InternalName,
                               width: SizeSpec.Px(190)))
                    PluginRecommender.OpenInstaller(e.Title);
            }
        }

        if (!string.IsNullOrEmpty(msg))
            EUi.Muted(msg!, wrap: true);
    }
}
