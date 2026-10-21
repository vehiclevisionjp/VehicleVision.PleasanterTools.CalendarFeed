"""検証の下ごしらえ: Pleasanter にサイトと予定を作り、本アプリに管理者とフィード（CalDAV 有効）を作る。"""
import json
import urllib.error
import urllib.request

from common import APP, PLEASANTER, API_KEY, pleasanter, save_state

ADMIN = {"loginId": "admin", "password": "correct horse battery staple"}


def app(method, path, body=None, cookies=None):
    """本アプリの管理 API。同一オリジンの検査のため Origin を付ける。"""
    headers = {"Content-Type": "application/json", "Origin": APP}
    if cookies:
        headers["Cookie"] = cookies
    req = urllib.request.Request(
        APP + path, json.dumps(body, ensure_ascii=False).encode("utf-8") if body is not None else None, headers, method=method
    )
    with urllib.request.urlopen(req, timeout=60) as res:
        raw = res.read()
        cookie = "; ".join(c.split(";")[0] for c in res.headers.get_all("Set-Cookie") or [])
        return res.status, (json.loads(raw) if raw else None), cookie


site = pleasanter(
    "api/items/0/CreateSite",
    {
        "Title": "CalDAV 実クライアント検証",
        "ReferenceType": "Issues",
        "SiteSettings": {"EditorColumnHash": {"General": ["Title", "StartTime", "CompletionTime", "Body"]}},
    },
)["Id"]
existing = pleasanter(
    f"api/items/{site}/create",
    {"Title": "Pleasanter で作った会議", "StartTime": "2026-11-01T09:00:00", "CompletionTime": "2026-11-01T10:00:00", "Body": "議題"},
)["Id"]

try:
    app("POST", "/admin/api/setup", ADMIN)
except urllib.error.HTTPError:
    pass  # 2 回目以降（最初の管理者は作成済み）
_, _, cookies = app("POST", "/admin/api/login", ADMIN)

# 前の検証のフィードは CalDAV を止める（クライアントに、カレンダーが 1 つだけ見えるように）
_, old_feeds, _ = app("GET", "/admin/api/feeds/", None, cookies)
for old in old_feeds:
    app("PUT", f"/admin/api/feeds/{old['id']}/caldav", {"enabled": False}, cookies)

_, created, _ = app("POST", "/admin/api/feeds", {"name": "検証カレンダー", "siteId": site}, cookies)
feed = created["feed"]["id"]
status, _, _ = app("PUT", f"/admin/api/feeds/{feed}/caldav", {"enabled": True}, cookies)
assert status == 204, status

save_state({"site": site, "feed": feed, "existing": existing, "url": f"{APP}/caldav/"})
print(f"site={site} existing record={existing} feed={feed}")
