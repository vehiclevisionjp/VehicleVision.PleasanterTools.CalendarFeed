"""vdirsyncer（実クライアント。CalDAV ⇔ ローカルの .ics ファイル）で、探索・取得・更新・作成・削除の同期を通す。"""
import glob
import os
import pathlib
import subprocess

from common import Check, load_state, records

state = load_state()
check = Check("vdirsyncer")
site, existing = state["site"], state["existing"]
home = pathlib.Path("/tmp/vds")
home.mkdir(exist_ok=True)
config = home / "config"
config.write_text(
    f"""[general]
status_path = "{home}/status/"

[pair cal]
a = "local"
b = "remote"
collections = ["from b"]

[storage local]
type = "filesystem"
path = "{home}/calendars/"
fileext = ".ics"

[storage remote]
type = "caldav"
url = "{state['url']}"
username = "alice"
password = "{os.environ['PLEASANTER_API_KEY']}"
""",
    encoding="utf-8",
)


def vdirsyncer(*args):
    result = subprocess.run(["vdirsyncer", "-c", str(config), *args], capture_output=True, text=True, input="y\n")
    print(f"$ vdirsyncer {' '.join(args)} -> {result.returncode}")
    if result.returncode:
        print(result.stdout[-1500:], result.stderr[-1500:])
    return result.returncode


def local_files():
    return sorted(pathlib.Path(p) for p in glob.glob(f"{home}/calendars/*/*.ics"))


def title_of(record_id):
    return records(site).get(record_id, {}).get("Title")


check.ok(vdirsyncer("discover", "cal") == 0, "探索: discover が通る（カレンダーが見つかる）")
check.ok(vdirsyncer("sync") == 0, "取得: sync が通る")
files = local_files()
check.ok(len(files) == 1, f"取得: 既存の予定が 1 件、ファイルに落ちた（{len(files)} 件）")

# ---- 更新を送る ----
if files:
    text = files[0].read_text(encoding="utf-8")
    files[0].write_text(text.replace("SUMMARY:Pleasanter で作った会議", "SUMMARY:vdirsyncer で直した会議"), encoding="utf-8")
    check.ok(vdirsyncer("sync") == 0, "更新: sync が通る")
    check.ok(title_of(existing) == "vdirsyncer で直した会議", "更新: Pleasanter の題名が変わった")

# ---- 作成を送る ----
calendar_dir = files[0].parent if files else next(pathlib.Path(f"{home}/calendars").iterdir())
(calendar_dir / "vds-new-1.ics").write_text(
    "BEGIN:VCALENDAR\nVERSION:2.0\nPRODID:-//vds//EN\nBEGIN:VEVENT\nUID:vds-new-1\n"
    "DTSTART:20261201T010000Z\nDTEND:20261201T020000Z\nSUMMARY:vdirsyncer で作った会議\nEND:VEVENT\nEND:VCALENDAR\n",
    encoding="utf-8",
)
check.ok(vdirsyncer("sync") == 0, "作成: sync が通る")
created = next((r for r in records(site).values() if r["Title"] == "vdirsyncer で作った会議"), None)
check.ok(created is not None, "作成: Pleasanter にレコードができた")
if created:
    check.ok(created["StartTime"].startswith("2026-12-01T10:00:00"), f"作成: UTC 01:00 は壁時計の 10:00（{created['StartTime']}）")

# ---- 落ち着かせる: 本アプリは保存した内容を整えるので、PUT の応答に（強い）ETag を付けない（RFC 4791 5.3.4）----
# vdirsyncer は、次の同期で ETag の変わりを見つけて取り直す。**この同期の前に消すと、「サーバー側が変わった」ものを
# 「こちらで消した」ものより優先して、消しが取り消される**（vdirsyncer の競合の決め方。サーバーの不具合ではない）
check.ok(vdirsyncer("sync") == 0, "落ち着かせる: もう一度 sync する")

# ---- 削除を送る ----
(calendar_dir / "vds-new-1.ics").unlink()
check.ok(vdirsyncer("sync") == 0, "削除: sync が通る")
check.ok(created is not None and created["IssueId"] not in records(site), "削除: Pleasanter からレコードが消えた（ごみ箱へ）")

# ---- 落ち着いた状態: もう一度同期しても何も起きない ----
before = {i: r["Title"] for i, r in records(site).items()}
check.ok(vdirsyncer("sync") == 0, "再同期: sync が通る")
check.ok({i: r["Title"] for i, r in records(site).items()} == before, "再同期: 何も変わらない（重複もしない）")

raise SystemExit(check.done())
