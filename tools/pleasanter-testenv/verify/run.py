"""CalendarFeed が頼っている Pleasanter の挙動を実機で確かめる。

**判定を推測で埋めない。** 応答は /results/ へ生のまま残し、解釈は人が読んで書く
（_documents/実機検証結果.md）。
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


def main():
    os.makedirs(OUT, exist_ok=True)

    # ---- 準備: 期限付きテーブル（Issues）----
    ss = {
        "TitleColumns": ["ClassA", "Title"],
        "TitleSeparator": " ",
        "Columns": [
            {"ColumnName": "ClassA", "LabelText": "区分",
             "ChoicesText": "1,会議\n2,出張", "ChoicesControlType": "DropDown"},
            {"ColumnName": "DateA", "LabelText": "開始日"},
            {"ColumnName": "Date001", "LabelText": "拡張日"},
            {"ColumnName": "CompletionTime", "LabelText": "終了", "EditorFormat": "Ymd"},
        ],
        "EditorColumnHash": {"General": ["Title", "Body", "StartTime", "CompletionTime", "ClassA", "DateA", "Date001"]},
    }
    status, body = api.post("api/items/0/CreateSite", {
        "Title": "検証用予定", "ReferenceType": "Issues", "SiteSettings": ss,
    })
    dump("00_create_site", status, body)
    site_id = body.get("Id")
    if not site_id:
        return 1

    # ---- レコード ----
    records = [
        {"Title": "時間つき", "Body": "本文", "StartTime": "2026-03-01T09:00:00",
         "CompletionTime": "2026-03-01T10:30:00", "ClassHash": {"ClassA": "1"},
         "DateHash": {"DateA": "2026-03-01T09:00:00", "Date001": "2026-03-02T00:00:00"}},
        {"Title": "日付だけ", "StartTime": "2026-03-05T00:00:00",
         "CompletionTime": "2026-03-06T00:00:00", "ClassHash": {"ClassA": "2"}},
        {"Title": "日付なし"},
    ]
    for i, rec in enumerate(records):
        status, body = api.post(f"api/items/{site_id}/Create", rec)
        dump(f"01_create_record_{i}", status, body)

    # ---- 取得（既定）----
    status, body = api.post(f"api/items/{site_id}/Get", {})
    dump("10_get_default", status, body)

    # ---- 取得（KeyValues・表示テキスト）----
    view = {
        "ApiDataType": 1,
        "ApiColumnKeyDisplayType": 1,
        "ApiColumnValueDisplayType": 2,
        "GridColumns": ["IssueId", "Ver", "Title", "StartTime", "CompletionTime", "ClassA", "DateA", "Date001"],
    }
    status, body = api.post(f"api/items/{site_id}/Get", {"View": view})
    dump("20_get_keyvalues_text", status, body)

    view["ApiColumnHash"] = {c: {"ValueDisplayType": 1} for c in ["IssueId", "Ver", "StartTime", "CompletionTime", "DateA", "Date001"]}
    status, body = api.post(f"api/items/{site_id}/Get", {"View": view})
    dump("21_get_keyvalues_mixed", status, body)

    # ---- GetSite（列の入力形式など）----
    status, body = api.post(f"api/items/{site_id}/GetSite", {})
    dump("30_getsite", status, body)
    return 0


if __name__ == "__main__":
    sys.exit(main())
