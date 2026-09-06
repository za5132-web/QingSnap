# QingSnap

[<kbd>简体中文</kbd>](README.md)　[<kbd>English</kbd>](README_EN.md)　[<kbd>繁體中文</kbd>](README_ZH_TW.md)　[<kbd>日本語</kbd>](README_JA.md)　[<kbd>한국어</kbd>](README_KO.md)

> 正確に切り取り、長くつなぎ、デスクトップに残す。OCR は必要なときだけオフラインで追加できます。

QingSnap は Windows 10/11 向けの軽量スクリーンショットツールです。スマート選択、ピクセル単位の調整、編集可能な注釈、スクロールキャプチャ、デスクトップピン、オフライン認識、履歴検索を一つのワークフローにまとめています。

![QingSnap v1.1.1 7ステップ初回ガイド](docs/images/qingsnap-first-run-tutorial.png)

[![Release](https://img.shields.io/github/v/release/za5132-web/QingSnap?label=Release&color=67d9ea)](https://github.com/za5132-web/QingSnap/releases/latest)
![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-1674d1)
![.NET](https://img.shields.io/badge/.NET-8-6f42c1)

## ダウンロード

- [QingSnap v1.1.1 をダウンロード](https://github.com/za5132-web/QingSnap/releases/download/v1.1.1/QingSnap-v1.1.1.zip)
- [v1.1.1 リリースノート](docs/RELEASE_NOTES_v1.1.1.md)
- [全機能一覧（中国語）](docs/QINGSNAP_COMPLETE_FEATURE_LIST.md)

ZIP を展開して `QingSnap.exe` を実行してください。[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) が必要です。リリースは自己署名のため、GitHub Release から取得し SHA-256 を確認してください。

## 1分で分かるワークフロー

初回起動時に7ステップのアニメーションガイドが表示され、設定からいつでも再生できます。

1. `F1` でキャプチャを開始し、ウィンドウを選ぶか自由に範囲指定。
2. X/Y/W/H、比率、固定サイズ、1px 移動で正確に調整。
3. 矢印、図形、文字を追加し、複数選択と Undo/Redo で再編集。
4. 自動スクロールまたは手動フレームで長いページをキャプチャ。
5. `F3` で画像を貼り付け、`M` で画面端に収納。
6. 文書のように画像内テキストを選択し、QR ホットスポットを操作。
7. 文字、タグ、取得元で履歴を検索し、再利用または同じ位置を再キャプチャ。

## 主な機能

- **高精度キャプチャ**：ウィンドウ認識、8方向調整、ピクセル拡大鏡、比率/固定サイズ、`W/A/S/D` 微調整、直近5範囲の呼び出し。
- **編集可能な注釈**：ペン、線、単方向/双方向矢印、図形、文字、番号、モザイク、ハイライト、ぼかし、完全な Undo/Redo と複数選択。
- **長いスクリーンショット**：自動スクロール、重なり照合、固定ヘッダー除去、再試行、手動補完、専用リーダー。
- **デスクトップピン**：直近5画像、外部クリップボード、滑らかな拡大縮小、元位置への復元、`M` 収納とホバー展開。
- **オフライン OCR / QR**：OCR は Tiny/Small を必要時にワンクリック導入。QR は OCR と独立して完全オフライン動作。
- **全履歴検索**：SQLite メタデータ、タグ、取得元、ページング、仮想化、複数選択、一括操作、再キャプチャ。
- **マルチモニター**：Per-Monitor V2、異なる DPI、負の仮想座標に対応。

## v1.1.1

- 初回ガイドを7ページの「1分コアワークフロー」に再構成。
- 全ページにユーザーデータを読み取らない内蔵シミュレーションアニメーションを追加。
- Keycap、アイコン階層、角丸、ヒントを統一し、ピン、QR、`Ctrl+C` のクリッピングを修正。
- Windows の「アニメーションを表示しない」設定に対応し、ページ切替と終了時の解放処理を強化。
- 自動テスト 149/149、Release ビルド 0 Warning / 0 Error。

## ショートカット

| 操作 | キー |
| --- | --- |
| 範囲キャプチャ | `F1` |
| ピン / 最近画像を循環 | `F3` |
| 前回範囲 | `Shift+F1` |
| 直近5範囲を循環 | `R` |
| 選択 / 編集 | `F2` |
| 元に戻す / やり直す | `Ctrl+Z` / `Ctrl+Y` |
| ピンを収納 / 復元 | `M` |
| 確定 / キャンセル | `Enter` / `Esc` |

## プライバシーとビルド

画像、OCR テキスト、QR 内容は第三者へ送信されません。通信はユーザーが開始した OCR ダウンロード、更新確認、Release ダウンロードにのみ使用します。

```powershell
dotnet restore QingSnap.sln
dotnet build QingSnap.sln -c Release
dotnet test tests\QingSnap.Tests\QingSnap.Tests.csproj -c Release
```

問題は [GitHub Issues](https://github.com/za5132-web/QingSnap/issues) へお寄せください。現在オープンソースライセンスは付与されていません。利用、変更、再配布には作者の許可が必要です。

