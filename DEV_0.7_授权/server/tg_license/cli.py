"""命令行子命令与运维入口（serve / keygen / keygen-master / import-* / list / show / revoke / extend / publish…）。"""
import argparse
import base64
import getpass
import json
import os
import re
import secrets
import shutil
import sys
import time
from datetime import datetime

from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import ec

from .admin_auth import hash_password
from .admin_web import AdminWeb
from .codec import (
    ALPHABET,
    PLANS,
    code_id,
    compose,
    date_text,
    display_id,
    normalize_id,
    parse_code,
    parse_date,
    parse_payload,
    stamp,
    unmask,
)
from .crypto import (
    load_master,
    load_server_key,
    load_signing_key,
    master_slot_source,
    public_key,
    same_public,
    save_signing_key,
    sign_p1363,
    slot_source,
    verify_p1363,
)
from .http_server import BoundedThreadingHTTPServer, make_handler
from .ledger import Ledger, expiry_day
from .service import Service
from .updates import Updates


def cmd_keygen(a):
    if os.path.exists(a.key) and not a.force:
        sys.exit("服务端私钥已存在：%s（换钥匙会让所有插件都连不上，确实要换请加 --force）" % a.key)
    key = ec.generate_private_key(ec.SECP256R1())
    pem = key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption())
    fd = os.open(a.key, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "wb") as f:
        f.write(pem)
    nums = key.public_key().public_numbers()
    x, y = nums.x.to_bytes(32, "big"), nums.y.to_bytes(32, "big")
    source = slot_source(a.url, a.key_id, x, y)
    with open(a.slot, "w", encoding="utf-8", newline="\n") as f:
        f.write(source)
    print("服务端私钥：%s（只留在服务器上，别提交、别外发）" % a.key)
    print("插件公钥槽：%s（覆盖到 src/License/LicenseServerSlot.cs 后重新编译插件）" % a.slot)


def cmd_import_master(a):
    if a.slot:
        with open(a.slot, encoding="utf-8-sig") as f:
            text = f.read()
        consts = dict(re.findall(r'(\w+)\s*=\s*"([^"]*)"', text))
        x = unmask(consts["MaskX"], consts["ValueX"], consts["SeedX"])
        y = unmask(consts["MaskY"], consts["ValueY"], consts["SeedY"])
        key_id = consts.get("KeyId", "master")
    elif a.x and a.y:
        x, y = base64.b64decode(a.x), base64.b64decode(a.y)
        key_id = a.key_id
    else:
        sys.exit("请用 --slot 指定 LicenseKeySlot.cs，或用 --x/--y 给出 TianGongLicenseAdmin.exe public 打印的坐标")
    public_key(x, y)  # 不在曲线上会抛错
    if key_id == "testmaster" and not a.allow_test:
        sys.exit("这是测试公钥（testmaster），生产服务器不应接受它签发的码；联调时加 --allow-test")
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump({"key_id": key_id, "x": x.hex(), "y": y.hex()}, f, indent=2)
    print("签发公钥已导入：%s（密钥ID %s）" % (a.out, key_id))


def cmd_serve(a):
    ledger = Ledger(a.db, a.default_quota)
    a._ledger = ledger
    master = load_master(a.master)
    service = Service(ledger, master, load_server_key(a.key), a.trust_proxy, a.rate)
    service.updates = Updates(a.updates, service.server_key, sign_p1363)
    admin = None
    if a.signing_key or a.admin:
        signing = None
        if a.signing_key:
            key_id, key = load_signing_key(a.signing_key)
            if not same_public(key.public_key(), master):
                sys.exit("签发私钥与 --master 公钥不配对：插件会拒绝这把私钥签出来的码。请重新 import-master。")
            signing = (key_id, key)
        if ledger.setting("admin_password") is None:
            print("提示：还没设置看板口令，先运行 set-password，否则无法登录。", flush=True)
        admin = AdminWeb(
            service,
            signing,
            secure_cookie=not a.insecure_cookie,
            max_issue_per_day=a.max_issue_per_day,
        )
    host, _, port = a.listen.rpartition(":")
    httpd = BoundedThreadingHTTPServer((host or "127.0.0.1", int(port)), make_handler(service, admin))
    print("授权服务已启动：http://%s/api/v1/license  台账 %s  默认换机次数 %d" % (a.listen, a.db, a.default_quota), flush=True)
    if admin:
        print(
            "管理看板：http://%s/admin/  %s" % (a.listen, "可签发（密钥ID %s）" % admin.key_id if admin.signing else "只管理、不签发"),
            flush=True,
        )
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass


