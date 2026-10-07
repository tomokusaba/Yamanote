# WalkLogger — 街歩きアーカイブ

M5Stack CoreS3またはAndroidでGPS・写真を記録し、WindowsのWPFアプリで整理する個人用アーカイブです。

## できること

- CoreS3: UART GPSを5秒または10秒間隔でSDへ保存。内蔵カメラでJPEG撮影し、UTC時刻・座標を記録。
- Android: .NET MAUIの記録端末。スマホのGPSとカメラで記録し、GPXまたは写真入りZIPを共有。
- Windows: BLE転送／SDフォルダー／GPXから取り込み。OpenStreetMap + Leafletでルートと撮影地点を表示。
- 写真と観察メモを編集。スマホなどのJPEGも撮影日時を確認してGPSへ紐付け。
- GPX、要約、写真、ブログ草稿を保存。OneDriveの同期フォルダーも保存先に選択可能。
- Azure OpenAIで事実に基づく日本語Markdownの草稿を生成。過去の記録の数値・メモも比較可能。
- 年次集計（回数・実測距離・写真枚数・ユーザー確認済み一周の最短経過時間）。

**実機とAzureの実リソースはこのプロジェクト作成時点で未接続です。** コンパイル・合成サンプルでのWPF起動／地図描画・自動回帰シナリオは確認できますが、GPS受信、カメラ、PCのBLEアダプターとの相性、実際のAzureデプロイでの応答はお手元の機器で確認してください。

## Windowsアプリの起動

ビルド済みの場合は `dist\windows\WalkLogger.App.exe` を起動してください。この配布フォルダーは丸ごと保持します。.NETランタイム同梱のWindows x64版です。

必要な環境:

