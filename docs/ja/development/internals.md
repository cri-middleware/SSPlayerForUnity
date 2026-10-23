# 🧩 プレイヤーの構成

[← ドキュメント目次へ戻る](../index.md)

このページは、SpriteStudio Player for Unity を開発する人と、自分のプロジェクトのために改造する人に向けたものです。サイトのほかのページより読み手に求めるものが多く、ファイル名を挙げて説明し、チェックアウトがある前提で書いています。ネイティブライブラリのビルドは[ビルドガイド](../setup/build.md)に、制約・セットアップ・テストは [CONTRIBUTING](https://github.com/cri-middleware/SSPlayerForUnity/blob/HEAD/CONTRIBUTING.md) にあります。このページは、それらをまたぐ地図です。

SpriteStudio の Player に共通すること（再生・モーションブレンド・サウンドキー・パーツトラッキングの振る舞い）は、SDK の [Player Porting Guide](https://github.com/cri-middleware/SpriteStudio-SDK/blob/HEAD/libs/ssruntime/docs/README.md) に一度だけ書いてあります。

## 1. 地図

パッケージは `package/` です。薄い C# の層が、csbindgen が生成する FFI バインディングで Rust のランタイム `libssruntime` を動かし、各フレームのジオメトリを Burst のジョブで組み立てて `Mesh` にアップロードします。エディタは `libssconverter` で `.sspj` を変換します。

### 再生（`package/Runtime/`）

| ファイル | 役割 |
|---|---|
| `SpriteStudioPlayer.cs` | ワールド空間用のコンポーネント。`MeshRenderer` で描く |
| `SpriteStudioGraphic.cs` | uGUI の Canvas 用のコンポーネント。`CanvasRenderer` に直接渡す |
| `SpriteStudioPlaybackCore.cs` | 2 つのコンポーネントが持ち、処理を任せる再生ロジック |
| `SpriteStudioUpdateManager.cs` | 毎フレーム、すべてのプレイヤーを 2 段で動かす（全員の Schedule のあと全員の Apply） |
| `SpriteStudioInternalPlayer.cs` | 1 つのプレイヤーのネイティブのコンテキスト、tick、メッシュを組み立てるジョブ。`IDisposable` |
| `SpriteStudioNativeContext.cs` | ネイティブのハンドルの生成と破棄、それが読む `.ssab` のバイト列 |
| `SpriteStudioGeometryBuilder.cs`、`SpriteStudioBatchBuilder.cs`、`SpriteStudioMaskPlanner.cs`、`SpriteStudioEffectPlanBuffer.cs`、`SpriteStudioNativeBuffers.cs` | 1 フレームの描画データを、バッチ・サブメッシュ・マスクの手順・エフェクトの四角形に変える |
| `SpriteStudioMeshJob.cs` | 頂点を書く Burst のジョブ |
| `SpriteStudioMeshUploader.cs` | `Mesh` へのアップロード。インデックスはトポロジーが変わったときだけ送り直す |
| `SpriteStudioMaterial.cs`、`SpriteStudioShaderCatalog.cs`、`SpriteStudioAddOnRegistry.cs` | テクスチャとブレンドごとのマテリアルの種類と、パーツ単位のシェーダー |
| `SpriteStudioAudioController.cs`、`SpriteStudioEvents.cs` | 組み込みのサウンド再生とイベント |
| `SpriteStudioPartTracker.cs`、`SpriteStudioPartOverride.cs`、`SpriteStudioTextureOverride.cs` | パーツへの追従と、パーツ単位・テクスチャの上書き |
| `SpriteStudioAnimeAsset.cs`、`SpriteStudioSequenceAsset.cs` | インポートしたアセット。テクスチャへのハード参照を持つ `.ssab` のバイト列と、`.ssqb` |
| `Timeline/` | Timeline のトラックとクリップ |
| `Bindings/`、`Plugins/` | FFI バインディングとネイティブライブラリ。生成またはダウンロードするもので git には無い（`.meta` だけがある） |
| `Format/`、`FlatBuffers/` | `.ssab` の読み取り（flatc の出力）と FlatBuffers の C# ランタイム。どちらもコミット済み |

### エディタ（`package/Editor/`）

| ファイル | 役割 |
|---|---|
| `SpriteStudioImporterWindow.cs` | **Window > SpriteStudio > Importer** |
| `SpriteStudioImporter.cs`、`SpriteStudioDirectoryScan.cs` | 一括インポートの入口。ドロップされたものの下の `.sspj` を `libssconverter` のスキャンで集める |
| `SpriteStudioConversionRunner.cs` | 変換を実行する。キューに入れ、同時実行は CPU 数まで、`EditorApplication.update` で進み具合を見るので、エディタは描画を続ける |
| `SpriteStudioAssetPostprocessor.cs` | `Assets/` に入った `.sspj` を変換し、テクスチャごとの共有マテリアルを作り、ネイティブプラグインのインポート設定を整える |
| `SpriteStudioSsabImporter.cs`、`SpriteStudioSsqbImporter.cs`、`SpriteStudioAnimeAssetBuilder.cs` | `.ssab` を `SpriteStudioAnimeAsset` に、`.ssqb` を `SpriteStudioSequenceAsset` にする `ScriptedImporter` |
| `SpriteStudioOutputFolder.cs`、`SpriteStudioSourceMap.cs`、`SpriteStudioRecentFiles.cs` | 変換の出力先、各 `.ssab` の変換元の `.sspj`、最近使ったファイル |
| `SpriteStudioReconvertMenu.cs`、`SpriteStudioDragAndDrop.cs` | Project ウィンドウからの Reconvert と、Scene・Hierarchy への `.ssab` のドロップ |
| `SpriteStudioPlayerEditor.cs`、`SpriteStudioGraphicEditor.cs`、`SpriteStudioAnimeAssetEditor.cs`、`SpriteStudioPartTrackerDrawer.cs`、`SpriteStudioAnimationNameField.cs` | インスペクタ |

### 周辺

| パス | 内容 |
|---|---|
| `package/Tests/` | EditMode・PlayMode・プレイヤーのテスト。`scripts/run-tests.sh` が 3 つとも回す |
| `examples/UnityTestProject` | テストを回すプロジェクト。パッケージは `file:../../../package` でつないである |
| `examples/SampleProject`、`examples/Override_Ringo` | サンプル。`deploy-examples` が中身を入れる |

## 2. 変更したプレイヤーを使う

ネイティブライブラリが入っているのはリリースのパッケージだけで、git の URL からパッケージを入れられないのはそのためです（`Plugins/` と `Bindings/` は git にありません）。チェックアウトでは、それをスクリプトで取ってきます。

**C# だけを変えるなら**、Rust は要りません。

1. repo を clone します。
2. `scripts/download-sdk.sh`（Windows は `.ps1`）を実行します。`scripts/SDK_VERSION.txt` の SDK リリースのネイティブライブラリとそのバインディングが `package/` に入ります。
3. 自分のプロジェクトを `package/` に向けます。Package Manager の **＋** → **Install package from disk...** で `package/package.json` を選ぶか、`examples/UnityTestProject` のように `Packages/manifest.json` に `file:` の依存を書きます（`"jp.co.cri-mw.spritestudio.ssplayer-unity": "file:../../../package"`）。

これでプロジェクトはチェックアウトをそのまま使うので、C# の変更は次のコンパイルで反映されます。**ネイティブのコアを変えるなら**ビルドガイドの手順です。`build-runtime` が `download-sdk` の置いたものを置き換えます。読み込むにはエディタの再起動が要ります。

## 3. Unity で確かめる

`scripts/run-tests.sh` は、`examples/UnityTestProject` で、そのプロジェクトが指定するエディタの版を使い、EditMode・PlayMode・スタンドアロンのプレイヤーのビルドをヘッドレスで回します。先にエディタを閉じてください。開いている間は batchmode がプロジェクトのロックを取れません。スイートの前に、テスト用のデータを SDK のサブモジュールから変換します。

- **プロジェクトを直接開くと、このマシンに無いネイティブプラグインの `.meta` が消えます。** エラーではなく削除なので、`git status` でしか分かりません。`run-tests.sh` は自分で戻しますが、自分でプロジェクトを開いたあとは `git checkout -- package/Runtime/Plugins package/Editor/Plugins` で戻してください。
- **エディタは一度読み込んだネイティブプラグインを読み直しません。** `build-runtime` や `download-sdk` のあとは再起動してください。しないと、直したのに直っていないように見えます。
- **ネイティブプラグインの読み込みは、Play Mode ではなくスタンドアロンのプレイヤーで確かめます。** エディタはプレイヤーよりプラグインを緩く解決します。macOS ではランタイムは `ssruntime.bundle` という名前のフォルダでなければならず、`libssruntime.bundle` はエディタでは読めてもプレイヤーで `DllNotFoundException: ssruntime` になります。それを捕まえるのがプレイヤーのスイートです。
- macOS 27 では Unity 6000.3.16f1 が起動しません（batchmode は何もせずに exit 0 で終わります）。6000.3.25f1 は起動します。

### Unity の表記の引き方

ガイドでは、エディタのメニューを Unity の表示どおりに書きます。文字列は `Unity.app/Contents/Resources/Scripting/Managed/UnityEngine/UnityEditor.CoreModule.dll` に UTF-16 で入っているので、UTF-16 のバイト列で探してください（Package Manager の **Install package from disk...**・**Install package from tarball...**・**Install package from git URL...** はそこにあります）。uGUI の **GameObject > UI** メニューは、プロジェクトの `Library/PackageCache/com.unity.ugui@*/` の `Editor/UGUI/UI/MenuOptions.cs` です。日本語のページでも、エディタの英語の表記を使います。

## 4. この作りになっている理由

ここに挙げる理由はどれも複数のファイルにまたがります。詳細は、それぞれが指すコードにあります。

**2 つのコンポーネントで、コアは 1 つです。** `SpriteStudioPlayer` は `MonoBehaviour`、`SpriteStudioGraphic` は uGUI の `MaskableGraphic` で、基底クラスが違うため継承では再生を共有できません。そこでどちらも `SpriteStudioPlaybackCore` を持って処理を任せます。シリアライズする設定は、Unity がシリアライズするコンポーネントの側に残しています。

**すべてのプレイヤーを 2 段で動かします。** `SpriteStudioUpdateManager` は、まず全プレイヤーの仕事（ランタイムのフレームデータとメッシュのジョブ。並列に走ります）をスケジュールし、そのあとで結果をメッシュに反映します。各プレイヤーをそれぞれの `Update` で動かすと、ジョブを 1 つずつ待つことになります。マネージャーはプレイヤーの一覧のスナップショットをたどるので、途中でプレイヤーが作られたり破棄されたりしても壊れません。

**Canvas 用のコンポーネントは Canvas の再構築を通りません。** `SpriteStudioGraphic` は `OnPopulateMesh` と頂点・マテリアルの dirty フラグを上書きし、メッシュを自分で `CanvasRenderer` に渡すので、Unity が毎フレームメッシュを破棄して確保し直すことはありません。Play Mode ではアップデートマネージャーが動かし、Edit Mode では自分の `LateUpdate` でプレビューします。

**ネイティブのハンドルは `IDisposable` のクラスが持ち、ジョブを先に終わらせます。** ランタイムのポインタを持つのは `SpriteStudioNativeContext` と `SpriteStudioInternalPlayer` です。何かを解放する前に `JobHandle.Complete()` を呼びます。解放したメモリを Burst のジョブが読み続けると、プロセスが落ちるためです。

**変換でエディタを止めません。** ドロップしたフォルダのスキャンは別スレッドで走り（`SpriteStudioDirectoryScan`）、変換は CPU 数を上限とするキューから `EditorApplication.update` で進めて、進み具合をステータスバーに出します（`SpriteStudioConversionRunner`）。`.sspj` はディスクのどこに置いてもよく、各 `.ssab` の変換元は `ProjectSettings/SpriteStudioSourceMap.json` が記録します。Reconvert が読むのはこれです。出力先と最近使ったファイルは利用者ごと（`UserSettings/`）です。

**インポートした `.ssab` は、テクスチャへのハード参照を持ちます。** `SpriteStudioAnimeAsset` は `.ssab` のバイト列を持ち、それが名指すすべてのテクスチャを参照するので、ビルドがアニメの描くものを取り除くことはなく、AssetBundle にも要るものが入ります。テクスチャの解決は、インポーターが `SpriteStudioAnimeAssetBuilder` で行います。

**SDK のサブモジュールが、この repo がどのランタイムでビルドされるかの記録です。** `Plugins/` と `Bindings/` は git に無いので、チェックアウトがどの `libssruntime` で動くはずかを示すのは、サブモジュールのポインタだけです。ランタイムの挙動に関わる変更は同じプルリクエストでサブモジュールを上げます。そうすれば、ブランチの記録どおりにビルドしたレビュアーが、プルリクエストの主張どおりの結果を得られます。