def cmd_keygen_master(a):
    for path in (a.key, a.slot):
        if os.path.exists(path):
            sys.exit("文件已存在，拒绝覆盖：%s（换签发密钥 = 所有已发出的激活码作废）" % path)
    key = ec.generate_private_key(ec.SECP256R1())
    save_signing_key(a.key, a.key_id, key)
    nums = key.public_key().public_numbers()
    x, y = nums.x.to_bytes(32, "big"), nums.y.to_bytes(32, "big")
    with open(a.slot, "w", encoding="utf-8", newline="\n") as f:
        f.write(master_slot_source(a.key_id, x, y))
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump({"key_id": a.key_id, "x": x.hex(), "y": y.hex()}, f, indent=2)
    print("签发私钥：%s（权限 600；离线再备份一份，丢了就再也签不出能用的码）" % a.key)
    print("插件公钥槽：%s（覆盖到 src/License/LicenseKeySlot.cs 后重新编译插件）" % a.slot)
    print("服务器验码公钥：%s（serve --master 用它）" % a.out)


def cmd_set_password(a):
    first = getpass.getpass("新的看板口令（至少 10 位）：")
    if len(first) < 10:
        sys.exit("口令太短")
    if getpass.getpass("再输一次：") != first:
        sys.exit("两次输入不一致")
    ledger = Ledger(a.db, a.default_quota)
    a._ledger = ledger
    ledger.set_setting("admin_password", hash_password(first))
    # 保留完整浮点时间戳，避免 int() 截断导致同一秒内刚签发的会话逃逸失效
    ledger.set_setting("session_epoch", repr(time.time()))
    ledger.audit("cli", "set-password")
    print("口令已设置。正在运行的服务会在下一次请求时让旧会话全部失效。")


def cmd_import_ledger(a):
    master = load_master(a.master)
    ledger = Ledger(a.db, a.default_quota)
    a._ledger = ledger
    added = updated = bad = 0
    with open(a.tsv, encoding="utf-8-sig") as f:
        header = f.readline().rstrip("\r\n").split("\t")
        for line in f:
            cells = dict(zip(header, line.rstrip("\r\n").split("\t")))
            parsed = parse_code(cells.get("code", ""))
            if parsed is None or not verify_p1363(master, parsed[0], parsed[1]):
                bad += 1
                print("跳过（码无效或不是这把签发密钥签的）：%s" % cells.get("codeId", "?"))
                continue
            payload = parsed[0]
            cid = code_id(payload)
            info = parse_payload(payload)
            text = compose(payload, parsed[1])
            if ledger.add_issued(cid, info["plan"], info["day"], text, None, cells.get("note") or None, "import", "import"):
                added += 1
            else:
                updated += 1
            if cells.get("status") == "void":
                ledger.execute("UPDATE codes SET revoked=1 WHERE code_id=?", (cid,))
    ledger.audit("cli", "import-ledger", detail="%s 新增 %d 更新 %d 跳过 %d" % (os.path.basename(a.tsv), added, updated, bad))
    print("导入完成：新增 %d，已有记录补全 %d，跳过 %d" % (added, updated, bad))


def open_ledger(a):
    if not os.path.exists(a.db):
        sys.exit("台账不存在：" + a.db)
    a._ledger = Ledger(a.db, a.default_quota)   # main() 跑完命令统一关掉
    return a._ledger


def describe(ledger, row):
    plan = PLANS.get(row["plan"], ("?", 0))
    expiry = ""
    day = expiry_day(row)
    if day is not None:
        expiry = date_text(day) + ("（延长 %+d 天）" % row["extra_days"] if row["extra_days"] else "")
    state = "作废" if row["revoked"] else ("已绑定" if row["machine"] else "未绑定")
    quota = ledger.quota_of(row)
    return "%s  %-4s  %-6s 到期 %-10s  机器 %-10s  换机 %d/%d  最近 %s  %s" % (
        display_id(row["code_id"]), state, plan[0], expiry, row["machine"] or "-",
        row["rebinds_used"], quota, row["last_seen"] or "-", row["note"] or "",
    )


