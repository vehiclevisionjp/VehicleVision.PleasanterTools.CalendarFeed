"""CalDAV の書き込みの前提（Issue #85 段階 0）を実機で確かめる。

**判定を推測で埋めない。** 応答は /results/ へ生のまま残し、解釈は人が読んで書く（_documents/実機検証結果.md）。

    docker compose run --rm verify python caldav_write.py
    # ホストから: PLEASANTER_BASE_URL=http://localhost:18090 PLEASANTER_API_KEY=... python caldav_write.py

確かめること:
  1. 更新 API の本文へ ``Timestamp``（取得で得た値）を載せると、更新が通るか
  2. **古い Timestamp** を載せると、どう断られるか（HTTP ステータス・本文）。競合の検出に使えるか
  3. Timestamp を載せない更新は、後勝ちで通るか（既存の挙動の確認）
  4. 削除 API の応答と、削除後の取得
"""
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(__file__))
import api  # noqa: E402

OUT = os.environ.get("VERIFY_OUT", "/results")


def dump(name, status, body):
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, name + ".json"), "w", encoding="utf-8") as f:
        json.dump({"status": status, "body": body}, f, ensure_ascii=False, indent=2)
    print(f"[{status}] {name}")


def row(body):
    data = (body.get("Response") or {}).get("Data") or []
    return data[0] if data else {}


def main():
    status, body = api.post("api/items/0/CreateSite", {
        "Title": "CalDAV 書き込みの検証", "ReferenceType": "Issues",
        "SiteSettings": {"EditorColumnHash": {"General": ["Title", "StartTime", "CompletionTime", "Body"]}},
    })
    dump("caldav_00_create_site", status, body)
    site_id = body.get("Id")
    if not site_id:
        return 1

    status, body = api.post(f"api/items/{site_id}/create", {
        "Title": "最初の題名", "StartTime": "2026-03-10T09:00:00", "CompletionTime": "2026-03-10T10:00:00"})
    dump("caldav_01_create", status, body)
    rid = body.get("Id")

    status, body = api.post(f"api/items/{rid}/get", {})
    dump("caldav_02_get", status, body)
    first = row(body)
    stamp = first.get("Timestamp")
    print("Timestamp:", stamp, "UpdatedTime:", first.get("UpdatedTime"), "Ver:", first.get("Ver"))

    # 1. 正しい Timestamp で更新
    status, body = api.post(f"api/items/{rid}/update", {"Title": "更新 1（正しい Timestamp）", "Timestamp": stamp})
    dump("caldav_03_update_with_timestamp", status, body)

    # 2. 古い Timestamp で更新（上の更新で Timestamp は進んでいる）
    time.sleep(1)
    status, body = api.post(f"api/items/{rid}/update", {"Title": "更新 2（古い Timestamp）", "Timestamp": stamp})
    dump("caldav_04_update_with_stale_timestamp", status, body)

    status, body = api.post(f"api/items/{rid}/get", {})
    dump("caldav_05_get_after_stale", status, body)
    print("題名（古い Timestamp の更新が通っていれば '更新 2'）:", row(body).get("Title"))

    # 3. Timestamp なし
    status, body = api.post(f"api/items/{rid}/update", {"Title": "更新 3（Timestamp なし）"})
    dump("caldav_06_update_without_timestamp", status, body)

    # 4. 削除
    status, body = api.post(f"api/items/{rid}/delete", {})
    dump("caldav_07_delete", status, body)
    status, body = api.post(f"api/items/{rid}/get", {})
    dump("caldav_08_get_after_delete", status, body)
    print("SITE_ID", site_id, "RECORD_ID", rid)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
