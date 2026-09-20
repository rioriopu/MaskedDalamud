using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Dalamud.Hooking;

using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using InteropGenerator.Runtime;

namespace MaskedDalamud;

/// <summary>
/// [調査] ゲームの VFX が「誰に作られたか」を見分けられるかを確かめるための観測専用フック。
///
/// NyaDraw や Splatoon (Use VFX Rendering) は、自分で絵を描くのではなく
/// **ゲーム本体に VFX を出させて**いる。3D シーンは手元と配信で 1 つしかないため、
/// 差分キャプチャでは分離できず「構造的に隠せない」としてきた。
///
/// 突破口があるとすれば、
///   ① プラグインが作った VFX を見分ける   ← ここが最大の壁。**本クラスの検証対象**
///   ② 見分けたものをゲームの描画から消す   (アルファを 0 にする。実現可能)
///   ③ 手元のオーバーレイへ描き直す         (深度なしの近似になる)
/// の 3 段。①が成立しなければ②③に進む意味がない。
///
/// 以前は呼び出し元を StackTrace で辿る方法を試して失敗している
/// (managed コードは JIT 領域で動くため、DLL のアドレス範囲と照合できない)。
/// そこで本クラスは**呼び出し元を追わず、作られた VFX の性質だけ**を記録する。
/// プラグインを ON/OFF して差分を見れば、見分けが付くかどうかが判定できる。
///
/// ⚠ 記録しかしない。Original を必ず呼び、ゲームの動作には一切干渉しない。
/// </summary>
internal sealed unsafe class VfxProbe : IDisposable
{
    /// <summary>1 件の観測記録。</summary>
    private sealed class Entry
    {
        public string Path = "";
        public string Pool = "";
        public int Count;
        public DateTime First;
        public DateTime Last;
    }

    private Hook<VfxObject.Delegates.Create>? _createHook;
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly object _gate = new();

    public bool IsActive { get; private set; }
    public string Status { get; private set; } = "未使用";
    public int TotalCreated { get; private set; }
    public int DistinctPaths { get { lock (_gate) return _entries.Count; } }

    public bool Enable()
    {
        if (IsActive) return true;
        try
        {
            var addr = VfxObject.Addresses.Create.Value;
            if (addr == 0) { Status = "VfxObject.Create のアドレスが取れません"; return false; }

            _createHook = Plugin.GameInterop.HookFromAddress<VfxObject.Delegates.Create>(addr, CreateDetour);
            _createHook.Enable();
            IsActive = true;
            Status = "観測中";
            Plugin.Log.Info("[VfxProbe] 観測開始");
            return true;
        }
        catch (Exception ex)
        {
            Status = $"フック失敗: {ex.Message}";
            Plugin.Log.Warning($"[VfxProbe] フック失敗: {ex.Message}");
            return false;
        }
    }

    public void Disable()
    {
        if (!IsActive) return;
        IsActive = false;
        try { _createHook?.Disable(); } catch { }
        Status = $"停止 (記録 {TotalCreated} 件)";
        Plugin.Log.Info($"[VfxProbe] 停止: {TotalCreated} 件");
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
        TotalCreated = 0;
        Status = IsActive ? "観測中 (記録を消去しました)" : Status;
    }

    private VfxObject* CreateDetour(CStringPointer path, CStringPointer pool)
    {
        // 何があってもゲームの呼び出しは通す。記録は完全に副作用として扱う。
        var result = _createHook!.Original(path, pool);
        try { Record(path, pool); } catch { }
        return result;
    }

    private void Record(CStringPointer path, CStringPointer pool)
    {
        string p, q;
        try { p = path.ToString() ?? ""; q = pool.ToString() ?? ""; } catch { return; }

        var now = DateTime.Now;
        lock (_gate)
        {
            // 同じ VFX は何度も作られるので、パスごとに件数を畳んでおく。
            var key = p + "" + q;
            if (!_entries.TryGetValue(key, out var e))
            {
                if (_entries.Count >= 512) return;   // 暴走時の保険
                e = new Entry { Path = p, Pool = q, First = now };
                _entries[key] = e;
            }
            e.Count++;
            e.Last = now;
        }
        TotalCreated++;
        Status = $"観測中 ({TotalCreated} 件 / {DistinctPaths} 種)";
    }

    /// <summary>観測結果をファイルへ書き出す。
    /// プラグインを ON にした場合と OFF にした場合で 2 回取り、差分を見るのが使い方。</summary>
    public string Dump(string label)
    {
        var dir = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "diag");
        Directory.CreateDirectory(dir);
        var safe = string.Join("_", label.Split(Path.GetInvalidFileNameChars()));
        var file = Path.Combine(dir, $"vfx_{safe}.txt");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Masked Dalamud VFX 観測  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"# ラベル: {label}");
        sb.AppendLine($"# 状態: {Status}");
        sb.AppendLine();
        sb.AppendLine("件数\tパス\tプール\t初回\t最終");

        lock (_gate)
        {
            foreach (var e in _entries.Values.OrderByDescending(x => x.Count))
                sb.AppendLine($"{e.Count}\t{e.Path}\t{e.Pool}\t{e.First:HH:mm:ss}\t{e.Last:HH:mm:ss}");
        }

        File.WriteAllText(file, sb.ToString());
        Plugin.Log.Info($"[VfxProbe] 観測結果を出力: {file}");
        return file;
    }

    public void Dispose()
    {
        try { Disable(); } catch { }
        try { _createHook?.Dispose(); _createHook = null; } catch { }
    }
}