def require_row(ledger, text, create=False):
    cid = normalize_id(text)
    if len(cid) != 8 or any(c not in ALPHABET for c in cid):
        sys.exit("码ID 应为 8 个字符（形如 abcd-efgh）：" + text)
    row = ledger.row(cid)
    if row is None and create:
        ledger.db.execute("INSERT INTO codes(code_id) VALUES(?)", (cid,))
        row = ledger.row(cid)
    if row is None:
        sys.exit("服务器上没有这个码的记录（客户还没联网激活过）：" + display_id(cid))
    return cid, row


def cmd_list(a):
    ledger = open_ledger(a)
    rows = ledger.db.execute("SELECT * FROM codes ORDER BY first_seen DESC").fetchall()
    for row in rows:
        print(describe(ledger, row))
    print("共 %d 个码" % len(rows))


def cmd_show(a):
    ledger = open_ledger(a)
    cid, row = require_row(ledger, a.code)
    print(describe(ledger, row))
    for ev in ledger.db.execute("SELECT * FROM events WHERE code_id=? ORDER BY id DESC LIMIT 20", (cid,)):
        print("  %s  %-8s %-7s %-7s 机器 %s  IP %s" % (ev["at"], ev["op"], ev["status"], ev["result"], ev["machine"], ev["ip"]))


def cmd_revoke(a):
    ledger = open_ledger(a)
    cid, _ = require_row(ledger, a.code, create=True)  # 没见过的码也能提前作废
    ledger.execute("UPDATE codes SET revoked=1, note=COALESCE(?, note) WHERE code_id=?", (a.note, cid))
    ledger.audit("cli", "revoke", cid, a.note)
    print("已作废 %s：已激活的机器最迟在下一次联网复核（约 1 天内）时失效" % display_id(cid))


def cmd_unrevoke(a):
    ledger = open_ledger(a)
    cid, _ = require_row(ledger, a.code)
    ledger.execute("UPDATE codes SET revoked=0 WHERE code_id=?", (cid,))
    ledger.audit("cli", "unrevoke", cid)
    print("已恢复 %s" % display_id(cid))


def cmd_unbind(a):
    ledger = open_ledger(a)
    cid, row = require_row(ledger, a.code)
    before = row["machine"]
    ledger.execute("UPDATE codes SET machine=NULL, bound_at=NULL, unbound_at=? WHERE code_id=?", (stamp(), cid))
    ledger.audit("cli", "unbind", cid, before)
    print("已解绑 %s：下一台输入这个码的机器直接绑定，不占换机次数；旧机器下次复核后停用" % display_id(cid))


def cmd_quota(a):
    ledger = open_ledger(a)
    cid, _ = require_row(ledger, a.code)
    if a.reset:
        ledger.execute("UPDATE codes SET rebinds_used=0 WHERE code_id=?", (cid,))
    if a.count is not None:
        ledger.execute("UPDATE codes SET quota=? WHERE code_id=?", (a.count, cid))
    ledger.audit("cli", "quota", cid, json.dumps({"quota": a.count, "reset": bool(a.reset)}))
    print(describe(ledger, ledger.row(cid)))


def cmd_extend(a):
    ledger = open_ledger(a)
    cid, row = require_row(ledger, a.code, create=True)   # 还没联网过的码也能先延长
    if a.to:
        if expiry_day(row) is None:
            sys.exit("服务器还没见过这个码的载荷（客户没联网激活过），没法按日期算；请用天数：extend %s 30" % display_id(cid))
        extra = (row["extra_days"] or 0) + parse_date(a.to) - expiry_day(row)
    elif a.days is not None:
        extra = (row["extra_days"] or 0) + a.days
    else:
        sys.exit("要么给延长天数（负数 = 缩短），要么给 --to YYYY-MM-DD")
    ledger.execute("UPDATE codes SET extra_days=? WHERE code_id=?", (extra, cid))
    ledger.audit("cli", "extend", cid, "累计 %+d 天" % extra)
    print(describe(ledger, ledger.row(cid)))
    print("客户的机器下次联网复核（约 1 天内；到期当天打开插件会立刻复核）就按新到期日算，不用换码")


