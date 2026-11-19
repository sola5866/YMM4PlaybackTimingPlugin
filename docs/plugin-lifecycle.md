# プラグインの起動と終了

YMM4が `YMM4PlaybackTimingPlugin` を生成すると、コンストラクターが再生タイミング調整を初期化します。診断パネルは、同じDLLにある `PlaybackDiagnosticsPlugin` がYMM4のツールとして公開します。パネルを開く操作と再生タイミング調整の起動は独立しています。[YMM4PlaybackTimingPlugin.cs](../src/YMM4PlaybackTimingPlugin/YMM4PlaybackTimingPlugin.cs)・[PlaybackDiagnosticsPlugin.cs](../src/YMM4PlaybackTimingPlugin/Diagnostics/PlaybackDiagnosticsPlugin.cs)

## YMM4での発見と内蔵Harmony

YMM4はプラグインを発見するため、コンストラクターの実行より先にアセンブリ内の型を列挙します。モジュール初期化前に型を列挙できるよう、型のフィールドやコンパイラーが生成する型のインターフェースからHarmonyの即時解決を要求する形を避けています。たとえば `PatchSpec` は `HarmonyPatchType` を整数のフィールドに格納し、Transpilerは命令のリストを組み立てて返します。[PlaybackTimingPatchRegistration.cs](../src/YMM4PlaybackTimingPlugin/Integration/PlaybackTimingPatchRegistration.cs)・[PreviewDiagnosticsHooks.cs](../src/YMM4PlaybackTimingPlugin/Integration/PreviewDiagnosticsHooks.cs)

製品DLLにはHarmony 2.4.2の `0Harmony.dll` を埋め込んでいます。モジュール初期化時に製品と同じ `AssemblyLoadContext` へ依存関係の解決処理を登録し、`0Harmony` の解決が必要になったときにリソースからメモリへ読み込みます。同じ版がそのコンテキストにすでに読み込まれていれば、その実体が使われます。製品のプラグインDLLは単体で配布し、YMM4 SDKのDLLはホストから参照します。表示名は製品DLL内のリソースから読み込み、日本語の環境では日本語名、それ以外では英語名を表示します。[EmbeddedHarmony.cs](../src/YMM4PlaybackTimingPlugin/Composition/EmbeddedHarmony.cs)・[YMM4PlaybackTimingPlugin.csproj](../src/YMM4PlaybackTimingPlugin/YMM4PlaybackTimingPlugin.csproj)

## パッチ登録と有効化

1. `PlaybackTimingStartup` は初回の初期化で `PlaybackTimingRegistrationOwner` を作成します。登録を管理するこのオブジェクトは、コールバックの状態と、自分が登録したパッチを保持します。
2. YMM4内部の型や命令列、既存のHarmonyパッチを検査してから、再生タイミング用のパッチを登録します。その後、診断用フックの登録を試みます。対象と前提は[YMM4への変更箇所](ymm4-integration.md)にあります。
3. 登録後に `Draw` と `Update` のパッチ構成を確認します。両メソッドでこのプラグインの `Postfix` が最後になることなどを確認した時点で構成を記録し、クロック処理を有効化します。

登録後に `Draw` や `Update` のパッチ構成が変わった場合は、確認済みの構成と一致するまで追加待機を行いません。実行中のワーカーやコールバックがない状態で次の `BeginVideoTask()` に入ると、構成の再確認を試みます。再確認に失敗した場合は停止処理に進みます。[PlaybackTimingRegistrationOwner.cs](../src/YMM4PlaybackTimingPlugin/Composition/PlaybackTimingRegistrationOwner.cs)・[PlaybackTimingDrawHooks.cs](../src/YMM4PlaybackTimingPlugin/Integration/PlaybackTimingDrawHooks.cs)

## 接続失敗時の挙動

| 失敗した箇所 | 挙動と確認先 |
| --- | --- |
| 再生タイミング用の事前検査、パッチ登録、登録後の確認 | 再生タイミング調整を有効にせず、すでに登録したパッチの解除を含む終了処理を開始する。起動エラーは `Trace` に出力する。 |
| 診断用の接続またはパッチ登録 | 診断の計測を無効にする。登録中に例外が発生した場合は追加済みの診断パッチを解除する。再生タイミング側の登録と検査が完了すればタイミング調整は動作する。診断の接続エラーはパネルのコピー結果から確認できる。 |

