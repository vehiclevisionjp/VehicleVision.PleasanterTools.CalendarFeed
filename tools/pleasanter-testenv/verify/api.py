"""Pleasanter 標準 API の薄いクライアント（検証用）。

Pleasanter は API キーを **リクエストボディの JSON** に載せる方式なので、
ヘッダではなくボディへ入れる。
"""
import json
import os
import urllib.request
import urllib.error

BASE = os.environ.get("PLEASANTER_BASE_URL", "http://pleasanter:8080").rstrip("/")
API_KEY = os.environ["PLEASANTER_API_KEY"]


def post(path: str, body: dict, timeout: int = 60):
    payload = dict(body)
    payload["ApiKey"] = API_KEY
    data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(
        f"{BASE}/{path.lstrip('/')}",
        data=data,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as res:
            raw = res.read().decode("utf-8")
            status = res.status
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", errors="replace")
        status = e.code
    try:
        return status, json.loads(raw)
    except json.JSONDecodeError:
        return status, {"_raw": raw[:2000]}
