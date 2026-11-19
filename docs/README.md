# YMM4再生タイミング調整プラグイン 開発者向けドキュメント

YMM4再生タイミング調整プラグインは、タイムラインのプレビュー再生を対象に、音声位置に基づく再生クロックを補間し、フレームの描画が早く終わったときに提示前の待機を追加します。YMM4が行うフレーム選択、描画、提示の流れにHarmonyで処理を追加します。導入後は自動で有効になります。動作確認済みのYMM4は `4.57.0.0` です。

「ツール」の「プレビュー再生診断」では、プロジェクト、プレビュー領域、モニターの情報を確認できます。「計測」をオンにすると、再生中に完了した描画や提示を集計します。

| 調べたいこと | 文書 |
| --- | --- |
| YMM4のどのメソッドに何を追加しているか | [YMM4への変更箇所](ymm4-integration.md) |
| 音声位置からフレームの待機期限をどう決めるか | [プレビュー再生のタイミング](playback-timing.md) |
| 診断パネルの数値は何を測っているか | [プレビュー再生診断](preview-diagnostics.md) |
| プラグインがいつ有効になり、どの順で解放されるか | [プラグインの起動と終了](plugin-lifecycle.md) |
| ビルドして配布ファイルを作るにはどうするか | [ビルドと梱包](#ビルドと梱包) |
| 変更後にどの試験を実行するか | [自動試験の選び方](#自動試験の選び方) |

## ソースコードの構成

| ディレクトリ | 役割 |
| --- | --- |
| [Composition](../src/YMM4PlaybackTimingPlugin/Composition/) | 内蔵Harmonyの読み込み、パッチ登録、終了処理を担当します。 |
| [Integration](../src/YMM4PlaybackTimingPlugin/Integration/) | YMM4内部の型、メソッド、命令列を検査し、再生と診断の処理を差し込みます。 |
| [Playback](../src/YMM4PlaybackTimingPlugin/Playback/) | プレーヤーごとのクロック、フレーム候補、待機期限を管理します。 |
| [Diagnostics](../src/YMM4PlaybackTimingPlugin/Diagnostics/) | 対象プレビューの特定、計測値の集計、表示情報の取得、診断パネルを担当します。 |

## ビルドと梱包

Windows、.NET 10 SDK、PowerShell 7、.NET 10版のYMM4を用意します。`YMM4_DIR` に `YukkuriMovieMaker.dll` があるフォルダーの絶対パスを指定し、リポジトリのルートで実行します。試験を実行する場合はまずReleaseビルドを行い、梱包は配布ファイルや梱包済みDLLが必要なときに実行します。

```powershell
$env:YMM4_DIR = 'YMM4の絶対path'
dotnet build YMM4PlaybackTimingPlugin.slnx -c Release
pwsh -NoProfile -File tools/create-ymme.ps1 -OutputRoot 'artifacts/release-check'
```

梱包の出力先には新しいフォルダーを指定します。配布ファイルは出力先の `YMM4PlaybackTimingPlugin.ymme` です。中間フォルダー `package/` とアーカイブ直下には、`YMM4PlaybackTimingPlugin.dll`、`README.md`、`LICENSE`、`THIRD-PARTY-NOTICES.txt` が入ります。同梱する `README.md` の内容は [assets/README.md](../assets/README.md) で編集します。

Lib.Harmonyを更新するときは、埋め込むDLLに含まれる依存コードを調べ、リポジトリ直下の [THIRD-PARTY-NOTICES.txt](../THIRD-PARTY-NOTICES.txt) と照合します。ビルド出力の通知がこのファイルと一致すると梱包できます。

YMM4への導入と動作確認は、梱包後に[YMM4実機での確認](plugin-lifecycle.md#ymm4実機での確認)の手順で行います。

## GitHub Actionsでのリリース

[Build and release](../.github/workflows/release.yml) は、公式のYMM4 Lite 4.57.0.0のZIPを取得し、固定したSHA-256と照合してからビルドに使います。Releaseビルド、基本の自動試験、梱包、`.ymme` 内のDLLの読み込み試験を実行します。

- ワークフローを既定ブランチに置くと、Actions画面から手動実行できます。検証済みの `.ymme` は実行結果のArtifactsから取得できます。
- プロジェクトの `Version` と一致するタグ（例 `v1.0.0`）をpushすると、検証済みの `.ymme` を添付した下書きリリースを作成します。[YMM4実機での確認](plugin-lifecycle.md#ymm4実機での確認)を済ませてから、GitHubのReleases画面で公開します。

## 自動試験の選び方

各試験プロジェクトはコンソールアプリです。Releaseビルド後、リポジトリのルートで基本の試験を実行し、変更箇所に対応する試験を追加します。

```powershell
dotnet run --project tests/YMM4PlaybackTimingPlugin.Tests -c Release --no-build
```

| 変更箇所 | 追加で確認する項目 |
| --- | --- |
| 再生クロック、描画後の待機、YMM4再生ループとの接続 | [YMM4の再生ループを使った試験](playback-timing.md#ymm4の再生ループを使った試験) |
| 診断パネル、描画や送信の計測、表示情報の取得 | [計測とUIの試験](preview-diagnostics.md#計測とuiの試験) |
| プラグインの発見、起動・終了、内蔵Harmonyや梱包済みDLL | [起動・終了と配布DLLの試験](plugin-lifecycle.md#起動終了と配布dllの試験) |
