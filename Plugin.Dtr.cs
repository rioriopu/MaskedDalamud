using System;

namespace MaskedDalamud;

// Plugin の機能別 partial: DTR (サーバー情報バー) 関連。
// 2.0.0.1 で「DTR の ImGui 置き換え」と「DTR 項目編集」を廃止し、
// Dalamud のサーバー情報バーへ登録する自前エントリ (● Masked / ○ Masked) のみを残した。
public partial class Plugin
{
    /// <summary>Dalamud サーバー情報バーの自前エントリ (● Masked / ○ Masked) を更新する。
    /// 状態が変わった時のみ Text を作り直す (● は緑)。表示可否は cfg.dtrShowSelfEntry。</summary>
    private void UpdateSelfDtrEntry()
    {
        if (_selfDtrEntry == null) return;
        _selfDtrEntry.Shown = cfg.dtrShowSelfEntry;
        if (!cfg.dtrShowSelfEntry) return;

        bool active = AnyMaskingActive;
        var method = ActiveMethodLabel;
        string text = active
            ? (string.IsNullOrEmpty(method) ? "● Masked" : $"● Masked ({method})")
            : "○ Masked";
        if (text == _selfDtrText) return; // 状態に変化が無ければ作り直さない
        _selfDtrText = text;

        var sb = new Dalamud.Game.Text.SeStringHandling.SeStringBuilder();
        if (active)
        {
            sb.AddUiForeground("●", 45); // 緑系のカラーキー
            sb.AddText(string.IsNullOrEmpty(method) ? " Masked" : $" Masked ({method})");
        }
        else
        {
            sb.AddText("○ Masked");
        }
        _selfDtrEntry.Text = sb.Build();
    }
}
