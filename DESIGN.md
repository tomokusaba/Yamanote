---
name: WalkLogger
description: 地図を中心に実測・写真・観察を整理するWindowsの街歩き調査室
colors:
  primary: "#147D78"
  ink: "#192C3B"
  muted: "#526574"
  background: "#F4F7FA"
  surface: "#FFFFFF"
  archive: "#EDF2F6"
  line: "#D8E1E7"
typography:
  title:
    fontFamily: "Yu Gothic UI, Segoe UI"
    fontSize: "24px"
    fontWeight: 600
  body:
    fontFamily: "Yu Gothic UI, Segoe UI"
    fontSize: "14px"
    fontWeight: 400
rounded:
  control: "3px"
spacing:
  small: "8px"
  medium: "16px"
  large: "24px"
components:
  button-primary:
    backgroundColor: "{colors.primary}"
    textColor: "{colors.surface}"
    rounded: "{rounded.control}"
    padding: "8px 14px"
---

## Overview

ユーザーが承認した地図中心のWPFワークスペース。帰宅後の明るい室内で記録を整理する場面に合わせた明色の作業面。事実を主役にし、AIは別タブの編集可能な草稿として扱う。

## Colors

実測ルートと主要アクションはteal。過去ルートはmutedの破線。写真地点は茶色の縁。白い作業面、薄い青灰色のアーカイブ、濃いinkのステータスバーを区別する。

## Typography

日本語のWindows標準UI書体。見出し18〜27px、本文14px、補助12px。本文を装飾用の等幅書体に変えない。

## Layout

Windows: 上部に取り込み操作、左258pxに日付順一覧、中央に伸縮する地図、右322pxを基準に写真・メモ。写真ペインはスクロールとスプリッターで調整可能。最小1080×700。ブログ、年次集計、設定はネイティブタブで切り替える。

Android: 同じtealの実測ルートを継承した屋外用記録端末。「記録・記録一覧・設定」のネイティブナビゲーション、地図とGPS状態、開始／休止／終了・撮影操作を優先する。600dp以上では常設ドロワーを使用する。ライト／ダークはOSに追従し、操作は48dp以上、隣接操作は8dp以上離す。文字サイズ拡大とシステムBack、画面端のインセットを尊重する。

## Elevation & Depth

影やグラス効果は使わず、背景色と1pxの境界で作業領域を分ける。

## Shapes

ボタンは3pxの小さな角丸、区画は矩形。地図の点は測定上の意味を持つ丸。

## Components

ボタンは最小36px高。ホバーはaccentの境界、キーボードフォーカスは2pxのink境界、処理中は無効状態と下部のキャンセルを表示。入力は日本語ラベル付き。複数行入力は上揃え。初回は空状態と明示されたサンプルを提示する。

## Do's and Don'ts

- 実測と推測、今回と過去、実記録とサンプルを区別する。
- GPSの欠測を連続ルートとして表示しない。
- 地図の帰属表示を維持する。
- 位置・写真の外部送信確認を取り除かない。
- 設定画面やアーカイブ一覧を装飾的なカード群へ置き換えない。
