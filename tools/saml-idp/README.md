# 検証用の SAML IdP（Keycloak）

SAML ログイン（Issue #28）を、実際の IdP で確かめるための環境。Questionnaire の同名のものに倣った。

⚠️ **検証環境専用。** 自己署名の証明書と公開された鍵・既知のパスワードを使う。本番へ向けないこと。

## 起こす

```bash
docker compose -f tools/saml-idp/docker-compose.yml up -d
```

- IdP: `https://localhost:8543`（realm `calendarfeed`）
- 利用者: `admin@example.jp` / `stranger@example.jp`（パスワードはどちらも `idp-test-password`）
- クライアント: `https://localhost:8443/admin/api/saml/metadata`（ACS・SLO は `https://localhost:8443/admin/api/saml/{acs,slo}`）

## 本アプリを HTTPS で起こす

```bash
export CALENDARFEED_DB_CONNECTIONSTRING="Data Source=saml.db"
export CALENDARFEED_SECRET_KEY="$(dotnet run --project src/VehicleVision.PleasanterTools.CalendarFeed.Web -- --generate-secret-key | tail -1)"
export CALENDARFEED_PUBLIC_BASEURL=https://localhost:8443
export ASPNETCORE_URLS=https://localhost:8443
export Kestrel__Certificates__Default__Path=tools/saml-idp/tls/cert.pem
export Kestrel__Certificates__Default__KeyPath=tools/saml-idp/tls/key.pem
dotnet run --project src/VehicleVision.PleasanterTools.CalendarFeed.Web
```

最初の管理者を `admin@example.jp` で作り、管理画面の設定（または `PUT /admin/api/settings/`）で `CALENDARFEED_SAML_*` を設定する。
IdP の署名証明書は `realm/realm-calendarfeed.json` の `certificate`、IdP のエンティティ ID は `https://localhost:8543/realms/calendarfeed`、
シングルサインオン URL は `https://localhost:8543/realms/calendarfeed/protocol/saml`。

## 往復を通す

```bash
python tools/saml-idp/roundtrip.py admin@example.jp idp-test-password
python tools/saml-idp/roundtrip.py stranger@example.jp idp-test-password unknown-user   # Reject のとき
```

ログイン → ACS → `/me` → SP からのログアウト（IdP へ SAMLRequest）までを、ブラウザ無しで確かめる。
ログインの制限（10 回／5 分）に当たったら、アプリを起こし直す。