- Windows 10 2004以降／Windows 11（Windows 11推奨）。
- Microsoft Edge WebView2 Evergreen Runtime。未導入の場合、[Microsoftの配布ページ](https://developer.microsoft.com/microsoft-edge/webview2/)から導入。
- BLE転送にはBluetooth LE対応アダプターと有効なBluetooth設定。
- 地図・地名・AIの利用にはインターネット。取り込み・実測値集計・GPX保存はオフラインでも可能。

ソースから起動する場合は.NET SDK 10.0.401以降の10.0.xを用意します。`global.json`で安定版10.0.401を基準にしています。

```powershell
dotnet build WalkLogger.slnx
dotnet run --project src\WalkLogger.App\WalkLogger.App.csproj
```

初回は「サンプルで試す」で画面を確認できます。サンプルは合成座標であり、実際の道に沿った歩行記録ではありません。年次集計から除外します。

## Androidを記録端末にする

CoreS3なしで、スマホのGPSとカメラを使用できます。Android 8.0以降が対象です。配布APKは`dist\android`、ソースは`src\WalkLogger.Android`です。[使い方・ビルド・継続記録の制約](src/WalkLogger.Android/README.md)を参照してください。Android版は記録用で、地名取得・AI草稿・過去比較・年次集計はWindows版で行います。

「記録一覧」から写真入りZIPをOneDrive等へ共有し、Windowsで展開後「フォルダー取込」を選びます。OneDriveの自動同期・Microsoftアカウント連携は行いません。

## GPSの接続とファームウェア

対応前提: **CoreS3**（Core2／初代Core／CoreS3 SEではありません）、**M5Stack Unit GPS v1.1（U032-V11）**、FAT32のmicroSDカード。別のNMEA対応UART GPSも、配線と通信速度を合わせれば使用できます。

既定のUART配線:

| GPS側 | CoreS3側 |
|---|---|
| TX | GPIO18（CoreS3のRX） |
| RX（受信のみなら省略可能） | GPIO17（CoreS3のTX） |
| GND | GND |
| 電源 | GPSユニットの仕様に従う |

**信号は3.3V UARTを使用してください。5V信号をESP32へ直接入力しないでください。** 電源電圧と信号電圧は別です。Groveの色やピン順だけで接続せず、ユニットの仕様書を確認してください。GPS既定ボーレートは**115200、8N1**です。[Unit GPS v1.1公式仕様](https://docs.m5stack.com/en/unit/Unit_GPS_v1.1)では、AT6668／ATGM336H-6N、115200 baud、NMEA0183 4.1、電源5Vとされています。旧Unit GPS（U032）は9600 baudで、v1.1とは異なります。v1.1を9600で受信すると、RXが増えても文字化けして`BAD NMEA`のままになることがあります。

`firmware\platformio.ini`で次を変更できます:

```ini
-DGPS_RX_PIN=18
-DGPS_TX_PIN=17
-DGPS_BAUD=115200
-DLOG_INTERVAL_SECONDS=5
```

記録間隔は5または10秒。SDのSPI設定はCoreS3内蔵スロットに合わせています。

旧Unit GPSなど9600 baudのセンサーを使う場合だけ`GPS_BAUD=9600`へ変更してください。ファームウェアはGPSユニット自体の設定を書き換えず、指定した速度で受信します。

ボードは`m5stack-cores3`、PSRAMはCoreS3のQuad方式に合わせた`board_build.arduino.memory_type = qio_qspi`を使用します。`qio_opi`（Octal方式）では、このCoreS3実機でカメラ画像バッファを確保できず、`frame buffer malloc failed`になりました。Quad方式へ変更して書き込み、実機の`Camera: Ready`表示を確認しています。

[PlatformIO](https://platformio.org/)を導入してビルド・書き込み:

```powershell
python -m platformio run --project-dir firmware
python -m platformio run --project-dir firmware --target upload --upload-port COM5
python -m platformio device monitor --baud 115200 --port COM5
```

`COM5`は接続したCoreS3のポートに置き換えます。既存ファームウェアは書き換わるため、必要ならバックアップしてください。自動的な書き込みは行いません。

接続できない場合は、RSTボタンを約3秒押し、緑のLEDが点いたら離してダウンロードモードにします。POWERボタンの長押しとは異なります。[CoreS3公式の操作説明](https://docs.m5stack.com/en/core/CoreS3)を参照してください。

起動時にPSRAMの認識・総容量・空き容量・最大連続領域をシリアルへ出力し、カメラのフレーム取得を確認します。画面の`Camera: Ready`は初期化と起動時のフレーム取得が成功した状態です。`PSRAM unavailable`はPSRAM未認識、`Init failed`はカメラ初期化失敗、`Capture failed`はフレーム取得失敗です。カメラの状態はGPS記録メッセージで上書きされません。USBシリアル接続の待機は最大3秒で、PC未接続でも起動します。GPS付きJPEGのSD保存は、屋外で記録を開始してPHOTOを押し、別途確認してください。

### 屋外GPS試験の診断ログ

GPS診断版では、SDカードが使える場合、起動ごとに`/diagnostics/boot-000001`などの新しいフォルダーを作成します。**GPS未捕捉・PAUSE中でも、STARTを押さずに診断ログを保存します。** PC接続は不要ですが、このファームウェアへの更新は必要です。

| ファイル | 内容 |
|---|---|
| `status.jsonl` | 起動時、5秒ごと、GPS状態変化時、ボタン操作時のJSONログ |
| `uart.nmea` | UARTから受信した生バイト。正常なNMEAだけでなく、チェックサム不一致や文字化けもそのまま保存 |

`uart.nmea`は受信データがある場合に作成します。受信0バイトでこのファイルが存在しない場合でも、`status.jsonl`の`rx_bytes=0`から無受信の状態を確認できます。

状態ログには起動後のミリ秒、リセット理由、RX/TXピン・ボーレート、GPS待ち理由、受信バイト数・最終受信からの時間、チェックサム成功／失敗数、UARTエラー数、位置・UTCの日付／時刻の有効性と古さ、衛星数・HDOP、記録モード、保存済みポイント数・写真数、歩行フォルダー、カメラ・メモリ・バッテリー状態、最後のエラーを残します。タッチ回数（`touch_presses`）、操作名（`last_control`）、操作時刻、画面座標と生座標も記録します。`control`イベントはSTART／PAUSE／PHOTO／NEW／BLEの全操作で、拒否された操作も残します。ボタン外のタッチは`touch`イベントです。`touch_enabled`はライブラリの有効状態で、コントローラーとの通信成功を保証する値ではありません。`last_lat`／`last_lon`は最後に取得した位置であり、`fix_ready=false`の場合は現在有効な位置とは限りません。NMEAのUTC情報がない間も`uptime_ms`で経過を追えます。

画面の見方:

| 表示 | 意味・確認点 |
|---|---|
| `NO UART`、RXが0 | GPSからまだ1バイトも届いていません。電源、TX→GPIO18、GND、Unit GPS v1.1では115200 baudを確認 |
| RXが増加、`BAD NMEA` | データは届いていますが有効なNMEAをまだ認識できません。ボーレート、文字化け、生ログを確認 |
| NMEA OKが増加、`NO FIX` | GPSとの通信はできています。空が開けた場所で衛星捕捉を待つ |
| `UART STALE`／`FIX STALE` | 通信や位置更新が2秒以上止まっています |
| `UTC ... WAIT`／`UTC ... STALE` | 日付・時刻が未取得、古い、または日付が2024年より前 |
| `SAT WAIT`／`SAT LOW`／`HDOP WAIT`／`HDOP HIGH` | 衛星数または精度が記録条件に届いていません（4衛星以上、HDOP 5以下） |
| 緑の`FIX READY` | 現在の記録条件を満たしています。STARTで歩行記録を開始 |
| `SD:OK DIAG:OK` | 診断ログの保存が有効。`Logs:`に保存先を表示 |
| `DIAG:ERR` | 診断ログを保存できません。画面の赤いエラーを確認。GPSを受信できていても保存成功とは異なります |
| `Touch:` | 押下回数、最後の操作名と座標。押して回数が増えればタッチを検出できています |

生ログは4096バイトのバッファで保存し、状態ログの出力時にもフラッシュします。UART受信バッファは2048バイトに設定し、バッファ溢れなどのUARTエラーと保存できなかった生バイト数も記録します。電源断では最後の未保存データ（通常5秒程度。SDやカメラ処理中は延びる場合があります）やFAT更新が失われる可能性があるため、試験後はPAUSEして数秒待ってから電源を切り、起動中にSDカードを抜かないでください。ログは自動削除せず、GPS未捕捉でも受信データに応じて増えます。115200 baudの生ログは通信速度上限で約41.5MB／時（9600 baudでは約3.5MB／時）、通常のNMEA出力はそれより少なく、状態ログは通常約1MB／時が目安です。SDの空き容量を確保してください。

帰宅後、SDカードの`diagnostics`フォルダーをPCへコピーしてください。診断ファイルは歩行用NDJSONとは分離しており、Windowsアプリの歩行取り込みやBLEの歩行転送には含めません。ログには座標・時刻が含まれるため、公開・共有前に位置情報を確認してください。ファームウェアのコンパイル時にはGPS状態判定の16ケースとタッチボタン境界の14ケースを`static_assert`で確認します。SDへの実保存・GPS実受信は屋外試験で確認してください。

### CoreS3の操作

画面下部のタッチボタンを使用します。

ボタン操作後は定期更新の3秒を待たずに画面を更新します。操作結果はボタン上の専用領域に大きく表示し、開始できない場合は`Cannot start: BAD NMEA`などの理由を赤字で表示します。開始成功時だけSTARTがPAUSEに変わります。GPS未捕捉・通信不良でも、診断ログの自動保存は続きます。

| 操作 | 動作 |
|---|---|
| START | GPSのUTC・位置が有効なとき記録開始／再開 |
| PAUSE | 記録を休止。再開は別の実測区間として記録 |
| PHOTO | 記録中に内蔵カメラで撮影。新しいGPS fixが必要 |
| NEW | 休止中・BLE OFFのとき、新しい街歩きへ切り替え |
| BLE ON | 休止中に転送を有効化。30分後に自動終了 |
| BLE OFF | 転送を終了。記録再開前にオフにする |

屋外でGPSを捕捉してください。衛星数4以上、HDOP 5以下、位置／UTCの更新が2秒以内という条件で保存します。欠測時には古い位置を繰り返して保存しません。時刻はUTCで、画面とWindowsの表示時に現地時刻へ変換します。

SDには次の形式で保存します:

```text
walks\
  20261005T022300Z\
    track.ndjson
    photos.ndjson
    IMG_000001.jpg
```

`track.ndjson`の1行:

```json
{"time":"2026-10-05T02:23:00Z","lat":35.681236,"lon":139.767125,"speed":5.4,"altitude":12.3,"segment":1}
```

`speed`はkm/h、`altitude`はメートル（任意）。`segment`は休止・再開の区間番号。`photos.ndjson`には同じ形式の日時・座標と`image`を記録します。GPSロガー内ではGPXを直接書かず、Windows側で検証して生成します。電源断でGPX全体が閉じなくなることを避けるためです。

再起動後は新しい記録になります。最後の途中書き込み行があるログは、原本を残したうえでその行を修復／削除してください。取り込み時に行番号を示して拒否し、黙って欠損を無視しません。

## BLE転送

1. CoreS3でPAUSE → BLE ON。
2. Windowsアプリの「BLEで取り込む」→「機器を検索」。
3. `WalkLogger-CoreS3`を選び「選択した機器から転送」。
4. ログ・写真を受信し、各ファイルのサイズとCRC32を確認してアーカイブへ取り込み。
5. 転送後、CoreS3でBLE OFF。

転送はSD内の全記録が対象です。記録中には転送しません。SD原本を削除しません。受信済みの同一ファイルはCRCが一致すれば省略し、同じ記録の再取り込みは重複追加せず、Windows側のメモを維持します。

途中キャンセル／接続断の場合、ローカルの`.part`を残します。再検索して転送するとそのバイト位置から再開し、最後に全体のCRCを検証します。内容が変わっていて検証できなければ破損した受信部分を破棄し、再転送を求めます。自動削除や無限リトライはしません。

BLEは写真の大量転送には遅い場合があります。30分で転送窓が閉じたら再びBLE ONにして続行します。1ファイル32MB、一覧4096ファイル／512KBを上限としています。多い場合はSD取り込みを使用してください。

**この初期版のBLEは暗号化／ボンディングを実装していません。CRCは破損検出であり、相手認証ではありません。** 転送を自分で有効化する方式ですが、近くの第三者からの接続を防ぐものではありません。信頼できる場所でのみ有効化し、機密性が必要な記録はBLE OFFのままSDで取り込んでください。公衆の場での常時広告はしません。

### 転送プロトコル v1

| 用途 | UUID |
|---|---|
| Service | `af736e10-70c0-4d31-a213-8d82b0047100` |
| Command（Write With Response） | `af736e11-70c0-4d31-a213-8d82b0047100` |
| Metadata（Read） | `af736e12-70c0-4d31-a213-8d82b0047100` |
| Data（Read） | `af736e13-70c0-4d31-a213-8d82b0047100` |

ASCIIコマンド: `CAT`（NDJSON一覧）、`GET <id>`（一覧のファイル）、`READ <offset>`（絶対バイト位置）。Metadataは`ok, version, size, crc32`、失敗時は`error`。Dataは先頭4バイトにlittle-endianのoffset、その後に最大180バイトの本文。MTU23なら本文16バイト。CRC32は標準のIEEE、`123456789`のチェック値は`CBF43926`。`READ`のMetadataの`crc32`は利用せず、`CAT/GET`時のチェック値で全体を検証します。

## OneDrive・Azureの設定

「設定」タブから設定します。

- **保存先**: OneDriveの同期対象フォルダー内を選択。ファイルを書き込む方式であり、Microsoft Graphへ直接アップロードしません。同期状態はOneDriveで確認します。
- **AzureリソースURL**: `https://<resource>.openai.azure.com/`または`https://<resource>.services.ai.azure.com/`。APIパスを含めないでください。
- **デプロイ名**: Azureで作成したチャットモデルのデプロイ名。例として`gpt-4.1`をデプロイした場合でも、ここに入力するのは実際のデプロイ名です。
- **APIキー**: ローカルのWindowsユーザー単位でDPAPI暗号化。クラウド同期先へは保存しません。
- **地名サービスの連絡先**: NominatimへのUser-Agentに含めるメールアドレス。

Azure OpenAIのv1 `chat/completions` APIと`max_completion_tokens`を使用します。写真を送る場合は画像入力対応モデルをデプロイしてください。ネットワーク制限されたリソースではPCから接続できる構成が必要です。APIキー認証が無効な組織では、この初期版の接続方式は使えません（Entra ID認証は未実装）。

生成時に、送信内容と料金発生の可能性を確認します。写真送信は既定オフで、今回の写真のみ最大8枚／各4MBまで。過去との比較は過去の数値・地名・観察メモ・写真メモを使用し、過去の画像自体は送信しません。

ブログ生成時、今回の記録や選択した比較記録の地名が未取得なら、Nominatimへの送信確認を追加で表示します。承認すると最大12代表地点／記録の近傍地名を取得・保存し、記録順の地名と対応座標をAzureへ渡します。取得済みの地名は再利用し、地点キャッシュ・1.1秒以上の問い合わせ間隔も維持します。「設定」の連絡先メールアドレスを事前に保存してください。どちらかの送信確認を拒否した場合は何も送信せず、地名取得・保存が失敗した場合もAzure生成へ進みません。地名取得後にキャンセルした場合、保存済みの地名は残ります。

記事では取得した町名・地域名を使って、出発側・途中・到着側の流れを紹介するよう指示します。AIが座標だけから地名を想像する方式ではありません。代表地点の近傍地名なので、正確な道路経路・施設への立ち寄り・サンプルの実歩行を保証しません。

AIが「店舗が閉店した」「人が多かった」などをGPSから創作しないように指示します。ただし、生成モデルの誤りを完全には防げないため、公開前に必ず草稿を確認します。WordPressなどへの自動公開はしません。Markdownを書き出して利用してください。

草稿は固定の調査レポートではなく、短い導入・自然な段落・必要な小見出し・振り返りを持つブログ向けの文章を指示します。距離や時間は本文へ織り込み、資料のない比較や「写真がありません」などの列挙は省きます。AIへ渡す日時はこのPCのローカル日時（UTCオフセット付き）、時間の長さは秒まで保持します。合成サンプルは記事の見本として扱い、実際に歩いた旅行記にはしません。モデルの設定名やAPIパラメーターは変更せず、GPT-6 Lunaを含む設定済みのデプロイで使用できます。文章の長さ・文体はモデルの応答に依存します。

## 集計とプライバシー

- 地図と距離は、同一区間・時刻が前進・間隔60秒以下・点間速度12m/s以下の部分のみ連続として扱います。
- 距離はHaversineで算出した実測点間の距離。GPSノイズの影響を受けます。道路への自動吸着は行いません。
- 経過時間は最初から最後のGPS時刻まで（休止・欠測を含む）。移動時間は点間速度0.5m/s以上の実測区間からの推定です。
- 一周完了はユーザーが確認します。山手線を回ったかの自動判定はしません。
- 新規発見スポット数／店舗の開閉数をGPSから勝手に集計しません。観察メモとして残せます。
- 自宅付近などの自動マスキングは未実装。公開用GPX・草稿・写真に位置情報が残ることに注意してください。
- 地図表示ではOSMにタイル要求、地名取得ではNominatimへ代表座標、AI生成ではAzureへ要約と選択した写真を送ります。ログ全点のAI送信はしません。

Nominatimは最大12代表地点、問い合わせ間隔1.1秒以上、ローカルキャッシュを使用します。駅名や施設名の通過を保証するものではなく、近傍地名の参考情報です。公共サービスのため大量利用には独自サーバーなどを検討してください。地図の帰属表示は消さないでください。

既定の保存先:

```text
%LOCALAPPDATA%\WalkLogger\
  settings.json
  azure-key.dpapi
  diagnostics.log
  places-cache.json
  Imports\<device-address>\...
  Archive\walks\<record-id>\
    walk.json
    route.gpx
    summary.txt
    blog.md
    IMG_....jpg
```

`walk.json`が正本です。草稿やメモを編集するとGPX・要約・Markdownも更新します。保存先変更時、旧保存先のデータは自動移動しません。フォルダーごとバックアップしてください。

## 開発・確認

```powershell
dotnet run --project tests\WalkLogger.Tests\WalkLogger.Tests.csproj
dotnet build WalkLogger.slnx
python -m platformio run --project-dir firmware
dotnet publish src\WalkLogger.App\WalkLogger.App.csproj -c Release -r win-x64 --self-contained true -o dist\windows
```

WPFの合成データ起動確認（Azureや実機は使用しません。地図タイル要求は発生します）:

```powershell
dotnet run --project src\WalkLogger.App\WalkLogger.App.csproj -- --smoke --smoke-output=C:\temp\walklogger-smoke.json
```

出力先の親フォルダーを先に作成してください。JSON、地図PNG、WPF画面PNGを出力して終了します。WebView2はWPFのビットマップ描画に含まれないため、地図は別のPNGで確認します。`--smoke-small`を追加すると最小ウィンドウサイズも確認できます。作成した専用の一時アーカイブはJSONの`root`に記録します。

コアの回帰シナリオにはGPX/UTC/距離/欠測/重複取り込み/破損入力/パス検証/BLEのCRC・中断再開・キャンセル/Azureの要求形状と失敗応答を含みます。テスト用HTTP応答であり、Azureの実リソース接続を検証するものではありません。

### Windowsアプリ全体のラインカバレッジ

Windowsの対話デスクトップ、PowerShell 7、WebView2 Runtimeが必要です。リポジトリのルートから実行します。

```powershell
dotnet tool restore
pwsh -NoProfile -File tests\Collect-Coverage.ps1
```

固定バージョンの`dotnet-coverage`で、通常の回帰シナリオ、WPF・Windows APIのテスト、Host起動／停止／破棄の失敗テスト、実際のWPF合成データ起動をまとめて計測します。WPFダイアログはテストプロセスが自動操作して閉じます。ヘッドレスのWindowsサービスではなく、ログイン済みデスクトップで実行してください。

Domain・Application・Presentation・Infrastructure・Infrastructure.Windows・AppのC#全体が対象です。除外は`obj`内と`*.g.cs`の自動生成ソースのみで、WinRT BLEやWPF本体は除外しません。Coreの型転送は実行行を持たないメタデータです。CoreS3のC++は対象外です。閾値はCoberturaの`lines-covered / lines-valid`による全体ラインカバレッジ90%です。対象プロジェクトの欠落や90%未満でコマンドが失敗します。各ファイルの表は同一ソース行を重複除去しているため、クラスごとの行を集計するCobertura全体値とは分母が異なることがあります。

レポートは`artifacts\coverage\coverage.cobertura.xml`、ファイル別の未実行行は`coverage.cobertura.summary.json`に出力します。既存レポートの再確認は次のコマンドで行えます。

```powershell
pwsh -NoProfile -File tests\Check-Coverage.ps1 -Report artifacts\coverage\coverage.cobertura.xml
```

設定・DPAPIキー・診断ログは専用の一時フォルダーで検証し、ユーザーの実設定やキーを変更しません。既定の設定保存先と公開APIは維持しています。WebView2の子プロセスが終了してファイルロックを解放してから、一時フォルダーを削除します。計測ツールのテレメトリーは無効化しています。AzureとNominatimのHTTP応答は偽物ですが、地図の起動確認ではOpenStreetMapへのタイル要求が発生します。

90%は全体目標であり、すべてのファイルが90%以上という意味ではありません。BLEのCRC・一覧パス検証・転送ユースケースは偽ポートで検証しますが、実際のWinRT接続・GATT送受信は実機未検証です。BLE実装の未実行行も分母に残して報告します。

### 実機で最初に確認すること

1. SDカードとGPS配線を確認して屋外でGPS FIXを待つ。
2. START → 数分歩く → PHOTO → PAUSE。
3. SDのログ・JPEGをPCで取り込めることを確認。
4. BLEで同じ記録を取り込み、重複しないことを確認。
5. 転送を途中キャンセルして再開できることを確認。
6. Azureを設定し、メモのみの草稿から試す。写真対応モデルの場合は写真1枚で試す。

## 構成・ライセンス

- `firmware`: PlatformIO / Arduino / M5CoreS3 / TinyGPSPlus / ArduinoJson。
- `src\WalkLogger.Domain`: GPS点・写真・記録モデルと距離・時間のルール。プロジェクト参照なし。
- `src\WalkLogger.Application`: 内向きのインターフェース、取り込み・編集・BLE・写真・地名・ブログ・設定のユースケース、BLE転送検証、年次レポート。Domainだけを参照。
- `src\WalkLogger.Presentation`: WPF非依存のViewModel、コマンド、画面状態、ダイアログ・診断ポート。Applicationだけを参照。
- `src\WalkLogger.Infrastructure`: ファイル保存、GPX・JSON、Azure・NominatimのApplicationインターフェース実装。
- `src\WalkLogger.Infrastructure.Windows`: WinRT BLE、DPAPI設定保存、JPEGのEXIF読み取り。
- `src\WalkLogger.Core`: 旧公開型の型転送を残す互換アセンブリ。新しいWPFコードは参照しません。
- `src\WalkLogger.App`: .NET 10 WPF / Generic Host / WebView2。
- `tests\WalkLogger.Tests`: 外部テストパッケージ不要の実行型回帰シナリオ。
- `tests\WalkLogger.Windows.Tests`: STA/Dispatcher上のWPF、DPAPI、EXIF、ダイアログ、終了・Host失敗シナリオ。
- `samples`: 明示された合成データ。

Leaflet 1.9.4はローカルに同梱し、[BSD-2-Clauseライセンス](src/WalkLogger.App/Map/leaflet/LICENSE)を保持しています。その他のライブラリーのライセンスは各依存パッケージに従ってください。OpenStreetMapは[帰属・利用規約](https://www.openstreetmap.org/copyright)、[タイル利用ポリシー](https://operations.osmfoundation.org/policies/tiles/)、Nominatimは[利用ポリシー](https://operations.osmfoundation.org/policies/nominatim/)に従います。

### WPFとGeneric Hostのライフサイクル

`Program.cs`の`[STAThread] Main`を起動エントリに指定しています。`Program.CreateHost`でGeneric Hostを作成し、ApplicationのインターフェースにInfrastructure実装を登録します。WPFのStartupイベントで作成したHostを`App.StartHostAsync`へ渡し、Hostの開始完了後にDIから`MainWindow`を取得して表示します。`StartupUri`は使用しません。Composition Rootは`Program`、WPFとの開始・終了連携は`App`です。画面はViewModelへ操作を渡し、具体的なHTTP・保存・BLE実装を生成しません。HTTPクライアントの破棄はHostが管理します。診断アダプターは`ILogger<MainWindow>`と従来のローカル診断ログを使用します。

終了時は従来どおり記録を保存してからウィンドウを閉じ、その後Hostの`StopAsync`を待ち、Hostを破棄してWPFを終了します。`ShutdownMode=OnExplicitShutdown`により、非同期の停止処理が完了するまでWPFのDispatcherを維持します。ConsoleLifetimeの代わりにWPF用の`IHostLifetime`を登録しています。

HostのContentRootは実行ファイルのフォルダーです。アーカイブ設定とAPIキーの保存形式・場所は変更していません。`--smoke`では確認専用のHostedServiceを登録し、出力JSONの`hostStarted`・`hostStopped`・`hostDisposed`で開始・非同期停止・破棄を確認できます。

### 依存方向

```text
WPF App（UI / Program.csのGeneric Host Composition Root）
  → Presentation（ViewModel / Commands / UIポート）→ Application → Domain
  → Application → Domain
  → Infrastructure.Windows → Infrastructure → Application → Domain
```

Domain、Application、PresentationからWPF、Windows API、保存・通信の具体実装への参照はありません。通常起動時の組み立ては`Program`に集約しています。既存の公開`MainWindow`コンストラクターも、互換性用ファクトリーで同じ構成へ委譲する形で維持しています。`PhotoTimeWindow`はWPF側の日時表示・入力、実際のEXIF読み取りはWindows側のアダプターにあります。

`MainWindow.xaml.cs`はDataContextの設定、選択・PasswordBox・終了イベントとWPFコンポーネントの橋渡しのみを担当します。画面状態・編集内容・確認・コマンド・キャンセルは`MainWindowViewModel`、BLE取得・写真の60秒以内のGPS紐付け・記録編集・地名と草稿の取得保存・保存先変更・サンプル取り込みはApplicationのユースケースが担当します。`RouteMapPresenter`はWebView2と地図メッセージ、`WpfWorkspaceDialogs`はOSダイアログ、`WorkspaceSmokeRunner`は起動確認と画像出力を担当します。入力値はバインディングで逐次反映し、編集中のまま閉じても最新の内容を保存します。

既存の型の完全修飾名は互換性のため維持しています。歴史的な`WalkLogger.Core`名前空間が使われていても、所属アセンブリとプロジェクト参照で層を分離しています。JSONの必須項目・日時変換・計算プロパティの除外はInfrastructureのシリアライズ契約で設定し、DomainにはJSON属性を付けません。アプリの保存JSON・GPX・設定・BLEプロトコルは維持しています。

アーキテクチャの回帰シナリオでプロジェクト参照、DomainとPresentationの独立性、型転送、画面の具体実装非依存、XAMLコマンドと編集バインディングを確認します。WPFなしのFakeポートで、写真の60秒境界・BLEの順序と解放・保存してからの選択切り替え・保存失敗・キャンセル・外部送信の確認・草稿の保存失敗・保存先切り替えを確認します。
