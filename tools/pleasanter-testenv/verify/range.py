"""期間の絞り込み（`View.ColumnFilterHash` の日付の範囲）の挙動を実機で確かめる（Issue #67）。

**判定を推測で埋めない。** 応答は /results/ へ生のまま残し、解釈は人が読んで書く（_documents/実機検証結果.md）。

    docker compose run --rm verify python range.py

確かめること:
  1. 範囲の書式は ``["from,to"]``（JSON の配列を文字列にしたもの）。**どちらかを空にすると開区間**
  2. 開始が窓の終わり以前 ＆ 終了が窓の始まり以後（重なり）を、2 つの列の絞り込みの AND で表せるか
  3. 日付の列（DateA）でも同じか
  4. 境界（窓の始まり・終わりちょうど）の扱い
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
import api  # noqa: E402

OUT = "/results"


def dump(name, status, body):
    path = os.path.join(OUT, name + ".json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump({"status": status, "body": body}, f, ensure_ascii=False, indent=2)
    print(f"[{status}] {name}")


def titles(body):
    rows = (body.get("Response") or {}).get("Data") or []
    return sorted(r.get("Title") for r in rows)


def main():
    os.makedirs(OUT, exist_ok=True)

    status, body = api.post("api/items/0/CreateSite", {
        "Title": "期間の検証", "ReferenceType": "Issues",
        "SiteSettings": {
            "Columns": [{"ColumnName": "DateA", "LabelText": "開始日"}],
            "EditorColumnHash": {"General": ["Title", "StartTime", "CompletionTime", "DateA"]},
        },
    })
    dump("range_00_create_site", status, body)
    site_id = body.get("Id")
    if not site_id:
        return 1

    # 窓は 2026-03-01 〜 2026-03-31 を想定する
    records = [
        ("A-1月（窓の前）", "2026-01-10T09:00:00", "2026-01-10T10:00:00", "2026-01-10T00:00:00"),
        ("B-3月の中", "2026-03-10T09:00:00", "2026-03-10T10:00:00", "2026-03-10T00:00:00"),
        ("C-窓の始まりをまたいで終わる", "2026-02-28T22:00:00", "2026-03-01T01:00:00", "2026-02-28T00:00:00"),
        ("D-窓の終わりをまたいで始まる", "2026-03-31T22:00:00", "2026-04-01T02:00:00", "2026-03-31T00:00:00"),
        ("E-窓を丸ごと含む長い予定", "2026-02-01T09:00:00", "2026-05-01T18:00:00", "2026-02-01T00:00:00"),
        ("F-6月（窓の後）", "2026-06-01T09:00:00", "2026-06-01T10:00:00", "2026-06-01T00:00:00"),
        ("G-窓の終わりちょうどに始まる", "2026-03-31T23:59:59", "2026-04-01T00:30:00", "2026-03-31T00:00:00"),
        ("H-窓の始まりちょうどに終わる", "2026-02-28T23:00:00", "2026-03-01T00:00:00", "2026-02-28T00:00:00"),
    ]
    for i, (title, start, end, date_a) in enumerate(records):
        status, body = api.post(f"api/items/{site_id}/Create", {
            "Title": title, "StartTime": start, "CompletionTime": end, "DateHash": {"DateA": date_a},
        })
        dump(f"range_01_create_{i}", status, body)

    def fetch(name, filters):
        status, body = api.post(f"api/items/{site_id}/Get", {
            "View": {"ColumnFilterHash": filters}, "ApiVersion": 1.1,
        })
        dump(name, status, body)
        print("   ->", titles(body))
        return titles(body)

    fetch("range_10_all", {})
    # 開始が窓の終わり以前（上側だけ。from を空にする）
    fetch("range_11_start_before_end", {"StartTime": json.dumps([",2026/03/31 23:59:59"])})
    # 終了が窓の始まり以後（下側だけ。to を空にする）
    fetch("range_12_end_after_start", {"CompletionTime": json.dumps(["2026/03/01 00:00:00,"])})
    # 重なり: 2 つの列の AND
    fetch("range_13_overlap", {
        "StartTime": json.dumps([",2026/03/31 23:59:59"]),
        "CompletionTime": json.dumps(["2026/03/01 00:00:00,"]),
    })
    # 日付の列（DateA）の絞り込み
    fetch("range_20_datea_between", {"DateA": json.dumps(["2026/03/01 00:00:00,2026/03/31 23:59:59"])})
    fetch("range_21_datea_open_to", {"DateA": json.dumps(["2026/03/01 00:00:00,"])})
    # 書式の確認: 日付だけ・T 区切り
    fetch("range_30_date_only_format", {"StartTime": json.dumps(["2026/03/01,2026/03/31"])})
    fetch("range_31_iso_format", {"StartTime": json.dumps(["2026-03-01T00:00:00,2026-03-31T23:59:59"])})
    return 0


if __name__ == "__main__":
    sys.exit(main())
