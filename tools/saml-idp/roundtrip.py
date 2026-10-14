# -*- coding: utf-8 -*-
"""検証用 IdP（Keycloak）へ SAML の往復を通す（Issue #28）。

**ブラウザ無しで確かめる。** 標準ライブラリだけで組んでいる。

    python tools/saml-idp/roundtrip.py admin@example.jp idp-test-password

起こし方は tools/saml-idp/README.md。Questionnaire の同名のスクリプトに倣った。

⚠️ **検証環境専用。** 自己署名の証明書を確かめずに繋ぐので、本番へ向けて使わないこと。
"""
import http.cookiejar
import re
import ssl
import sys
import urllib.error
import urllib.parse
import urllib.request

APP = "https://localhost:8443"
CTX = ssl._create_unverified_context()  # 開発用の自己署名証明書


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def request(opener, method, url, fields=None):
    data = urllib.parse.urlencode(fields).encode() if fields is not None else None
    req = urllib.request.Request(
        url, data=data, method=method,
        headers={"Content-Type": "application/x-www-form-urlencoded"} if data is not None else {})
    try:
        with opener.open(req) as response:
            return response.status, response.headers, response.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as error:
        return error.code, error.headers, error.read().decode("utf-8", "replace")


def main(user, password, expect_failure=None):
    # 1 つの cookie 入れを使い回す（ブラウザ 1 つ分）
    jar = http.cookiejar.CookieJar()
    processor = urllib.request.HTTPCookieProcessor(jar)
    https = urllib.request.HTTPSHandler(context=CTX)
    follower = urllib.request.build_opener(processor, https)
    stopper = urllib.request.build_opener(processor, https, NoRedirect())

    # ---- 1. SP へログインを頼む（IdP へ飛ばされる）
    status, headers, _ = request(stopper, "GET", f"{APP}/admin/api/saml/login?returnUrl=/admin/users")
    assert status in (301, 302, 303, 307), status
    idp_url = headers["Location"]
    print(f"1. SP -> IdP  {status}  {idp_url[:80]}...")
    assert "SAMLRequest=" in idp_url
    assert any(cookie.name == "calendarfeed_saml" for cookie in jar), "途中を預ける cookie が無い"

    # ---- 2. IdP のログイン画面
    status, _, html = request(follower, "GET", idp_url)
    print(f"2. IdP login  {status}")
    action = re.search(r'<form id="kc-form-login"[^>]*action="([^"]+)"', html)
    assert action, html[:400]
    login_url = action.group(1).replace("&amp;", "&")

    # ---- 3. 資格情報を出す（SAMLResponse を積んだ form が返る）
    status, _, html = request(follower, "POST", login_url, {"username": user, "password": password, "credentialId": ""})
    print(f"3. IdP auth   {status}")
    response = re.search(r'name="SAMLResponse" value="([^"]+)"', html)
    assert response, html[:600]
    acs = re.search(r'action="([^"]+)"', html).group(1).replace("&amp;", "&")
    relay = re.search(r'name="RelayState" value="([^"]*)"', html)
    print(f"   ACS = {acs}")

    # ---- 4. 応答を SP の受け口へ渡す
    fields = {"SAMLResponse": response.group(1)}
    if relay:
        fields["RelayState"] = relay.group(1)
    status, headers, _ = request(stopper, "POST", acs, fields)
    location = headers.get("Location", "")
    print(f"4. IdP -> SP  {status}  Location={location}")
    if expect_failure:
        assert expect_failure in location, location
        print("   （失敗を期待どおりに確認）")
        return

    assert location == "/admin/users", location

    # ---- 5. 入れたか
    status, _, body = request(follower, "GET", f"{APP}/admin/api/me/")
    print(f"5. me         {status}  {body[:200]}")
    assert status == 200

    # ---- 6. 単一ログアウト（SP から。IdP へ頼む）
    status, headers, _ = request(stopper, "GET", f"{APP}/admin/api/saml/logout")
    print(f"6. SP logout  {status}  {headers.get('Location', '')[:90]}")
    assert "SAMLRequest=" in headers.get("Location", "")
    status, _, _ = request(follower, "GET", f"{APP}/admin/api/me/")
    print(f"7. me (after) {status}")
    assert status == 401


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else None)
