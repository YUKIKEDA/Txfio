# Txfio

Windows（NTFS / SMB ファイルサーバー）向けの、git のステージング / コミットに着想を得たトランザクショナルなファイル IO ライブラリです。

**現状**: 公開 API の実装は未着手です。設計とリポジトリ基盤のみです。GitHub は Phase 3 完了まで private です。nuget.org への公開も同じタイミングです。

## 対象

- TFM: `net8.0`（net8 以降のアプリから参照可）
- ランタイム保証: Windows（NTFS / SMB）
- クラッシュリカバリ可能なジャーナル。一時フォルダは使わない

詳細は [`docs/design.md`](docs/design.md) です。

## ドキュメント

| 文書 | 役割 |
| --- | --- |
| [`docs/design.md`](docs/design.md) | 設計の正本 |
| [`docs/roadmap.md`](docs/roadmap.md) | 実装順（Phase は仮） |
| [`docs/conventions.md`](docs/conventions.md) | コーディング規約の詳細 |
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | 進め方（人間向け正本） |
| [`AGENTS.md`](AGENTS.md) | エージェント入口 |
| [`SECURITY.md`](SECURITY.md) | 脆弱性の報告 |

## ローカル検証

正本ゲートは Windows 上の `./build.ps1` です。GitHub Actions は置くが、利用制限中は必須ゲートにしません。

```powershell
./build.ps1
```

Linux ではこのスクリプトを実行しないでください。restore / format / build までとし、テスト成功をマージ条件に使わないでください。

## ライセンス

[MIT](LICENSE)
