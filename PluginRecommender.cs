using System;
using System.Linq;
using System.Reflection;

using Dalamud.Interface;

namespace MaskedDalamud;

/// <summary>
/// 推奨プラグインの導入を手助けする。
///
/// Dalamud は「導入済みか調べる」「インストール画面を開く」は公開 API を持つが、
/// **カスタムリポジトリの追加は公開されていない**。そこだけは内部構造へ
/// リフレクションで触る必要がある (Questionable 等も同じ方法)。
///
/// 内部に触る以上、Dalamud の更新で動かなくなることがある。そのため
///   ・例外は外へ出さず、失敗として返すだけ
///   ・失敗したら URL をコピーして手動で追加してもらう案内に切り替える
/// という作りにして、**本体の動作には一切影響させない**。
/// </summary>
internal static class PluginRecommender
{
    /// <summary>推奨する 1 件。</summary>
    /// <param name="Author">作者名。他所の成果を紹介する以上、必ず名前を添える。</param>
    internal sealed record Entry(string InternalName, string Title, string Author, string Summary, string RepoUrl);

    /// <summary>推奨プラグインの一覧。</summary>
    internal static readonly Entry[] Entries =
    {
        new("DTROverlay",
            "DTROverlay",
            "mirage",
            "サーバー情報バー (DTR) の見た目を自由に変えられます。"
          + "Masked Dalamud と併用すると、DTR の表示を配信から隠せるようになります。",
            "https://raw.githubusercontent.com/exatrines/DalamudPlugins/refs/heads/main/pluginmaster.json"),
    };

