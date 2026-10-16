"""検証の共通部分。Pleasanter の標準 API（検証用の API キー）と、本アプリの管理 API を叩く。"""
import json
import os
import urllib.error
import urllib.request

PLEASANTER = os.environ.get("PLEASANTER_URL", "http://pleasanter:8080").rstrip("/")
APP = os.environ.get("APP_URL", "http://app:8080").rstrip("/")
API_KEY = os.environ["PLEASANTER_API_KEY"]
STATE = "/state/state.json"


def pleasanter(path, body=None):
    """Pleasanter の標準 API（API キーは本文）。"""
    payload = dict(body or {})
    payload["ApiKey"] = API_KEY
    req = urllib.request.Request(
        f"{PLEASANTER}/{path.lstrip('/')}",
        json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        {"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=60) as res:
        return json.load(res)


def records(site_id):
    """サイトの今のレコード（Pleasanter 側から見た真実）。"""
    data = pleasanter(f"api/items/{site_id}/get", {"PageSize": 200})["Response"]["Data"]
    return {int(r["IssueId"]): r for r in data}


def load_state():
    with open(STATE, encoding="utf-8") as f:
        return json.load(f)


def save_state(state):
    os.makedirs(os.path.dirname(STATE), exist_ok=True)
    with open(STATE, "w", encoding="utf-8") as f:
        json.dump(state, f)


class Check:
    """失敗を数えて、最後に終了コードへ。1 つ落ちても残りを続ける。"""

    def __init__(self, name):
        self.name = name
        self.failures = 0

    def ok(self, condition, message):
        print(("  OK   " if condition else "  FAIL ") + message)
        if not condition:
            self.failures += 1

    def done(self):
        print(f"== {self.name}: {'通った' if self.failures == 0 else str(self.failures) + ' 件失敗'}")
        return 1 if self.failures else 0
