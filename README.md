# Masked Dalamud

OBS / Discord などの配信キャプチャから、**プラグインの UI だけ**を隠す FFXIV (Dalamud) プラグインです。
ゲーム画面は今までどおり配信に映り、手元では UI が見えて操作もできます。

## 仕組み

ImGui を描く**前**の backbuffer を保存しておき、描いた**後**と比べて、
変化したピクセルを「プラグインの UI」とみなします。

```
Before: backbuffer を保存          ← ここが「ゲーム画面」の基準
  ↓  Dalamud が UI を描く
After:  差分 = UI。backbuffer からは消し、手元用のオーバーレイへ回す
```

手元側の表示方法は 3 つあり、環境に応じて選びます。**同時に 1 つだけ**有効になります。

| 方式 | 中身 | 向いている環境 |
|---|---|---|
| DComp | DirectComposition + Composition Swapchain | 既定。最も軽い |
| GPU-GDI | レイヤード窓 + UpdateLayeredWindow | AMD / ReShade など DComp が動かない環境 |
| CPU | CPU で読み戻して合成 | 最終手段。FPS が 10〜50 落ちる |

いずれもキャプチャ除外には `WDA_EXCLUDEFROMCAPTURE` を使います。

## ネイティブ UI を描くプラグインへの対応

ゲームの UI レイヤーへ直接描くプラグインは、上記の差分では隠せません
(ImGui を通らないため、保存した「ゲーム画面」に最初から入ってしまう)。

これらは対象ノードのアルファを 0 にしてゲーム側から消し、
同じ絵を手元のオーバーレイへ描き直すことで対応しています (`KamiMirror.cs`)。

- **JobBars** … KamiToolKit 製のゲージ・アイコン・数字
- **BossModReborn** … `Enable projecting radar into the 3D world` の 3D 投影レーダー

## 構造的に隠せないもの

- **Splatoon の VFX** … ゲームの VFX システムに乗るため、UI として分離できません
- **排他フルスクリーン** … Windows の制限で手元 UI が出ません (配信非表示は維持)

## ビルド

`EstellUtils` を同じ階層に置いてください (UI ライブラリとして参照しています)。

```
TempRepos/
├── MaskedDalamud/
└── EstellUtils/
```

```
dotnet build -c Release
```

`bin/Release/MaskedDalamud/latest.zip` が配布物です。

## 配布

[PrivateReleaseRepo](https://github.com/rioriopu/PrivateReleaseRepo) から配信しています。