    /// <summary>導入済みか。読み込み済みでなくても、入っていれば true。</summary>
    internal static bool IsInstalled(string internalName)
    {
        try
        {
            return Plugin.PluginInterface.InstalledPlugins
                .Any(p => string.Equals(p.InternalName, internalName, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>そのリポジトリが既に登録されているか。判定できなければ null。</summary>
    internal static bool? HasRepo(string url)
    {
        try
        {
            var list = GetRepoList();
            if (list == null) return null;

            foreach (var item in list)
            {
                var u = item?.GetType().GetProperty("Url")?.GetValue(item) as string;
                if (Same(u, url)) return true;
            }
            return false;
        }
        catch { return null; }
    }

    /// <summary>リポジトリを追加する。成功したら true。</summary>
    internal static bool TryAddRepo(string url, out string error)
    {
        error = "";
        try
        {
            var dalamud = typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly;

            var settingsType = dalamud.GetType("Dalamud.Configuration.ThirdPartyRepoSettings");
            if (settingsType == null) { error = "リポジトリ設定の型が見つかりません"; return false; }

            var list = GetRepoList();
            if (list == null) { error = "リポジトリ一覧を取得できません"; return false; }

            // 既にあるなら有効化だけして終わる。
            foreach (var item in list)
            {
                var u = item?.GetType().GetProperty("Url")?.GetValue(item) as string;
                if (!Same(u, url)) continue;
                item!.GetType().GetProperty("IsEnabled")?.SetValue(item, true);
                SaveAndReload(dalamud);
                return true;
            }

            var entry = Activator.CreateInstance(settingsType);
            if (entry == null) { error = "リポジトリ設定を作成できません"; return false; }
            settingsType.GetProperty("Url")?.SetValue(entry, url);
            settingsType.GetProperty("IsEnabled")?.SetValue(entry, true);

            var add = list.GetType().GetMethod("Add");
            if (add == null) { error = "リポジトリ一覧へ追加できません"; return false; }
            add.Invoke(list, new[] { entry });

            SaveAndReload(dalamud);
            Plugin.Log.Info($"[Recommender] リポジトリを追加: {url}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Plugin.Log.Warning($"[Recommender] リポジトリ追加に失敗: {ex.Message}");
            return false;
        }
    }

    /// <summary>リポジトリを追加してインストールまで行う。
    ///
    /// Dalamud の <c>PluginManager.InstallPluginAsync</c> をリフレクションで呼ぶ。
    /// 配布一覧 (AvailablePlugins) から該当の manifest を探して渡すので、
    /// リポジトリ JSON を自前で解釈する必要はない。
    ///
    /// **リポジトリの読み直しは非同期**なので、追加直後は一覧に載っていない。
    /// 少し待って現れなければ諦め、インストール画面へ誘導する。</summary>
    internal static async System.Threading.Tasks.Task<string> InstallAsync(Entry e)
    {
        try
        {
            if (IsInstalled(e.InternalName)) return "既にインストール済みです。";

            if (HasRepo(e.RepoUrl) != true && !TryAddRepo(e.RepoUrl, out var addErr))
                return $"リポジトリを追加できませんでした ({addErr})。URL を手動で登録してください。";

            var dalamud = typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly;
            var pm = GetPluginManager(dalamud);
            if (pm == null) return "Dalamud の内部構造が変わっているため、自動インストールできません。";

            // 追加したリポジトリが読み込まれるのを待つ (最大 15 秒)。
            object? manifest = null;
            for (var i = 0; i < 30 && manifest == null; i++)
            {
                manifest = FindManifest(pm, e.InternalName);
                if (manifest == null) await System.Threading.Tasks.Task.Delay(500).ConfigureAwait(false);
            }
            if (manifest == null)
                return "配布一覧に見つかりませんでした。インストール画面から導入してください。";

            var install = pm.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name == "InstallPluginAsync")
                .OrderBy(m => m.GetParameters().Length)
                .FirstOrDefault();
            if (install == null) return "インストール用の処理が見つかりません。";

            // 引数の並びは Dalamud の版で変わり得るので、型を見て埋める。
            //
            // **bool は false にすること。** ここは useTesting で、true にすると
            // 配布情報の「テスト版のダウンロード先」を使う。多くのリポジトリはそこが
            // 空なので、空の URL を取りにいって
            // 「An invalid request URI was provided」で失敗する (実機で発生)。
            var ps = install.GetParameters();
            var args = new object?[ps.Length];
            for (var i = 0; i < ps.Length; i++)
            {
                var t = ps[i].ParameterType;
                args[i] = i == 0 ? manifest
                        : t == typeof(bool) ? false
                        : t.IsEnum ? LoadReason(t)
                        : ps[i].HasDefaultValue ? ps[i].DefaultValue
                        : t.IsValueType ? Activator.CreateInstance(t)
                        : null;
            }

            if (install.Invoke(pm, args) is not System.Threading.Tasks.Task task)
                return "インストールを開始できませんでした。";
            await task.ConfigureAwait(false);

            Plugin.Log.Info($"[Recommender] インストール実行: {e.InternalName}");
            return IsInstalled(e.InternalName)
                ? "インストールしました。"
                : "インストールを実行しましたが確認できませんでした。インストール画面をご確認ください。";
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Recommender] インストール失敗: {ex.Message}");
            return $"インストールに失敗しました ({ex.InnerException?.Message ?? ex.Message})。"
                 + "インストール画面から導入してください。";
        }
    }

    /// <summary>読み込み理由の列挙から「インストーラ経由」を選ぶ。無ければ既定値。</summary>
    private static object? LoadReason(Type enumType)
    {
        try
        {
            foreach (var name in Enum.GetNames(enumType))
                if (name.Equals("Installer", StringComparison.OrdinalIgnoreCase))
                    return Enum.Parse(enumType, name);
        }
        catch { }
        return Activator.CreateInstance(enumType);
    }

    /// <summary>配布一覧から内部名で manifest を探す。</summary>
    private static object? FindManifest(object pm, string internalName)
    {
        try
        {
            if (pm.GetType().GetProperty("AvailablePlugins")?.GetValue(pm) is not System.Collections.IEnumerable list)
                return null;

            foreach (var m in list)
            {
                var n = m?.GetType().GetProperty("InternalName")?.GetValue(m) as string;
                if (string.Equals(n, internalName, StringComparison.Ordinal)) return m;
            }
        }
        catch { }
        return null;
    }

    private static object? GetPluginManager(Assembly dalamud)
    {
        try
        {
            var pmType = dalamud.GetType("Dalamud.Plugin.Internal.PluginManager");
            var svcType = dalamud.GetType("Dalamud.Service`1");
            if (pmType == null || svcType == null) return null;
            var get = svcType.MakeGenericType(pmType)
                             .GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return get?.Invoke(null, null);
        }
        catch { return null; }
    }

    /// <summary>インストール画面を、そのプラグインを検索した状態で開く。</summary>
    internal static void OpenInstaller(string searchText)
    {
        try { Plugin.PluginInterface.OpenPluginInstallerTo(PluginInstallerOpenKind.AllPlugins, searchText); }
        catch (Exception ex) { Plugin.Log.Warning($"[Recommender] インストール画面を開けません: {ex.Message}"); }
    }

    // ───────────────────────── 内部 ─────────────────────────

    /// <summary>Dalamud 本体の設定が持つリポジトリ一覧 (List&lt;ThirdPartyRepoSettings&gt;)。</summary>
    private static System.Collections.IList? GetRepoList()
    {
        var cfg = GetConfiguration();
        return cfg?.GetType().GetProperty("ThirdRepoList")?.GetValue(cfg) as System.Collections.IList;
    }

    /// <summary>Dalamud 本体の設定インスタンス。Service&lt;DalamudConfiguration&gt;.Get() を呼ぶ。</summary>
    private static object? GetConfiguration()
    {
        try
        {
            var dalamud = typeof(Dalamud.Plugin.IDalamudPluginInterface).Assembly;
            var cfgType = dalamud.GetType("Dalamud.Configuration.Internal.DalamudConfiguration");
            var svcType = dalamud.GetType("Dalamud.Service`1");
            if (cfgType == null || svcType == null) return null;

            var svc = svcType.MakeGenericType(cfgType);
            var get = svc.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return get?.Invoke(null, null);
        }
        catch { return null; }
    }

    /// <summary>設定を保存し、リポジトリを読み直させる。
    /// 読み直しは非同期なので待たない (待つと描画スレッドが止まる)。</summary>
    private static void SaveAndReload(Assembly dalamud)
    {
        try
        {
            var cfg = GetConfiguration();
            cfg?.GetType().GetMethod("QueueSave")?.Invoke(cfg, null);
        }
        catch { }

        try
        {
            var pmType = dalamud.GetType("Dalamud.Plugin.Internal.PluginManager");
            var svcType = dalamud.GetType("Dalamud.Service`1");
            if (pmType == null || svcType == null) return;

            var svc = svcType.MakeGenericType(pmType);
            var get = svc.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var pm = get?.Invoke(null, null);
            if (pm == null) return;

            pmType.GetMethod("SetPluginReposFromConfigAsync", new[] { typeof(bool) })?.Invoke(pm, new object[] { true });
        }
        catch (Exception ex) { Plugin.Log.Warning($"[Recommender] リポジトリ再読込に失敗: {ex.Message}"); }
    }

    private static bool Same(string? a, string b)
        => !string.IsNullOrEmpty(a) && string.Equals(a!.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
