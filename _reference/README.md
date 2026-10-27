# _reference（参考専用・AGPL）

Pleasanter 本体（GNU AGPL v3）は **AI の事実確認専用**。Git では追跡せず、読む前に必要時取得する。
日次追従・ポインタ更新 PR は作らず、CI でも取得しない。
この README だけを追跡し、本体は `_reference/Implem.Pleasanter/` の ignore 対象とする。

- 取得元: <https://github.com/Implem/Implem.Pleasanter>
- 取得 branch: `main`（リリース前の変更を含む）
- 本アプリのデュアルライセンス（AGPL-3.0-or-later OR 商用）は変更しない。
  本体のコードを取り込まないことが商用提供の前提（[`LICENSING.md`](../LICENSING.md)）

## 厳守事項

- 本体コードをコピー・流用・リンク・プロジェクト参照しない。標準 Web API を叩く別アプリとして実装する
- API 仕様・サイト設定のスキーマ・列定義・挙動の事実確認だけに使い、取得先を編集しない
- ソリューション・ビルド・配布物へ含めない。共有取得先からローカルへのリンクも作らない
- 実際の API 応答・日時・テナント設定・権限は実機で確認する。未検証ならその旨を残す

## 取得

```bash
# 読む前に毎回実行する（上流 main の先頭へ fast-forward 更新）
bash scripts/sync-reference.sh

# 任意: 他の作業ツリーと取得先を共有する場合（リンクは作らず、この場所を直接読む）
export PLEASANTER_REFERENCE_DIR='D:\references\Implem.Pleasanter'
bash scripts/sync-reference.sh
bash scripts/reference-isolation-check.sh
```

共有指定が無ければ `_reference/Implem.Pleasanter` へ取得する。
成功時は取得先・SHA・コミット日・状態・版がコンソールへ出る。再実行時は同じ取得先を更新する。
取得先のローカル変更や異なる取得元などはエラーとなり、破棄して成功扱いにはしない。
旧 submodule の取得済みソースや未コミット変更は移行時に削除しない。
既存取得先で同期が失敗した場合も変更を破棄せず、別の専用取得先を指定する。

CalendarFeed は一覧なしの本体 1 件を使う。スクリプトが備える `references.json` /
`--manifest` / `--root` は複数参照向けで、一覧使用時は `PLEASANTER_REFERENCE_DIR` を使わない。
このアプリには一覧を追加しない。

## CalendarFeed の調査先

従来の調査で使った本体内のパスを残す。現行 `main` で同じ仕様かどうかは、取得後のソースと実機で再確認する。
今回の移行では、以下の API 仕様を新しい版で検証したことにはしない。

| 対象 | 調査する内容 |
| --- | --- |
| `Implem.Pleasanter/Controllers/Api/ItemsController.cs` | 標準 API のエンドポイントとルーティング |
| `Implem.Pleasanter/Models/Results/ResultUtilities.cs`（`Get` の API 版） | `Response.Offset` / `PageSize` / `TotalCount` / `Data` とページ上限 |
| `Implem.Pleasanter/Models/Issues/IssueApiModel.cs` | 記録テーブルの `StartTime` / `CompletionTime` など |
| `Implem.Pleasanter/Libraries/Requests/Context.cs` | リクエストボディの `ApiKey` の受け渡し |

## 確認した版の記録

```bash
# 共有指定時は、以下のパスを実際の取得先に置き換える
git -C _reference/Implem.Pleasanter rev-parse HEAD
git -C _reference/Implem.Pleasanter log -1 --format='%H %ad' --date=short
```

文書には **確認した完全なコミット SHA・ソースへの固定リンク・参照日** を残す。
固定リンクは `https://github.com/Implem/Implem.Pleasanter/blob/<完全なSHA>/<確認したファイル>` とする。
版は取得ソースの `Implem.Pleasanter/Implem.Pleasanter.csproj` の `<Version>` を確認する。
取得しただけで実機検証済みとは扱わず、過去の実機検証記録の版を自動で書き換えない。

## 移行の根拠

承認済み Template コミット `3312f003fbe9827090e20bc643800824bdf30e22` の
[配布手順][distribution]（2026-10-27 参照）と
vehiclevisionjp/VehicleVision.PleasanterTools.Template#61・
vehiclevisionjp/VehicleVision.PleasanterTools.Template#63 に基づく。
実際の本体ソースの挙動・API 仕様を今回新たに検証したという意味ではない。

[distribution]: https://github.com/vehiclevisionjp/VehicleVision.PleasanterTools.Template/blob/3312f003fbe9827090e20bc643800824bdf30e22/_documents/参照ソースのオンデマンド取得への配布手順.md