def cmd_publish(a):
    """在服务器上发布一个版本（安装包已经传到服务器上时用；平时用看板「版本发布」或本机 tools/publish-update.ps1）"""
    notes = a.notes or ""
    if a.notes_file:
        with open(a.notes_file, encoding="utf-8-sig") as f:
            notes = f.read()
    updates = Updates(a.updates, load_server_key(a.key), sign_p1363)
    tmp = os.path.join(updates.folder, ".cli-upload-%d-%s.tmp" % (os.getpid(), secrets.token_hex(4)))
    try:
        # 拷贝到临时副本后再走流式 publish_file，避免把大包整包读入内存且不移动源文件
        shutil.copyfile(a.zip, tmp)
        m = updates.publish_file(tmp, os.path.basename(a.zip), a.version, notes, by="cli")
    except ValueError as e:
        sys.exit("发布失败：" + str(e))
    finally:
        if os.path.exists(tmp):
            try:
                os.remove(tmp)
            except OSError:
                pass
    print("已发布 %s（%s，%.1f MB，sha256 %s…）。插件下次启动后检查更新就会提示。" % (
        m["version"], m["file"], m["size"] / 1048576.0, m["sha256"][:12]
    ))


def cmd_releases(a):
    updates = Updates(a.updates, None, None)
    for row in updates.history(a.limit):
        first_line = (row.get("notes") or "").splitlines()[0][:60] if row.get("notes") else ""
        print("%s  %-8s %s  %s" % (row.get("at"), row.get("version"), row.get("file"), first_line))


def cmd_events(a):
    ledger = open_ledger(a)
    for ev in ledger.db.execute("SELECT * FROM events ORDER BY id DESC LIMIT ?", (a.limit,)):
        print("%s  %s  %-8s %-7s %-7s 机器 %s  IP %s" % (
            ev["at"], display_id(ev["code_id"]), ev["op"], ev["status"], ev["result"], ev["machine"], ev["ip"]
        ))


