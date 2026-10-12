"""既存サイトのレコードを、取得方法を変えて取り直す（タイムゾーンなどを変えた後の見え方の確認用）。

使い方: docker compose run --rm verify python get.py <サイトID> <出力の接頭辞>
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
import api  # noqa: E402

site_id = int(sys.argv[1])
prefix = sys.argv[2] if len(sys.argv) > 2 else "get"
cols = ["IssueId", "Ver", "Title", "StartTime", "CompletionTime", "ClassA", "DateA", "UpdatedTime"]


def show(name, body):
    print(f"== {name}")
    for row in body["Response"]["Data"]:
        keep = {k: row.get(k) for k in cols + ["ItemTitle"] if k in row}
        if "DateHash" in row:
            keep["DateHash"] = row["DateHash"]
        print(json.dumps(keep, ensure_ascii=False))
    os.makedirs("/results", exist_ok=True)
    with open(f"/results/{prefix}_{name}.json", "w", encoding="utf-8") as f:
        json.dump(body, f, ensure_ascii=False, indent=2)


_, body = api.post(f"api/items/{site_id}/Get", {})
show("default", body)

base = {"ApiDataType": 1, "ApiColumnKeyDisplayType": 1, "GridColumns": cols}
for label, vt in [("kv_displayvalue", 0), ("kv_value", 1), ("kv_text", 2)]:
    _, body = api.post(f"api/items/{site_id}/Get", {"View": {**base, "ApiColumnValueDisplayType": vt}})
    show(label, body)
