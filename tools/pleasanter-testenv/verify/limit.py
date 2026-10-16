"""Pleasanter の API の回数制限（`Api.json` の `LimitPerSite`。サイトごと・1 日）に当たったときの挙動を実機で確かめる（Issue #69）。

    docker compose -f docker-compose.yml -f docker-compose.limit.yml run --rm verify python limit.py

確かめること: ①どの API が数えられるか（1 回の Get ＝ 1 回か）②超えたときの HTTP の状態と本文
③数えは作成（Create）も含むか ④残りの回数（`LimitRemaining`）が応答に出るか
**応答は /results/ へ生のまま残す。**
"""
import json
import os
import sys
import urllib.error
import urllib.request

sys.path.insert(0, os.path.dirname(__file__))
import api  # noqa: E402

OUT = "/results"


def raw_post(path, body):
    payload = dict(body)
    payload["ApiKey"] = api.API_KEY
    request = urllib.request.Request(
        f"{api.BASE}/{path.lstrip('/')}", data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        headers={"Content-Type": "application/json"}, method="POST")
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            return response.status, dict(response.headers), response.read().decode("utf-8")
    except urllib.error.HTTPError as error:
        return error.code, dict(error.headers), error.read().decode("utf-8", errors="replace")


def dump(name, status, headers, text):
    os.makedirs(OUT, exist_ok=True)
    try:
        body = json.loads(text)
    except ValueError:
        body = text
    with open(os.path.join(OUT, name + ".json"), "w", encoding="utf-8") as f:
        json.dump({"http": status, "headers": {k: v for k, v in headers.items() if k.lower().startswith(("retry", "x-", "content-type"))}, "body": body}, f, ensure_ascii=False, indent=2)
    return body


def main():
    status, headers, text = raw_post("api/items/0/CreateSite", {
        "Title": "回数制限の検証", "ReferenceType": "Issues",
        "SiteSettings": {"EditorColumnHash": {"General": ["Title", "StartTime", "CompletionTime"]}},
    })
    body = dump("limit_00_create_site", status, headers, text)
    print(f"create site: http={status} body.StatusCode={body.get('StatusCode') if isinstance(body, dict) else body}")
    site_id = body.get("Id") if isinstance(body, dict) else None
    if not site_id:
        return 1

    # サイトを作る API は、サイトの数えには入らない（まだ無いサイト）。以降、このサイトへの呼び出しを数える
    for i in range(1, 9):
        status, headers, text = raw_post(f"api/items/{site_id}/Get", {"ApiVersion": 1.1})
        body = dump(f"limit_{i:02d}_get", status, headers, text)
        info = body if isinstance(body, dict) else {}
        print(f"get #{i}: http={status} StatusCode={info.get('StatusCode')} LimitPerDate={info.get('LimitPerDate')} "
              f"LimitRemaining={info.get('LimitRemaining')} message={str(info.get('Message'))[:80]!r}")

    # 作成も数えられるか
    status, headers, text = raw_post(f"api/items/{site_id}/Create", {"Title": "x"})
    body = dump("limit_20_create", status, headers, text)
    print(f"create record: http={status} StatusCode={body.get('StatusCode') if isinstance(body, dict) else body}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
