using System;

namespace MaskedDalamud;

/// <summary>
/// プロセス全体で「現在ライブな MaskedDalamud ロード」を識別する世代トークン。
///
/// 背景: Dalamud のホットリロードはプラグインを新しい AssemblyLoadContext に読み込むため、
/// MaskedDalamud 内の static フィールドはロード間で共有されない。そのため旧ロードの
/// overlay (Before/After callback が armed のまま残った「孤児」) は「自分が古い」ことを
/// 自前 static では検知できず、毎フレーム backbuffer をスクラブし続けてしまう
/// (配信側 UI が消えたまま / resize 参照漸増の原因)。
///
/// 解決: 環境変数はプロセス共通 (System.Private.CoreLib は全 ALC で共有) なので、
/// ここに「最新ロードのトークン」を置けば全 ALC から参照できる。各 Plugin はロード時に
/// <see cref="Claim"/> で新トークンを発行・記録し、各 overlay は <see cref="IsCurrent"/> で
/// 自分のトークンが最新か毎フレーム照合する。新ロードが来た瞬間に旧トークンは陳腐化し、
/// 孤児は次フレームで自己停止できる。
/// </summary>
internal static class OverlayGeneration
{
    private const string Key = "MaskedDalamud_OverlayGeneration";

    /// <summary>新しい世代トークンを発行しプロセス全体に記録する。Plugin ロード時に1回呼ぶ。</summary>
    public static string Claim()
    {
        var token = Guid.NewGuid().ToString("N");
        try { Environment.SetEnvironmentVariable(Key, token); } catch { }
        return token;
    }

    /// <summary>token がプロセス最新世代と一致するか。
    /// 取得失敗時は true を返す (= ライブ instance を誤って停止させない安全側)。</summary>
    public static bool IsCurrent(string? token)
    {
        if (token == null) return true;
        try { return Environment.GetEnvironmentVariable(Key) == token; }
        catch { return true; }
    }
}
