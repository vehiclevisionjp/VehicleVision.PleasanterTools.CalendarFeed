# _reference（参考専用・AGPL）

`Implem.Pleasanter` は Pleasanter 本体（**GNU AGPL v3**）を **参考目的でのみ** サブモジュールとして
配置したもの。固定コミットで参照する。

- リポジトリ: <https://github.com/Implem/Implem.Pleasanter>
- 固定位置: `Pleasanter_1.5.8.1`（`626a173e`。Questionnaire リポジトリと同じ位置）

## 厳守事項

- **コードをコピー・流用・リンク・プロジェクト参照しない。** 本アプリは Pleasanter 本体を
  改造せず、**標準 API を叩く別アプリ**として実装する
- ここは **API 仕様・サイト設定のスキーマ・列定義・挙動の事実確認** にのみ使う
- ビルド対象・配布物に含めない（ソリューションファイルにも入れない）
- `_reference/` 配下を編集しないこと。サブモジュールの作業ツリーであり、本リポジトリの
  コミットには乗らない

> 本アプリは Pleasanter を**ネットワーク越しに API 利用するだけ**なので、本体の派生著作物には
> ならない。**本体のコードを取り込んだ瞬間にこの前提が崩れる。**
> 「どうせ AGPL 側があるのだから取り込んでよい」ではない。

## 取得

```bash
git submodule update --init --recursive
```

## この submodule で確認できること

| 対象 | 確認できること |
| --- | --- |
| `Implem.Pleasanter/Controllers/Api/ItemsController.cs` | 標準 API のエンドポイント一覧とルーティング |
| `Implem.Pleasanter/Models/Results/ResultUtilities.cs`（`Get` の API 版） | 一覧取得の応答（`Response.Offset` / `PageSize` / `TotalCount` / `Data`）とページ上限の扱い |
| `Implem.Pleasanter/Models/Issues/IssueApiModel.cs` | 記録テーブルの API 上の項目（`StartTime` / `CompletionTime` ほか） |
| `Implem.Pleasanter/Libraries/Requests/Context.cs` | API キーの受け渡し方式（リクエストボディの `ApiKey`） |

## 確認できないこと

- **実際に API が返す JSON の中身**（日時の書式・タイムゾーン変換・未設定値）。**実機の応答で確認すること**
- 実運用のテナント設定・権限

## バージョン追随

固定コミットで参照する。Pleasanter 更新時は差分（API 仕様・列定義・ランタイム版数）を確認し、
必要な対応は**自前実装側に取り込む**（本体コードは持ち込まない）。
