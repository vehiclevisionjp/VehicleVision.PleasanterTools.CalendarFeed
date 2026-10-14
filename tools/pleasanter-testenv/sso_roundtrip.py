# -*- coding: utf-8 -*-
"""Pleasanter のログインで管理画面へ入る機能（Issue #28）を、実機の Pleasanter に対して通す。

**ブラウザ無しで確かめる。** 標準ライブラリだけで組んでいる。起こし方は README.md の
「Pleasanter のログインでの管理画面（SSO）」。

    python tools/pleasanter-testenv/sso_roundtrip.py

前提:
  - 検証用の Pleasanter（18090）が起きていて、seed/01_apikey.sql と seed/02_sso_users.sql を流してある
  - 本アプリが http://localhost:5099 で起きていて、最初の管理者を sso-e2e-plain（パスワード APP_PASSWORD）で作ってある
  - **本アプリと Pleasanter は、同じホスト名（localhost）で動かす。** cookie はポートを区別しないので、1 つの cookie 入れで両方へ届く

⚠️ **検証環境専用。** 既知のパスワードを使う。
"""
import http.cookiejar
import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

APP = "http://localhost:5099"
PLEASANTER = "http://localhost:18090"
APP_PASSWORD = "Sso-e2e-admin-password-1"
REVALIDATE_WAIT = 70  # 設定の再検証の間隔（1 分）を過ぎるまで待つ秒数


class Browser:
    """cookie を覚える 1 つのブラウザ。転送は追わない。"""

    class _NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *args, **kwargs):
            return None

    def __init__(self):
        self.jar = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar), self._NoRedirect())

    def request(self, method, url, body=None, form=None, headers=None):
        data = None
        hs = dict(headers or {})
        if form is not None:
            data = urllib.parse.urlencode(form).encode()
            hs["Content-Type"] = "application/x-www-form-urlencoded"
        elif body is not None:
            data = json.dumps(body).encode()
            hs["Content-Type"] = "application/json"
        req = urllib.request.Request(url, data=data, method=method, headers=hs)
        try:
            with self.opener.open(req) as r:
                return r.status, r.read().decode("utf-8", "replace")
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode("utf-8", "replace")

    def pleasanter_login(self, login_id, password):
        self.request("GET", f"{PLEASANTER}/users/login")
        status, _ = self.request(
            "POST", f"{PLEASANTER}/users/authenticate?ReturnUrl=",
            form={"Users_LoginId": login_id, "Users_Password": password},
            headers={"X-Requested-With": "XMLHttpRequest"})
        assert status == 200, status

    def pleasanter_logout(self):
        self.request("GET", f"{PLEASANTER}/users/logout")

    def check(self):
        return self.request("POST", f"{APP}/admin/api/pleasanter-sso/check", body={})

    def me(self):
        status, text = self.request("GET", f"{APP}/admin/api/me/")
        return status, (json.loads(text) if status == 200 else None)


def show(step, value):
    print(f"{step} {value}")


def main():
    # ---- 管理者（設定を変える側）
    admin = Browser()
    status, _ = admin.request("POST", f"{APP}/admin/api/login", body={"loginId": "sso-e2e-plain", "password": APP_PASSWORD})
    assert status == 200, f"管理者でログインできない: {status}"

    def settings(values):
        status, text = admin.request("PUT", f"{APP}/admin/api/settings/", body={"values": values})
        assert status == 200, text

    base = {
        "CALENDARFEED_PLEASANTERSSO_ENABLED": "true",
        "CALENDARFEED_PLEASANTERSSO_INTERNALBASEURL": PLEASANTER,
        "CALENDARFEED_PLEASANTERSSO_LOGINURL": f"{PLEASANTER}/users/login",
        "CALENDARFEED_PLEASANTERSSO_REVALIDATEMINUTES": "1",
        "CALENDARFEED_PLEASANTERSSO_UNKNOWNUSER": "Reject",
        "CALENDARFEED_PLEASANTERSSO_ALLOWEDDEPTIDS": "",
        "CALENDARFEED_PLEASANTERSSO_REGISTERROLE": "Auditor",
    }
    settings(base)

    # ---- 1. まだログインしていない
    b = Browser()
    status, text = b.check()
    show("1. 未ログイン    ", (status, text))
    assert status == 200 and json.loads(text)["status"] == "unauthenticated"

    # ---- 2. Pleasanter にログインして入る
    b.pleasanter_login("sso-e2e-plain", "SsoE2e#Plain1")
    status, text = b.check()
    show("2. 入る          ", (status, text))
    assert status == 200 and json.loads(text) == {"status": "signedIn", "next": "done"}, text
    status, me = b.me()
    show("3. me            ", (status, {k: me[k] for k in ("loginId", "role", "viaPleasanterSso")}))
    assert status == 200 and me["viaPleasanterSso"] is True and me["loginId"] == "sso-e2e-plain"

    # ---- 4. 本アプリに居ない人は通さない（Reject）
    s = Browser()
    s.pleasanter_login("sso-e2e-stranger", "SsoE2e#Stranger1")
    status, text = s.check()
    show("4. 居ない人      ", (status, text))
    assert status == 403 and json.loads(text)["code"] == "unknown-user"

    # ---- 5. 所属を許可して自動登録（組織 5051 は sso-e2e-plain / mail だけ）
    settings({"CALENDARFEED_PLEASANTERSSO_UNKNOWNUSER": "Register", "CALENDARFEED_PLEASANTERSSO_ALLOWEDDEPTIDS": "5051"})
    status, text = s.check()
    show("5. 許可外の組織  ", (status, text))
    assert status == 403 and json.loads(text)["code"] == "not-allowed", "stranger は組織 5051 に居ない"

    m = Browser()
    m.pleasanter_login("sso-e2e-mail", "SsoE2e#Mail1")
    status, text = m.check()
    show("6. 2 要素の人    ", (status, text))
    # Pleasanter 側の 2 要素（メール）を終えないと cookie が出ないので、ここへ来ない（Pleasanter が 2 要素を通す）
    assert status == 200

    # ---- 7. Pleasanter でログアウトすると、再検証の間隔の後に本アプリからも落ちる
    b.pleasanter_logout()
    status, _ = b.me()
    show("7a. 直後の me    ", status)
    assert status == 200, "間隔の内は通る"
    time.sleep(REVALIDATE_WAIT)
    status, _ = b.me()
    show("7b. 1 分後の me  ", status)
    assert status == 401, "Pleasanter でログアウトしたので、本アプリからも落ちる"

    # ---- 8. 接続の試験（管理者）
    admin.pleasanter_login("sso-e2e-plain", "SsoE2e#Plain1")
    status, text = admin.request("POST", f"{APP}/admin/api/pleasanter-sso/test", body={})
    show("8. 接続の試験    ", (status, text))
    assert status == 200 and json.loads(text)["status"] == "Authenticated"

    # 後片付け: 無効に戻す
    settings({"CALENDARFEED_PLEASANTERSSO_ENABLED": "false", "CALENDARFEED_PLEASANTERSSO_UNKNOWNUSER": "Reject", "CALENDARFEED_PLEASANTERSSO_ALLOWEDDEPTIDS": ""})
    print("OK")


if __name__ == "__main__":
    sys.exit(main())
