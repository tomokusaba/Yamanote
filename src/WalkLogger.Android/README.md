# Android記録端末

CoreS3の代わりに、AndroidのGPSとカメラで街歩きを記録します。Windows版のブログ生成・過去比較・年次集計をスマホへ移植するものではありません。

## 使い方

Android 8.0（API 26）以降、GPS搭載端末が対象です。

1. アプリの「設定」で5秒／10秒間隔を選び、屋外で「記録開始」を押します。位置情報と通知を許可してください。
2. GPSを取得してから点数・精度・実測距離が表示されます。画面消灯／別アプリ使用中も、位置情報フォアグラウンドサービスの通知を表示して継続します。
3. 「写真を撮る」でカメラを起動します。撮影日時はEXIFを優先し、不明なら写真返却時刻の推定とメモに明示します。前後60秒以内で最も近いGPS点に紐付けます。撮影前に任意の観察メモを入力できます。
4. 「一時停止」「記録を再開」で区間を分割します。休止やGPS欠測を直線で結んだ距離に加算しません。
5. 「記録を終了」後、記録一覧で選択し「写真入りZIPを共有」または「GPXを共有」を押します。位置情報の共有確認後、OneDrive等のAndroid共有先を選びます。
6. WindowsでZIPを展開し、WPF版の「フォルダー取込」で展開先を選びます。`track.ndjson`、`photos.ndjson`、JPEGがCoreS3と互換の形式です。GPX単体には写真や写真メモを含みません。

GPSを受信できない間は座標を捏造せず、点を保存しません。実測距離／経過時間はWindowsと同じGPS区間の計算です。写真の位置は最寄りの記録点であり、シャッター瞬間の補間位置ではありません。

## 保存・継続記録

記録はアプリ専用領域の`Recordings\<記録ID>`に保存します。各GPS点はNDJSONへ追記してディスクへフラッシュします。撮影画像を保存してから写真メタデータを追記します。プロセス再生成時は保存済みデータを復元し、区間を分割します。途中書き込み／不足写真などの破損はエラーとして表示し、勝手にデータを捨てません。

Androidの強制停止・端末再起動・メーカー独自の省電力制限では記録が中断します。通知を伴うサービスも絶対的な継続保証ではありません。アプリを開き、状態を確認して再開してください。長時間のGPS・CPU稼働は電池を消費します。アプリを削除・データ消去すると記録も消えるため、ZIPでバックアップしてください。

位置情報と写真を自動アップロードしません。Android版のOneDrive連携は共有シートへのZIP/GPX送出であり、Graph API／クラウド自動同期はありません。地図の表示範囲はOpenStreetMapのHTTPSタイルサーバーへ送信します。地図タイルがなくても記録／GPX／ZIPは利用できます。LeafletはWindows版と同じ同梱の1.9.4を使用します。

## ビルド

`.NET SDK 10.0.401`、`maui-android`ワークロード、Android SDK（API 36）、JDK 21が必要です。Visual Studioの.NET MAUI Android開発環境でも構いません。

```powershell
dotnet workload restore src\WalkLogger.Android\WalkLogger.Android.csproj
dotnet build WalkLogger.Android.slnx
dotnet publish src\WalkLogger.Android\WalkLogger.Android.csproj -c Release -o dist\android
dotnet run --project tests\WalkLogger.Recording.Tests\WalkLogger.Recording.Tests.csproj
pwsh -NoProfile -File tests\Collect-RecordingCoverage.ps1
```

SDK/JDKが自動検出されない場合は`-p:AndroidSdkDirectory="..." -p:JavaSdkDirectory="..."`を追加してください。Windowsだけのソリューションは従来の`WalkLogger.slnx`です。Windowsカバレッジの対象と90%ゲートは変更しません。記録コアは`WalkLogger.Recording`、ファイル保存／GPX・ZIPは`WalkLogger.Recording.Infrastructure`、Android固有のGPS・カメラ・共有とMAUI表示は`WalkLogger.Android`に分離しています。

既存の反射ベースのJSON契約を安全に共有するため、Android版はトリミングとAOTを無効にしています。APKサイズよりもデータ互換性を優先した構成です。

Release版では`AndroidEnableMarshalMethods=false`を指定し、Javaから.NETへの呼び出しは従来の動的登録を使用します。ネイティブ呼び出し最適化を有効にしたAPKでは、AQUOS R11（Android 16）で`MauiApplication.n_onCreate`の`UnsatisfiedLinkError`が発生したためです。インストール成功だけでなく、配布するRelease APKを実機で起動して確認してください。

1.0.1はAQUOS R11（A602SH、Android 16）に`adb install -r`で更新インストールし、記録画面の表示を確認済みです。ファイル管理アプリから1.0をインストールした際の拒否理由は未特定です。実機でのGPS記録・撮影・長時間継続の確認は、このインストール／起動確認とは別です。

配布時は`*-Signed.apk`を使用してください。既定の開発署名は個人利用用です。公開配布には自身の署名鍵が必要です。署名鍵やパスワードをソースに保存しないでください。

実端末で、GPS測位、カメラ、画面消灯中の長時間継続、メーカーの省電力動作を確認してください。自動テストでは記録周期、休止・再開／復元、8時間相当の点数、異常データ、ZIP→Windows取り込みを検証しますが、実GPS・カメラ・OSの省電力制限を代替しません。

記録コアとファイル保存／エクスポートのカバレッジは別の90%ゲートで計測します。Android固有のサービス・権限・カメラ・MAUI画面をこの数字に含めず、実機で未検証の機能を計測済みとは扱いません。初回のカバレッジ収集前には`dotnet tool restore`を実行してください。
