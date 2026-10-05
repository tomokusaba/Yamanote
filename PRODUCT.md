# WalkLogger

<!-- impeccable:product-schema 1 -->

## Platform

adaptive

Windows desktop (WPF), Android recording terminal (.NET MAUI), and M5Stack CoreS3 firmware.

## Users

街の移り変わりを記録したい個人の街歩き・山手線徒歩ユーザー。

## Product Purpose

CoreS3またはAndroidで位置と写真を記録し、Windowsで地図・旅行記・過去比較・年次レポートとして整理する。

## Operating Context

屋外ではCoreS3のSDカード、またはAndroidのアプリ専用領域に記録。帰宅後にWindowsで整理。OneDriveはWindowsの同期フォルダー、Androidではユーザーが選ぶ共有先として利用する。

## Capabilities and Constraints

- WindowsアプリはWPF。
- AndroidはCoreS3の代わりになるGPS・写真記録端末。5秒／10秒間隔、位置情報フォアグラウンドサービス、GPX／写真入りZIPの共有に対応。AI・過去比較・年次集計はWindowsで行う。OneDriveへの自動同期は行わない。
- CoreS3の内蔵カメラ、外付けUART GPS、SDカードを使用する。GPS型番は未指定なので配線・ボーレートを設定可能にする。
- BLEでログと写真を取り込む。SDカードからの取り込みも用意する。
- 地図はOpenStreetMap。ブログ生成はAzure OpenAI。
- 実測ログ・写真・観察メモから分からない混雑、店舗の開閉、街の変化は作り話として補完しない。
- 実機・Azureリソース・APIキーは未提供。合成サンプルは明確に表示する。

## Product Principles

- 記録はデバイスにも残す。
- 位置情報と写真の外部送信はユーザー操作による。
- AIの草稿は公開前にユーザーが確認する。
- 途切れたGPSを連続した実測ルートに見せない。
