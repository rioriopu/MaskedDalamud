using System;

using Dalamud.Plugin.Services;

namespace MaskedDalamud;

// Plugin の機能別 partial: チャット通知 (Say/Err) / 他プラグインのチャット抑制フィルタ /
// 状態通知 (NotifyState/NotifyStatus)。フィールドは Plugin.cs 側に集約。
public partial class Plugin
{
    // ====== チャット通知ヘルパー (cfg.chatNotifications で一括 ON/OFF。OFF 時は /xllog のみ) ======
    // これまで ChatGui.Print/PrintError を直接呼んでいた全箇所をここへ集約し、
    // チャットログ汚染を設定一つで止められるようにした。状態は常駐ステータスウィンドウで確認可能。
    /// <summary>通常メッセージ。チャット出力は cfg.chatNotifications に従う。/xllog には常に記録。</summary>
    internal void Say(string msg)
    {
        Log.Information(msg);
        if (cfg.chatNotifications)
            ChatGui.Print(msg);
    }

    /// <summary>エラーメッセージ。チャット出力は cfg.chatNotifications に従う。/xllog には常に記録。</summary>
    internal void Err(string msg)
    {
        Log.Warning(msg);
        if (cfg.chatNotifications)
            ChatGui.PrintError(msg);
    }

    // ====== 他プラグインのチャット非表示フィルタ ======
    // BossMod / Artisan 等のシステムメッセージは ChatGui.Print 既定で
    // PluginInterface.GeneralChatType (通常 Debug) チャンネルへ出る。そのチャンネルを
    // 抑制することで、個別パターンを登録せずに他プラグインのチャットをまとめて消す。
    // 自分 (Masked Dalamud) のメッセージは prefix で判定して残す (chatNotifications で別管理)。
    private void OnPluginChatFilter(Dalamud.Game.Chat.IHandleableChatMessage message)
    {
        if (!cfg.hidePluginChat)
            return;
        try
        {
            var type = message.LogKind;
            var suppress = type == PluginInterface.GeneralChatType; // プラグイン出力の既定チャンネル
            if (cfg.hidePluginChatIncludeErrors
                && (type == Dalamud.Game.Text.XivChatType.ErrorMessage
                 || type == Dalamud.Game.Text.XivChatType.SystemError))
                suppress = true;
            if (!suppress)
                return;
            // 自分のメッセージは残す。
            var text = message.Message?.TextValue;
            if (text != null && text.Contains("[Masked Dalamud]"))
                return;
            message.PreventOriginal();
        }
        catch { }
    }

    private void NotifyState()
    {
        var aff = GetCurrentAffinity();
        bool actuallyOn = aff == WDA_EXCLUDEFROMCAPTURE;
        bool intended = cfg.enabled;
        bool ok = actuallyOn == intended;

        string label = intended ? "ON (キャプチャ除外)" : "OFF (通常表示)";
        string detail = ok
            ? $"WDA={AffinityToString(aff)}"
            : $"反映されていません WDA={AffinityToString(aff)} (期待={(intended ? "EXCLUDE" : "NONE")})";

        var msg = $"[Masked Dalamud] {label} / {detail}";
        if (ok)
            Say(msg);
        else
            Err(msg);

        try
        {
            NotificationManager.AddNotification(new Dalamud.Interface.ImGuiNotification.Notification
            {
                Title = "Masked Dalamud",
                Content = $"{label}\n{detail}",
                Type = ok
                    ? Dalamud.Interface.ImGuiNotification.NotificationType.Success
                    : Dalamud.Interface.ImGuiNotification.NotificationType.Warning,
            });
        }
        catch { }
    }

    /// <summary>現在の状態 (WDA / プロセス昇格 / HWND) をまとめて Chat に出力する。</summary>
    public void NotifyStatus()
    {
        var hwnd = GetFfxivHwnd();
        var aff = GetCurrentAffinity();
        bool elevated = IsRunningElevated();
        Say("[Masked Dalamud] 状態 ----");
        Say($"  設定:       {(cfg.enabled ? "ON" : "OFF")}");
        Say($"  実際の WDA: {AffinityToString(aff)}");
        Say($"  FFXIV HWND: {(hwnd == IntPtr.Zero ? "未検出" : $"0x{hwnd.ToInt64():X}")}");
        Say($"  プロセス権限: {(elevated ? "管理者" : "通常")}");
        if (!elevated)
        {
            Err("  ⚠ Discord/OBS が管理者権限で起動している場合、FFXIV (Launcher) も管理者で起動しないとキャプチャ除外が bypass されます。");
        }
    }
}