def build_parser():
    p = argparse.ArgumentParser(description="天工工具箱 联网授权服务端")
    p.add_argument("--db", default=os.environ.get("TG_LICENSE_DB", "license.db"), help="台账 SQLite 文件")
    p.add_argument("--default-quota", type=int, default=int(os.environ.get("TG_LICENSE_QUOTA", "2")),
                   help="每个码默认允许的自助换机次数（默认 2）")
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("keygen", help="生成服务端回执密钥，并写出插件公钥槽")
    s.add_argument("--key", default="server.key")
    s.add_argument("--url", required=True, help="插件访问的完整地址，如 https://license.example.com/api/v1/license")
    s.add_argument("--slot", default="LicenseServerSlot.cs")
    s.add_argument("--key-id", default="server-" + datetime.now().strftime("%Y%m%d"))
    s.add_argument("--force", action="store_true")
    s.set_defaults(func=cmd_keygen)

    s = sub.add_parser("keygen-master", help="生成激活码签发密钥（.tgkey）+ 插件公钥槽 + 服务器验码公钥")
    s.add_argument("--key", default="master.tgkey")
    s.add_argument("--key-id", default="master")
    s.add_argument("--slot", default="LicenseKeySlot.cs")
    s.add_argument("--out", default="master.pub.json")
    s.set_defaults(func=cmd_keygen_master)

    sub.add_parser("set-password", help="设置网页看板的管理员口令").set_defaults(func=cmd_set_password)

    s = sub.add_parser("import-ledger", help="导入管理员工具的台账 *.ledger.tsv")
    s.add_argument("tsv")
    s.add_argument("--master", default="master.pub.json")
    s.set_defaults(func=cmd_import_ledger)

    s = sub.add_parser("import-master", help="导入激活码签发公钥")
    s.add_argument("--slot", help="插件源码里的 src/License/LicenseKeySlot.cs")
    s.add_argument("--x", help="TianGongLicenseAdmin.exe public 打印的 X（base64）")
    s.add_argument("--y", help="同上 Y")
    s.add_argument("--key-id", default="master")
    s.add_argument("--out", default="master.pub.json")
    s.add_argument("--allow-test", action="store_true")
    s.set_defaults(func=cmd_import_master)

    s = sub.add_parser("serve", help="启动 HTTP 服务（放在 HTTPS 反向代理后面）")
    s.add_argument("--listen", default="127.0.0.1:8750")
    s.add_argument("--master", default="master.pub.json")
    s.add_argument("--key", default="server.key")
    s.add_argument("--trust-proxy", action="store_true", help="用 X-Forwarded-For 识别客户端 IP（只在反代后面开）")
    s.add_argument("--rate", type=int, default=30, help="每个 IP 每分钟最多请求数")
    s.add_argument("--signing-key", help="签发私钥 .tgkey：给了就在 /admin 开放看板并允许网页签发")
    s.add_argument("--admin", action="store_true", help="只开看板（管理、不签发），不需要签发私钥")
    s.add_argument("--max-issue-per-day", type=int, default=50, help="网页每天最多签发多少个码（防口令泄露后被刷）")
    s.add_argument("--insecure-cookie", action="store_true", help="本机 http 调试用：会话 Cookie 不加 Secure")
    s.add_argument("--updates", default="updates", help="版本发布目录（安装包 + 签名清单）")
    s.set_defaults(func=cmd_serve)

    sub.add_parser("list", help="列出所有码").set_defaults(func=cmd_list)
    s = sub.add_parser("show", help="查看一个码与最近事件")
    s.add_argument("code")
    s.set_defaults(func=cmd_show)
    s = sub.add_parser("revoke", help="作废一个码（即时生效，不用发新版本）")
    s.add_argument("code")
    s.add_argument("--note")
    s.set_defaults(func=cmd_revoke)
    s = sub.add_parser("unrevoke", help="撤销作废")
    s.add_argument("code")
    s.set_defaults(func=cmd_unrevoke)
    s = sub.add_parser("unbind", help="解绑（客户旧电脑坏了等情况，下一台机器直接绑定、不占次数）")
    s.add_argument("code")
    s.set_defaults(func=cmd_unbind)
    s = sub.add_parser("quota", help="调整一个码的换机次数")
    s.add_argument("code")
    s.add_argument("count", type=int, nargs="?")
    s.add_argument("--reset", action="store_true", help="把已用次数清零")
    s.set_defaults(func=cmd_quota)
    s = sub.add_parser("extend", help="延长（或缩短）一个码的有效期，不用换码")
    s.add_argument("code")
    s.add_argument("days", type=int, nargs="?", help="延长天数，负数 = 缩短")
    s.add_argument("--to", help="直接改到这一天到期（YYYY-MM-DD）")
    s.set_defaults(func=cmd_extend)
    s = sub.add_parser("publish", help="发布新版本：插件启动后会提示用户更新")
    s.add_argument("zip", help="make-installer.ps1 打出来的安装包 zip")
    s.add_argument("--version", help="版本号（默认从文件名里的 DEV0.9.2 认）")
    s.add_argument("--notes", help="更新说明（显示给用户）")
    s.add_argument("--notes-file", help="更新说明文件（UTF-8）")
    s.add_argument("--updates", default="updates")
    s.add_argument("--key", default="server.key", help="服务端回执私钥（清单用它签名）")
    s.set_defaults(func=cmd_publish)
    s = sub.add_parser("releases", help="发布记录")
    s.add_argument("--updates", default="updates")
    s.add_argument("--limit", type=int, default=20)
    s.set_defaults(func=cmd_releases)
    s = sub.add_parser("events", help="最近的激活/复核记录")
    s.add_argument("--limit", type=int, default=50)
    s.set_defaults(func=cmd_events)
    return p


def main(argv=None):
    p = build_parser()
    a = p.parse_args(argv)
    try:
        a.func(a)
    finally:
        # 命令行跑完关掉台账连接：Windows 上不关，台账文件删不掉 / 挪不动
        ledger = getattr(a, "_ledger", None)
        if ledger is not None:
            ledger.db.close()
