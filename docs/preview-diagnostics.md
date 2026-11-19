# プレビュー再生診断

YMM4の「ツール」から「プレビュー再生診断」を開きます。パネルにはプラグインとYMM4のバージョン、再生タイミング調整の有効状態、シーン、プロジェクトの解像度とFPS、プレビュー領域、モニター構成が表示されます。「計測」は初期状態ではオフです。

## 計測対象と操作

パネルは開いているタイムラインと同じ `Timeline` を使うプレーヤーを探します。候補は、YMM4の `BeginVideoTask()` で見つけたプレーヤーのうち、編集中でプレビューのホストが表示され、破棄が始まっていないものです。該当するプレーヤーが1つの場合に計測対象にします。対象が切り替わると新しい計測モデルを作成します。[PreviewDiagnosticsService.cs](../src/YMM4PlaybackTimingPlugin/Diagnostics/PreviewDiagnosticsService.cs)・[PlaybackDiagnosticsViewModel.cs](../src/YMM4PlaybackTimingPlugin/Diagnostics/PlaybackDiagnosticsViewModel.cs)

| 操作 | 動作 |
| --- | --- |
| 計測をオン | 対象プレーヤーを購読し、再生中の描画・提示を集計する。 |
| 計測をオフ | 購読を解除し、同じ対象の最終値を表示する。再開すると新しい時間窓で計測し、累計は引き継ぐ。 |
| リセット | 累計、計測時間、直近の集計、最後の送信エラーを消去する。 |
| パネルを閉じる | 計測と画面更新を停止し、映像領域の観測要求も取り下げる。 |
| 結果をコピー | 表示値に加え、採取時刻、モニターのデバイスパス、表示情報や接続の取得エラー、最後の送信エラーをテキストに含める。 |

## 計測値の定義

| 表示値 | 集計の対象 |
| --- | --- |
| 描画FPS・描画の累計フレーム数 | Direct2Dの `EndDraw()` が成功し、YMM4の `Draw()` が例外なく終了した回数。FPSは直近1秒の件数。 |
| 送信FPS・送信の累計フレーム数 | DXGIの `IDXGISwapChain.Present` が `S_OK` を返した回数。FPSは直近1秒の件数。画面が遮蔽されたことを示すステータスなどは、この集計から外れる。 |
| 最大送信間隔 | 同じ計測区間にある連続した `S_OK` 同士の時間差のうち、後の送信が直近5秒に含まれるものの最大値。 |
| 送信失敗数 | `Present` の負の `HRESULT`、またはDXGI呼び出しに入ったあと結果の記録前に発生した例外の件数。 |
| 計測時間 | 計測がオンで、対象プレーヤーが再生中だった時間の合計。 |

送信FPSで分かるのは、DXGIが受理した提示要求の頻度です。モニターの項目に表示する Hz はWindowsが報告するリフレッシュレートです。モニターに実際に表示されたフレーム数を測るには別途計測が必要です。

計測をオンにしている間の累計は再生中だけ加算します。再生と停止の切り替え、シーク、速度変更、描画ソースの交換で区間を区切り、FPSと最大送信間隔の履歴を作り直します。描画と送信の開始時に区間番号を取得し、完了時も同じ区間に属する場合に限って集計します。このため、計測停止やリセットより前に開始した処理があとから完了しても累計に入りません。新しい区間のFPSは1秒経過してから表示します。時間窓のバッファが上限に達すると、その区間のFPSと最大送信間隔は欠測扱いになります。[PreviewMeasurements.cs](../src/YMM4PlaybackTimingPlugin/Diagnostics/PreviewMeasurements.cs)・[PreviewDiagnosticsHooks.cs](../src/YMM4PlaybackTimingPlugin/Integration/PreviewDiagnosticsHooks.cs)

## プレビュー領域とモニター情報

**プレビュー全体のサイズ**は、YMM4のプレビュー用ウィンドウハンドルからクライアント領域を物理ピクセル単位で取得します。**映像部分のサイズ**は、描画スレッドで取得した `DrawImage` 直前の変換とDPIを使って映像の四隅を変換し、描画バッファとクライアント領域の比で換算して領域内に収めた値です。ほかのウィンドウが手前に重なっても算出値は同じです。パネルは描画スレッドが記録した値を読みます。[PreviewGeometry.cs](../src/YMM4PlaybackTimingPlugin/Diagnostics/PreviewGeometry.cs)

プレビューが複数のモニターにまたがる場合は、最も大きく重なるモニターをWindows APIで選びます。複製表示などで表示先を一意に特定できない場合は、その状態をパネルに表示します。色形式とビット深度はWindowsが報告する値で、SDR・SDR ACM・HDRの分類には新しい表示構成APIを使います。旧APIしか使えない場合は、取得できた色形式とビット深度を表示し、モードは「取得できません」と表示します。[WindowsPreviewDisplay.cs](../src/YMM4PlaybackTimingPlugin/Diagnostics/WindowsPreviewDisplay.cs)
