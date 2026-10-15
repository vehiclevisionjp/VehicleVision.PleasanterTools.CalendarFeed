# -*- coding: utf-8 -*-
"""SAML のログインを、実機の IdP（Keycloak）で端から端まで通す（Issue #28・#45）。

**CI と手元で同じものを使う。** 標準ライブラリだけで組んでいる。

    python tools/saml-idp/e2e.py

前提:
  - 検証用の Keycloak（tools/saml-idp/docker-compose.yml）が起きている
  - 本アプリが https://localhost:8443 で起きている（公開 URL も同じ。証明書は tools/saml-idp/tls）。
    秘密の鍵（CALENDARFEED_SECRET_KEY）が要る
  - 最初の管理者を admin@example.jp（パスワード APP_PASSWORD）で作ってある

通すもの: 管理者の往復と単一ログアウト／本アプリに居ない人の拒否（Reject）／自動登録（Register）して入る。

⚠️ **検証環境専用。** 既知のパスワードと、証明書を確かめない接続を使う。
"""
import http.cookiejar
import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

import roundtrip

APP_PASSWORD = "Saml-e2e-admin-password-1"
USER_PASSWORD = "idp-test-password"
REALM = Path(__file__).parent / "realm" / "realm-calendarfeed.json"


def idp_certificate():
    realm = json.loads(REALM.read_text(encoding="utf-8"))
    for component in realm["components"]["org.keycloak.keys.KeyProvider"]:
        certificate = component.get("config", {}).get("certificate")
        if certificate:
            return f"-----BEGIN CERTIFICATE-----\n{certificate[0]}\n-----END CERTIFICATE-----"
    raise SystemExit("realm に署名証明書が無い")


def main():
    # 設定を変える管理者のセッション（パスワードで入る）
    jar = http.cookiejar.CookieJar()
    opener = urllib.request.build_opener(
        urllib.request.HTTPCookieProcessor(jar), urllib.request.HTTPSHandler(context=roundtrip.CTX))

    def call(method, path, body):
        request = urllib.request.Request(
            roundtrip.APP + path, data=None if body is None else json.dumps(body).encode(), method=method,
            headers={"Content-Type": "application/json"} if body is not None else {})
        try:
            with opener.open(request) as response:
                return response.status, response.read().decode()
        except urllib.error.HTTPError as error:
            return error.code, error.read().decode()

    status, text = call("POST", "/admin/api/login", {"loginId": "admin@example.jp", "password": APP_PASSWORD})
    assert status == 200, f"管理者でログインできない: {status} {text}"

    def settings(values):
        status, text = call("PUT", "/admin/api/settings/", {"values": values})
        assert status == 200, text

    settings({
        "CALENDARFEED_SAML_ENABLED": "true",
        "CALENDARFEED_SAML_ENTITYID": f"{roundtrip.APP}/admin/api/saml/metadata",
        "CALENDARFEED_SAML_IDPENTITYID": "https://localhost:8543/realms/calendarfeed",
        "CALENDARFEED_SAML_SINGLESIGNONURL": "https://localhost:8543/realms/calendarfeed/protocol/saml",
        "CALENDARFEED_SAML_IDPCERTIFICATE": idp_certificate(),
        "CALENDARFEED_SAML_SINGLELOGOUTURL": "https://localhost:8543/realms/calendarfeed/protocol/saml",
        "CALENDARFEED_SAML_UNKNOWNUSER": "Reject",
    })

    print("== 1. 管理者の往復（ログイン → ACS → me → 単一ログアウト）")
    roundtrip.main("admin@example.jp", USER_PASSWORD)

    print("== 2. 本アプリに居ない人は通さない（Reject）")
    roundtrip.main("stranger@example.jp", USER_PASSWORD, "samlError=unknown-user")

    print("== 3. 自動登録（Register・役割 Auditor）して入る")
    settings({"CALENDARFEED_SAML_UNKNOWNUSER": "Register", "CALENDARFEED_SAML_REGISTERROLE": "Auditor"})
    roundtrip.main("stranger@example.jp", USER_PASSWORD)

    status, text = call("GET", "/admin/api/users", None)
    assert status == 200 and "stranger@example.jp" in text and "Auditor" in text, text
    print("OK")


if __name__ == "__main__":
    sys.exit(main())