状態の管理は [PlaybackTimingStartup.cs](../src/YMM4PlaybackTimingPlugin/Composition/PlaybackTimingStartup.cs) と [PlaybackTimingRegistrationOwner.cs](../src/YMM4PlaybackTimingPlugin/Composition/PlaybackTimingRegistrationOwner.cs) にあります。

## 終了とパッチの解除

1. `YMM4PlaybackTimingPlugin.DisposeAsync()` から終了を開始し、診断の購読を解除します。新たなタイミング処理の受け付けを止め、候補時刻とクロックを失効させます。
2. 追跡中の映像ワーカーと実行中のコールバックの終了を待ちます。各プレーヤーのセッションは、待機を抜けてから待機タイマーを解放します。
3. この登録処理が追加したパッチを逆順に解除します。Harmonyのパッチ一覧で自分のパッチが消えたことを確認してから、登録時の管理状態を解放します。

ワーカーの異常終了やパッチ解除の失敗などで片付けを確認できない場合は、終了の `Task` が失敗し、登録を管理するオブジェクトを保持します。[PlaybackTimingRegistrationOwner.cs](../src/YMM4PlaybackTimingPlugin/Composition/PlaybackTimingRegistrationOwner.cs)・[PreviewPlaybackSession.cs](../src/YMM4PlaybackTimingPlugin/Playback/PreviewPlaybackSession.cs)

## 起動・終了と配布DLLの試験

[Releaseビルド](README.md#ビルドと梱包)のあと、公開エントリーポイントからの自動起動、二重登録の防止、終了を確認します。この試験を含む `YMM4PlaybackTimingPlugin.Tests` はHarmonyを直接参照します。

```powershell
dotnet run --project tests/YMM4PlaybackTimingPlugin.Tests -c Release --no-build -- --plugin-startup
```

梱包済みのDLLについては、製品とHarmonyへのビルド時参照を持たない専用ホストで、型の列挙、自動起動、終了を確認します。[梱包](README.md#ビルドと梱包)を済ませて、`package/` 内のDLLの絶対パスを指定します。

```powershell
dotnet run --project tests/YMM4PlaybackTimingPlugin.Loading.Tests -c Release --no-build -- embedded '生成package内のYMM4PlaybackTimingPlugin.dllの絶対path' $env:YMM4_DIR
```

ホストの引数は `mode 製品DLL YMM4フォルダー [追加DLL]` です。内蔵Harmonyの解決を確認できるよう、ホストの出力先と製品DLLのフォルダーには `0Harmony.dll` がない状態で実行します。外部Harmonyを渡す試験では、別フォルダーのDLLを指定します。

| モード | 確認する内容 | 追加DLL |
| --- | --- | --- |
| `embedded` | 内蔵Harmonyによる起動と終了 | 不要 |
| `parallel` | 並行して読み込んでも同じHarmonyの実体が使われること | 不要 |
| `external-first` | 同版の外部Harmonyを先に読み込んだ場合の共有 | Harmony 2.4.2のDLL |
| `embedded-first` | 内蔵Harmonyを先に読み込んだ場合の共有 | Harmony 2.4.2のDLL |
| `native-loop` | 内蔵Harmonyを使った再生ループの `warm-long` | ビルド済みの `YMM4PlaybackTimingPlugin.Tests.dll` |

再生ループ試験の環境は[YMM4の再生ループを使った試験](playback-timing.md#ymm4の再生ループを使った試験)、各モードの実装は [YMM4PlaybackTimingPlugin.Loading.Tests の Program.cs](../tests/YMM4PlaybackTimingPlugin.Loading.Tests/Program.cs) にあります。

## YMM4実機での確認

配布前に、YMM4標準インストーラーを使って `YMM4PlaybackTimingPlugin.ymme` の新規導入と既存バージョンからの更新を確認します。YMM4がプラグインを発見して自動起動すること、プレビューの再生・停止とYMM4の終了ができることを確認します。他のプラグインとの併用を対象とする場合は、その組み合わせでも確認します。第三者ライブラリの権利情報は [THIRD-PARTY-NOTICES.txt](../THIRD-PARTY-NOTICES.txt) にあります。
