"""Python の caldav ライブラリ（実クライアント）で、探索・作成・更新・競合・削除を通す。
結果は Pleasanter 側のレコードで確かめる（本アプリの応答だけを信じない）。"""
import caldav
from caldav.lib import error

from common import Check, load_state, pleasanter, records

state = load_state()
check = Check("caldav ライブラリ")
site, existing = state["site"], state["existing"]


def ics(uid, summary, extra="", start="DTSTART;TZID=Asia/Tokyo:20261120T180000", end="DTEND;TZID=Asia/Tokyo:20261120T190000"):
    lines = ["BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//check//EN", "BEGIN:VEVENT", f"UID:{uid}", start, end, f"SUMMARY:{summary}"]
    if extra:
        lines.append(extra)
    lines += ["END:VEVENT", "END:VCALENDAR", ""]
    return "\r\n".join(lines)


def title_of(record_id):
    return records(site).get(record_id, {}).get("Title")


def find(cal, needle):
    return next((e for e in cal.events() if needle in e.data), None)


client = caldav.DAVClient(url=state["url"], username="alice", password=__import__("os").environ["PLEASANTER_API_KEY"])
cal = client.principal().calendars()[0]
check.ok(cal.get_display_name() == "検証カレンダー", "探索: principal → calendars で、フィードがカレンダーとして見える")
check.ok(find(cal, "Pleasanter で作った会議") is not None, "一覧: Pleasanter で作った予定が見える")

# ---- 作成 ----
created = cal.save_event(ics("check-created-1", "クライアントから作成", "DESCRIPTION:作成の説明"))
rec = next((r for r in records(site).values() if r["Title"] == "クライアントから作成"), None)
check.ok(rec is not None, "作成: Pleasanter にレコードができた")
if rec:
    check.ok(rec["StartTime"].startswith("2026-11-20T18:00:00"), f"作成: 開始は壁時計の 18:00（{rec['StartTime']}）")
    check.ok(rec.get("Body") == "作成の説明", "作成: 説明が入った")
check.ok(created.url.path.endswith("/check-created-1.ics") or "check-created-1" in str(created.url), f"作成: クライアントが付けた名前で見える（{created.url}）")
check.ok(find(cal, "check-created-1") is not None, "一覧: 付けた UID で見える")

# ---- 更新（If-Match つき） ----
event = find(cal, "check-created-1")
event.data = event.data.replace("SUMMARY:クライアントから作成", "SUMMARY:クライアントから更新")
event.save()
check.ok(rec and title_of(rec["IssueId"]) == "クライアントから更新", "更新: 題名が変わった")
check.ok(rec and records(site)[rec["IssueId"]].get("Body") == "作成の説明", "更新: 説明は残った")

# ---- 競合: 取得の後で Pleasanter の画面（API）で直された ----
event = find(cal, "check-created-1")
pleasanter(f"api/items/{rec['IssueId']}/update", {"Title": "画面で直した題名"})
event.data = event.data.replace("SUMMARY:クライアントから更新", "SUMMARY:古い内容からの上書き")
try:
    event.save()
    check.ok(False, "競合: 古い ETag の更新は断られる（通ってしまった）")
except error.DAVError as ex:
    check.ok("412" in str(ex), f"競合: 古い ETag の更新は 412 で断られる（{str(ex).splitlines()[0]}）")
check.ok(rec and title_of(rec["IssueId"]) == "画面で直した題名", "競合: Pleasanter 側の変更は上書きされなかった")

# ---- 既存の予定の更新 ----
event = find(cal, "Pleasanter で作った会議")
event.data = event.data.replace("SUMMARY:Pleasanter で作った会議", "SUMMARY:Pleasanter で作った会議（変更）")
event.save()
check.ok(title_of(existing) == "Pleasanter で作った会議（変更）", "更新: Pleasanter で作った予定を直せた")

# ---- 受け付けない書き込み ----
try:
    cal.save_event(ics("check-dup", "UID が重なる") + "")
    cal.save_event(ics("check-dup", "UID が重なる（2 件目）"))  # 同じ UID・同じ名前は更新になる（If-Match が無いので断られる）
    check.ok(False, "重複: 同じ UID の再作成は断られる")
except error.DAVError:
    check.ok(True, "重複: 同じ UID の再作成は断られる")
try:
    cal.save_event(ics("check-rrule", "繰り返し", "RRULE:FREQ=WEEKLY"))
    check.ok(False, "繰り返し: 断られる")
except error.AuthorizationError:
    check.ok(True, "繰り返し: 403 で断られる（caldav は 403 を AuthorizationError にする）")
except error.DAVError as ex:
    check.ok(False, f"繰り返し: 403 で断られる（{type(ex).__name__}: {str(ex)[:120]!r}）")

# ---- 削除 → 作り直し ----
event = find(cal, "check-created-1")
event.delete()
check.ok(find(cal, "check-created-1") is None, "削除: 一覧から消えた")
check.ok(rec and rec["IssueId"] not in records(site), "削除: Pleasanter の一覧から消えた（ごみ箱へ）")
cal.save_event(ics("check-created-1", "同じ UID で作り直し"))
check.ok(find(cal, "check-created-1") is not None, "作り直し: 同じ UID で作れる")

raise SystemExit(check.done())
