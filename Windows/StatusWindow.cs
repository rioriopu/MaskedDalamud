using System;
using System.Numerics;

using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace MaskedDalamud.Windows;

// DTR (サーバー情報バー) ではなく専用 UI で「現在 有効化中/無効化中」を示す常駐ウィンドウ。
// ・任意の位置へ配置可能 (ドラッグ移動)。位置は ImGui が保存。
// ・ロックすると移動/リサイズ不可＋クリック透過の邪魔にならない常駐表示になる。
// ・キャプチャ除外が有効な間は、これ自体プラグイン UI なので配信には映らない
//   → チャットログを汚さずに状態を手元だけで確認できる。
public class StatusWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration cfg;

    public StatusWindow(Plugin plugin) : base("Masked Dalamud##MdStatus")
    {
        this.plugin = plugin;
        this.cfg = plugin.cfg;
        Size = new Vector2(190f, 0f);
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
    }

    public void Dispose() { }

    // 設定の表示フラグに応じて毎フレーム開閉を決める。
    public override void PreOpenCheck()
    {
        IsOpen = cfg.showStatusWindow;
    }

    public override void PreDraw()
    {
        var f = ImGuiWindowFlags.AlwaysAutoResize
              | ImGuiWindowFlags.NoScrollbar
              | ImGuiWindowFlags.NoScrollWithMouse
              | ImGuiWindowFlags.NoFocusOnAppearing
              | ImGuiWindowFlags.NoNav;
        if (cfg.statusWindowCompact)
            f |= ImGuiWindowFlags.NoTitleBar;
        if (cfg.statusWindowLocked)
            f |= ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoInputs;
        Flags = f;
    }

    public override void Draw()
    {
        var active = plugin.AnyMaskingActive;
        var (mark, color) = active
            ? ("● 有効化中", new Vector4(0.35f, 0.90f, 0.40f, 1f))
            : ("○ 無効化中", new Vector4(0.65f, 0.65f, 0.65f, 1f));

        // コンパクト時はタイトルバーが無いので、行頭に小さな見出しを付ける。
        if (cfg.statusWindowCompact)
        {
            ImGui.TextColored(new Vector4(0.70f, 0.85f, 1f, 1f), "Masked");
            ImGui.SameLine();
        }
        ImGui.TextColored(color, mark);

        if (active)
        {
            var method = plugin.ActiveMethodLabel;
            if (!string.IsNullOrEmpty(method))
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"({method})");
            }
        }
    }
}
