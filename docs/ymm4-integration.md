# YMM4への変更箇所

プラグインは、YMM4が読み込んだメソッドに実行時にHarmonyパッチを適用します。`Prefix` はメソッドの実行前、`Postfix` は実行後、`Finalizer` は例外を含む終了時に呼ばれます。`Transpiler` はメソッド本体のIL（中間言語）に処理を挿入します。

## 再生タイミングのパッチ

型名は表では短く記載しています。`TimelineVideoPlayer` と `TimelineAudioPlayer` は `YukkuriMovieMaker.Player`、`TimelineSource` は `YukkuriMovieMaker.Player.Video`、音声バックエンドの `AudioPlayer` は `YukkuriMovieMaker.Player.Audio`、`YMMSettings` は `YukkuriMovieMaker.Settings` に属します。`TimelineVideoPlayer` は `TimelineAudioPlayer` を継承します。タイムライン側の位置や再生状態への介入は、プレビュー用の映像プレーヤーのインスタンスが対象です。

| YMM4の対象 | パッチと挿入位置 | 加わる処理 | 実装ファイル |
| --- | --- | --- | --- |
| `TimelineVideoPlayer.BeginVideoTask()` | `Prefix`、`Transpiler`、`Finalizer`。本体では `videoTask` に代入する直前を観測 | YMM4が開始したワーカーの `Task` を記録する。診断パネルが検索するプレーヤーの候補にも追加する。 | `PlaybackTimingDrawHooks.cs` |
| `TimelineSource.Update(TimeSpan, TimelineSourceUsage)` | `Prefix`、`Postfix`、`Finalizer` | 呼び出し時の候補時刻をソース単位で記録し、通常再生のワーカーから呼ばれた元の処理が成功したことを確認する。 | `PlaybackTimingDrawHooks.cs` |
| `TimelineVideoPlayer.Draw()` | `Prefix`、`Transpiler`、`Postfix`、`Finalizer`。本体では最初の `DrawImage` の直前と `EndDraw()` の直後に処理を挿入 | `Update` の候補と描画結果を照合し、条件を満たす場合に `Finalizer` で[フレームの期限まで待機](playback-timing.md#描画が終わったあとの待機)する。描画変換と描画結果は診断でも使う。 | `PlaybackTimingDrawHooks.cs` |
| `TimelineAudioPlayer.Position` の取得・設定 | 両アクセサーの `Postfix` | 取得時に補間した位置を返し、設定時に音声サンプルまたはYMM4の位置からクロックの基準を更新する。 | `PlaybackTimingClockHooks.cs` |
| `TimelineAudioPlayer.IsPlaying` と `PlaybackRate` の設定 | 両セッターの `Prefix` と `Postfix` | 再生開始・停止をセッションに反映する。速度変更前の位置を確保し、変更後のクロック補正に使う。 | `PlaybackTimingClockHooks.cs` |
| `TimelineAudioPlayer.EndAudioTask()` | `Prefix` と `Postfix` | 停止要求を記録して古いフレーム待機を無効にし、返された `Task` の完了時に音声位置の引き継ぎを確認する。 | `PlaybackTimingClockHooks.Audio.cs` |
| `AudioPlayer.CurrentPosition` の取得 | `Prefix` と `Finalizer` | YMM4とプラグインの音声位置読み取りを同じバックエンドについて直列化する。 | `PlaybackTimingClockHooks.Audio.cs` |
| `AudioPlayer.Stop()` と `AudioPlayer.Dispose(bool)` | `Prefix` | 一時停止の対象となるバックエンドで、停止直前の音声位置を確保する。 | `PlaybackTimingClockHooks.Audio.cs` |
| `TimelineAudioPlayer.PlaybackRate`、`AudioPlayer.PlaybackRate`、`AudioPlayer.PlaybackRateMode`、`YMMSettings.AudioPlayerPlaybackRateMode` の取得 | 各ゲッターの `Postfix` | YMM4の音声位置更新ループが要求値とバックエンドの値を比較する箇所を観測し、バックエンドの切り替えに備えて現在位置を保持する。 | `PlaybackTimingClockHooks.Audio.cs` |
| `TimelineVideoPlayer.Dispose(bool)` と `TimelineSource.Dispose(bool)` | `Prefix` と `Finalizer` | 管理対象を破棄する呼び出しで、新しい待機を受け付けなくし、対応する候補やセッションを失効させる。 | `PlaybackTimingDrawHooks.cs` |

パッチの登録は [PlaybackTimingPatchRegistration.cs](../src/YMM4PlaybackTimingPlugin/Integration/PlaybackTimingPatchRegistration.cs) にあります。各パッチの処理は [PlaybackTimingDrawHooks.cs](../src/YMM4PlaybackTimingPlugin/Integration/PlaybackTimingDrawHooks.cs)、[PlaybackTimingClockHooks.cs](../src/YMM4PlaybackTimingPlugin/Integration/PlaybackTimingClockHooks.cs)、[PlaybackTimingClockHooks.Audio.cs](../src/YMM4PlaybackTimingPlugin/Integration/PlaybackTimingClockHooks.Audio.cs) にあります。

## 診断のパッチ

| YMM4の対象 | パッチと挿入位置 | 加わる処理 |
| --- | --- | --- |
| `TimelineVideoPlayer.Draw()` | `Prefix` と `Finalizer`。描画結果は再生タイミング用の `Transpiler` と共有 | 計測対象の描画を開始時に捕捉し、`EndDraw()` が成功して例外なく終了したものを集計する。 |
| `TimelineVideoPlayer.Present()` | `Prefix`、`Transpiler`、`Finalizer`。本体ではDXGIの `IDXGISwapChain.Present` 呼び出しの直前・直後に処理を挿入 | 呼び出し結果の `HRESULT` を記録する。DXGI呼び出しが例外で終わった場合も失敗として扱う。 |
| `TimelineAudioPlayer.IsPlaying` の設定 | `Postfix` | 計測モデルに再生状態を反映する。 |
| `TimelineVideoPlayer.SeekAsync(TimeSpan)` と `TimelineAudioPlayer.PlaybackRate` の設定 | `Prefix` | シークや速度変更の前に計測区間を区切る。 |

パッチの登録と処理本体は [PreviewDiagnosticsHooks.cs](../src/YMM4PlaybackTimingPlugin/Integration/PreviewDiagnosticsHooks.cs)、結果の意味は[プレビュー再生診断](preview-diagnostics.md#計測値の定義)にあります。

## YMM4更新時に確認する前提

- **再生メソッドと状態** — 型の継承関係、メソッドのシグネチャ、`Position` などのアクセサーが触るフィールド、音声停止時のバックエンド解放と `IsPlaying = false` の順序、音声位置更新ループの速度・モード比較を検査します。[PreviewNativePlaybackContract.cs](../src/YMM4PlaybackTimingPlugin/Integration/PreviewNativePlaybackContract.cs)
- **映像ワーカーと待機位置** — `BeginVideoTask` の `Task.Run` から実際のワーカーを特定し、候補時刻が `Update` に渡り、`Draw` のあとにYMM4の待機判定と `Present` が続くことを確認します。`Update` のロックが `finally` で解放され、ワーカーの呼び出し経路がプラグインの待機をまたいでロックを保持しない構造も検査します。[PreviewNativeVideoLoopContract.cs](../src/YMM4PlaybackTimingPlugin/Integration/PreviewNativeVideoLoopContract.cs)・[PreviewNativePacingContract.cs](../src/YMM4PlaybackTimingPlugin/Integration/PreviewNativePacingContract.cs)
- **他のHarmonyパッチとの併用** — クロックやワーカー本体などに外部パッチがある場合、再生タイミング調整の接続を拒みます。`Draw` や `Update` などの共有箇所では、挿入対象の命令列を検査し、`Draw` と `Update` ではパッチの並びと実行時の状態も照合します。[PlaybackTimingPatchDependencies.cs](../src/YMM4PlaybackTimingPlugin/Integration/PlaybackTimingPatchDependencies.cs)・[PlaybackTimingDrawHooks.cs](../src/YMM4PlaybackTimingPlugin/Integration/PlaybackTimingDrawHooks.cs)
- **診断の接続** — `Present()` と `SeekAsync(TimeSpan)` の存在、プレビューのウィンドウを取得する内部フィールド、`Present()` 内のDXGI呼び出しが1か所であることを確認します。[PreviewDiagnosticsConnection.cs](../src/YMM4PlaybackTimingPlugin/Diagnostics/PreviewDiagnosticsConnection.cs)・[PreviewDiagnosticsHooks.cs](../src/YMM4PlaybackTimingPlugin/Integration/PreviewDiagnosticsHooks.cs)
