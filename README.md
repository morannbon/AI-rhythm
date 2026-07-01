# AI-rhythm v1.0.2

## プラグイン名

AI-rhythm

## 概要

AI-rhythmはTvAIrの番組表情報をもとにおすすめ候補と推薦理由を表示するTvAIr用プラグインです。

ニックネーム、優先キーワード、除外キーワードなどの軽量設定に対応しています。

## インストール方法

Visual Studio 2022で以下のソリューションを開きます。

```text
AIrhythm.BasicPlugin.sln
```

構成をRelease x64にしてビルドします。
ビルド後、以下のDLLをTvAIrのPluginsフォルダへコピーします。

```text
_bin\AIrhythm.BasicPlugin\Release\net8.0-windows\AIrhythm.BasicPlugin.dll
```

TvAIr側の配置先は以下です。

```text
TvAIr\Plugins\AIrhythm.BasicPlugin.dll
```

TvAIrを起動し、メニューからAI-rhythmを開きます。

## アンインストール方法

TvAIrを終了してから以下のファイルを削除します。

```text
TvAIr\Plugins\AIrhythm.BasicPlugin.dll
```

ニックネームなどの設定も削除する場合は以下のファイルも削除します。

```text
TvAIr\Plugins\AIrhythm.BasicPlugin.ini
```

## ビルド時の出力パス

Visual Studio 2022でRelease x64ビルドを行った場合、主な出力ファイルは以下に生成されます。

```text
_bin\AIrhythm.BasicPlugin\Release\net8.0-windows\AIrhythm.BasicPlugin.dll
```

## TvAIrのプラグインパス

TvAIr本体フォルダを基準に以下へ配置します。

```text
TvAIr\Plugins\AIrhythm.BasicPlugin.dll
```

AI-rhythmの設定ファイルは設定保存時に以下へ作成されます。

```text
TvAIr\Plugins\AIrhythm.BasicPlugin.ini
```

## バージョン

v1.0.2

## リリース日

2026年7月1日 バージョン1.0.2リリース

## リリースノート

### v1.0.2

TvAIr本体同梱のTvAIrPlugin契約に合わせ、AI-rhythm側SDK参照とmanifest生成を調整しました。
README.md、README.txt、画面表示、DLLメタデータのバージョン表記をv1.0.2に統一しました。
インストール手順はDLLのみ必須の構成を維持しています。

## ライセンス

AI-rhythm本体はMIT Licenseです。
詳細は同梱のLICENSEを確認してください。

同梱されるTvAIrプラグインSDK関連ファイルはTvAIr側の配布条件およびライセンスに従います。
