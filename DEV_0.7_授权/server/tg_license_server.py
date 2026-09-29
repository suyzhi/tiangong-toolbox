#!/usr/bin/env python3
"""天工工具箱 联网授权服务端（DEV 0.8）。

子命令：
  serve          HTTP 服务：插件激活/复核时来这里登记"码 → 机器"，拿回一张签名回执；
                 加 --signing-key 时同时提供 /admin 网页看板（管理 + 签发，见 tg_license_admin.py）
  keygen         生成服务端回执密钥，并写出插件里的 LicenseServerSlot.cs
  keygen-master  生成激活码签发密钥（.tgkey，与管理员工具同格式）+ 插件公钥槽 LicenseKeySlot.cs
  import-master  导入激活码的签发公钥（只验码、不签发时用）
  set-password   设置网页看板的管理员口令
  import-ledger  把管理员工具的台账（*.ledger.tsv）导入服务器，旧码也能在看板里管理
  list/show/revoke/unrevoke/unbind/quota/events   命令行维护台账

依赖：Python 3.8+，pip install cryptography。数据存在一个 SQLite 文件里。
对外必须套一层 HTTPS 反向代理（Caddy / nginx），服务本身只监听 127.0.0.1。

安全模型：
  - 回执用"服务端私钥"签：插件只认内嵌服务端公钥签过的回执，伪造服务器/中间人改包都没用。
  - 回执带插件随机生成的 nonce，旧回执不能重放给新请求。
  - 签发私钥放在服务器上（用户选择的方案）：服务器被攻破 = 别人能签任意激活码。
    所以看板有口令、登录限速、会话过期、CSRF 校验、每日签发上限和审计日志；不开 --signing-key 就只验不签。
"""
import argparse
import base64
import hashlib
import hmac
import json
import os
import re
import sqlite3
import sys
import threading
import time
from collections import defaultdict, deque
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.asymmetric.utils import decode_dss_signature, encode_dss_signature

ALPHABET = "0123456789abcdefghjkmnpqrstvwxyz"
EPOCH = datetime(2020, 1, 1, tzinfo=timezone.utc).timestamp()
# 与 src/License/LicensePlans.cs 一致：档位编号 → (名称, 天数)
PLANS = {11: ("一个月", 30), 27: ("半年", 183), 42: ("一年", 365)}
PAYLOAD_SIZE = 27
MAX_BODY = 4096
HEX = re.compile(r"^[0-9a-f]+$")


# ---------------------------------------------------------------- 格式（与插件 C# 实现逐字节一致）

def day_offset(now=None):
    """距 2020-01-01 UTC 的天数，对应 LicenseTime.ToDayOffset。"""
    days = ((time.time() if now is None else now) - EPOCH) / 86400.0
    return max(int(days // 1), 0)


def encode_group(five):
    """5 字节 → 8 个字符，对应 LicenseCodec.EncodeGroup。"""
    word = int.from_bytes(five, "big")
    return "".join(ALPHABET[(word >> (5 * (7 - i))) & 31] for i in range(8))


def code_id(payload):
    """码ID = 载荷 SHA-256 前 5 字节，对应 LicenseCodec.IdOf。"""
    return encode_group(hashlib.sha256(payload).digest()[:5])


def normalize_id(text):
    """对应 LicenseCodec.NormalizeId：去分隔符、转小写、纠正 o→0、i/l→1。"""
    out = []
    for c in (text or "").strip():
        if c in "-_" or c.isspace():
            continue
        c = c.lower()
        c = "0" if c == "o" else ("1" if c in "il" else c)
        out.append(c)
    return "".join(out)


def display_id(cid):
    return cid[:4] + "-" + cid[4:] if cid and len(cid) > 4 else (cid or "")


def parse_payload(payload):
    """对应 LicensePayload.FromBytes；格式不对返回 None。"""
    if len(payload) != PAYLOAD_SIZE or payload[1:3] != b"TG" or payload[3] != 1:
        return None
    return {
        "flags": payload[0],
        "plan": payload[4],
        "day": int.from_bytes(payload[15:19], "big", signed=True),
    }


def _stream(seed, length):
    out = b""
    counter = 0
    while len(out) < length:
        out += hashlib.sha256(seed.encode("ascii") + counter.to_bytes(4, "big")).digest()
        counter += 1
    return out[:length]


def unmask(mask_hex, value_b64, seed):
    """对应 LicenseKeyMaterial.Unmask：从 LicenseKeySlot.cs 的掩码常量还原公钥坐标。"""
    value = base64.b64decode(value_b64)
    mask = bytes.fromhex(mask_hex)
    if len(value) != 32 or len(mask) != 32:
        raise ValueError("公钥槽常量长度不对")
    stream = _stream(seed, 32)
    return bytes(v ^ s ^ m for v, s, m in zip(value, stream, mask))


def public_key(x, y):
    """坐标 → P-256 公钥；点不在曲线上会抛 ValueError（顺带验证了还原结果）。"""
    return ec.EllipticCurvePublicNumbers(
        int.from_bytes(x, "big"), int.from_bytes(y, "big"), ec.SECP256R1()
    ).public_key()


def verify_p1363(key, data, signature):
    if len(signature) != 64:
        return False
    der = encode_dss_signature(int.from_bytes(signature[:32], "big"), int.from_bytes(signature[32:], "big"))
    try:
        key.verify(der, data, ec.ECDSA(hashes.SHA256()))
        return True
    except InvalidSignature:
        return False


def sign_p1363(key, data):
    r, s = decode_dss_signature(key.sign(data, ec.ECDSA(hashes.SHA256())))
    return r.to_bytes(32, "big") + s.to_bytes(32, "big")


# ---------------------------------------------------------------- 激活码全文（对应 LicenseCodec.Compose / Parse）

CHECK_SALT = b"TGS-CHK-2026-01"
BODY_GROUPS = 19
CODE_CHARS = (BODY_GROUPS + 1) * 8
PLAN_LETTERS = {"M": 11, "H": 27, "Y": 42}
LETTER_OF = {v: k for k, v in PLAN_LETTERS.items()}


def _checksum(body):
    return hmac.new(CHECK_SALT, body, hashlib.sha256).digest()[:5]


def compose(payload, signature):
    """载荷 + 签名 → 160 字符激活码（每 5 字符一个 '-'）。"""
    body = payload + signature
    padded = body + bytes(BODY_GROUPS * 5 - len(body))
    text = "".join(encode_group(padded[g * 5:g * 5 + 5]) for g in range(BODY_GROUPS)) + encode_group(_checksum(body))
    return "-".join(text[i:i + 5] for i in range(0, len(text), 5))


def _decode_group(chars):
    word = 0
    for c in chars:
        word = (word << 5) | ALPHABET.index(c)
    return word.to_bytes(5, "big")


def parse_code(text):
    """激活码全文 → (payload, signature)；格式或抄写校验不对返回 None。宽容规则与插件一致。"""
    chars = []
    for c in text or "":
        if len(chars) == CODE_CHARS:
            break
        if c in "-_　" or c.isspace():
            continue
        if "A" <= c <= "Z":
            c = c.lower()
        c = "0" if c == "o" else ("1" if c in "il" else c)
        if c not in ALPHABET:
            return None
        chars.append(c)
    if len(chars) != CODE_CHARS:
        return None
    body = b"".join(_decode_group(chars[g * 8:g * 8 + 8]) for g in range(BODY_GROUPS))[:PAYLOAD_SIZE + 64]
    if _decode_group(chars[BODY_GROUPS * 8:]) != _checksum(body):
        return None
    payload, signature = body[:PAYLOAD_SIZE], body[PAYLOAD_SIZE:]
    info = parse_payload(payload)
    if info is None or info["plan"] not in PLANS:
        return None
    return payload, signature


def new_payload(plan, day):
    """未绑定机器的通用码载荷（对应 LicenseAdminKey.Create 不带机器码的分支）。"""
    return bytes([0]) + b"TG" + bytes([1, plan]) + os.urandom(10) + day.to_bytes(4, "big") + os.urandom(8)


# ---------------------------------------------------------------- 签发私钥（.tgkey，与 TianGongLicenseAdmin 同格式）

TGKEY_MAGIC = "TGLK-1"


def load_signing_key(path):
    """读 .tgkey：TGLK-1 / 密钥ID / base64(CNG ECCPRIVATEBLOB 104 字节 + X + Y)。返回 (密钥ID, 私钥)。"""
    with open(path, encoding="utf-8-sig") as f:
        lines = f.read().split("\n")
    if len(lines) < 3 or lines[0].strip() != TGKEY_MAGIC:
        raise ValueError("签发私钥文件格式不对：" + path)
    blob = base64.b64decode(lines[2].strip())[:104]
    if len(blob) != 104 or blob[:4] != b"ECS2" or int.from_bytes(blob[4:8], "little") != 32:
        raise ValueError("签发私钥文件内容不对：" + path)
    key = ec.derive_private_key(int.from_bytes(blob[72:104], "big"), ec.SECP256R1())
    nums = key.public_key().public_numbers()
    if nums.x.to_bytes(32, "big") != blob[8:40] or nums.y.to_bytes(32, "big") != blob[40:72]:
        raise ValueError("签发私钥文件里的公钥与私钥不配对：" + path)
    return lines[1].strip() or "master", key


def save_signing_key(path, key_id, key):
    nums = key.public_key().public_numbers()
    x, y = nums.x.to_bytes(32, "big"), nums.y.to_bytes(32, "big")
    d = key.private_numbers().private_value.to_bytes(32, "big")
    blob = b"ECS2" + (32).to_bytes(4, "little") + x + y + d
    text = TGKEY_MAGIC + "\n" + key_id + "\n" + base64.b64encode(blob + x + y).decode("ascii") + "\n"
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def master_slot_source(key_id, x, y):
    """插件公钥槽 LicenseKeySlot.cs，与 TianGongLicenseAdmin keygen 输出逐字一致。"""
    seed_a, seed_b = "TGS-SLOT-A-" + key_id, "TGS-SLOT-B-" + key_id
    return (
        "// 由 TianGongLicenseAdmin keygen 生成，请勿手工修改。密钥ID：" + key_id + "\n"
        "// 这是公钥，可以随源码一起提交；私钥在同名的 .tgkey 文件里，绝不能提交。\n"
        "namespace TianGongCadSuite.Licensing {\n"
        "    internal static class LicenseKeySlot {\n"
        '        internal const string KeyId = "' + key_id + '";\n'
        '        internal static readonly string MaskX = "' + _stream(seed_a, 32).hex() + '";\n'
        '        internal static readonly string MaskY = "' + _stream(seed_b, 32).hex() + '";\n'
        '        internal static readonly string SeedX = "' + seed_a + '";\n'
        '        internal static readonly string SeedY = "' + seed_b + '";\n'
        '        internal static readonly string ValueX = "' + base64.b64encode(x).decode("ascii") + '";\n'
        '        internal static readonly string ValueY = "' + base64.b64encode(y).decode("ascii") + '";\n'
        "    }\n"
        "}\n"
    )


def same_public(a, b):
    return a.public_numbers() == b.public_numbers()


# ---------------------------------------------------------------- 密钥文件

def load_master(path):
    with open(path, encoding="utf-8") as f:
        info = json.load(f)
    return public_key(bytes.fromhex(info["x"]), bytes.fromhex(info["y"]))


def load_server_key(path):
    with open(path, "rb") as f:
        return serialization.load_pem_private_key(f.read(), password=None)


def slot_source(url, key_id, x, y):
    return (
        "// 由 server/tg_license_server.py keygen 生成，请勿手工修改。密钥ID：" + key_id + "\n"
        "// 联网授权：服务端地址 + 服务端回执公钥。公钥可以提交；服务端私钥只放在服务器上。\n"
        "namespace TianGongCadSuite.Licensing {\n"
        "    internal static class LicenseServerSlot {\n"
        '        internal const string KeyId = "' + key_id + '";\n'
        '        internal const string Url = "' + url + '";\n'
        '        internal const string KeyX = "' + x.hex() + '";\n'
        '        internal const string KeyY = "' + y.hex() + '";\n'
        "    }\n"
        "}\n"
    )


# ---------------------------------------------------------------- 台账（SQLite）

SCHEMA = """
CREATE TABLE IF NOT EXISTS codes(
    code_id      TEXT PRIMARY KEY,
    plan         INTEGER,
    issue_day    INTEGER,
    machine      TEXT,
    bound_at     TEXT,
    last_seen    TEXT,
    last_ip      TEXT,
    rebinds_used INTEGER NOT NULL DEFAULT 0,
    quota        INTEGER,
    revoked      INTEGER NOT NULL DEFAULT 0,
    note         TEXT,
    first_seen   TEXT
);
CREATE TABLE IF NOT EXISTS events(
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    at      TEXT,
    code_id TEXT,
    machine TEXT,
    op      TEXT,
    status  TEXT,
    result  TEXT,
    ip      TEXT
);
CREATE INDEX IF NOT EXISTS events_code ON events(code_id);
CREATE TABLE IF NOT EXISTS audit(
    id      INTEGER PRIMARY KEY AUTOINCREMENT,
    at      TEXT,
    ip      TEXT,
    action  TEXT,
    code_id TEXT,
    detail  TEXT
);
CREATE TABLE IF NOT EXISTS settings(
    name  TEXT PRIMARY KEY,
    value TEXT
);
"""

# 0.8 看板新增的列：旧库启动时自动补上（ALTER TABLE ADD COLUMN，不动已有数据）
CODE_COLUMNS = {
    "code_text": "TEXT",   # 激活码全文（看板签发 / 导入台账时才有）
    "customer": "TEXT",
    "issued_at": "TEXT",
    "issued_by": "TEXT",   # 签发时的登录 IP / "import"
    "source": "TEXT",      # web / import / NULL（客户端首次联网时才见到）
}


def stamp():
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


class Ledger:
    def __init__(self, path, default_quota=2):
        self.path = path
        self.default_quota = default_quota
        # 一个连接多个线程共用：所有读写都要拿这把锁，否则看板的写入会混进插件请求的事务里
        self.lock = threading.RLock()
        self.db = sqlite3.connect(path, check_same_thread=False, isolation_level=None)
        self.db.row_factory = sqlite3.Row
        self.db.execute("PRAGMA journal_mode=WAL")
        self.db.executescript(SCHEMA)
        have = {r["name"] for r in self.db.execute("PRAGMA table_info(codes)")}
        for name, kind in CODE_COLUMNS.items():
            if name not in have:
                self.db.execute("ALTER TABLE codes ADD COLUMN %s %s" % (name, kind))

    def query(self, sql, args=()):
        with self.lock:
            return self.db.execute(sql, args).fetchall()

    def execute(self, sql, args=()):
        with self.lock:
            self.db.execute(sql, args)

    def setting(self, name):
        rows = self.query("SELECT value FROM settings WHERE name=?", (name,))
        return rows[0]["value"] if rows else None

    def set_setting(self, name, value):
        self.execute("INSERT INTO settings(name,value) VALUES(?,?) ON CONFLICT(name) DO UPDATE SET value=excluded.value",
                        (name, value))

    def audit(self, ip, action, cid=None, detail=None):
        self.execute("INSERT INTO audit(at,ip,action,code_id,detail) VALUES(?,?,?,?,?)", (stamp(), ip, action, cid, detail))

    def add_issued(self, cid, plan, day, text, customer, note, by, source):
        """记下一个签发/导入的码。已存在（客户端先联网过）就只补全文与客户信息。"""
        with self.lock:
            now = stamp()
            if self.row(cid) is None:
                self.db.execute(
                    "INSERT INTO codes(code_id,plan,issue_day,code_text,customer,note,issued_at,issued_by,source,first_seen)"
                    " VALUES(?,?,?,?,?,?,?,?,?,?)", (cid, plan, day, text, customer, note, now, by, source, now))
                return True
            self.db.execute(
                "UPDATE codes SET plan=COALESCE(plan,?), issue_day=COALESCE(issue_day,?), code_text=COALESCE(code_text,?),"
                " customer=COALESCE(customer,?), note=COALESCE(note,?), issued_at=COALESCE(issued_at,?),"
                " issued_by=COALESCE(issued_by,?), source=COALESCE(source,?) WHERE code_id=?",
                (plan, day, text, customer, note, now, by, source, cid))
            return False

    def row(self, cid):
        with self.lock:
            return self.db.execute("SELECT * FROM codes WHERE code_id=?", (cid,)).fetchone()

    def quota_of(self, row):
        return self.default_quota if row["quota"] is None else row["quota"]

    def decide(self, op, cid, plan, issue_day, machine, ip, today):
        """核心规则。返回 (status, result, 剩余换机次数, 总次数)。

        status: ok / revoked / expired / moved / quota
        result: bound（首次绑定）/ already（本机重复）/ rebound（从别的机器换过来）
        check  只复核：码在别的机器上 → moved，不会自动换绑
        activate 用户主动输码：码在别的机器上 → 有换机次数就换过来，否则 quota
        """
        with self.lock:
            self.db.execute("BEGIN IMMEDIATE")
            try:
                outcome = self._decide(op, cid, plan, issue_day, machine, ip, today)
                self.db.execute(
                    "INSERT INTO events(at,code_id,machine,op,status,result,ip) VALUES(?,?,?,?,?,?,?)",
                    (stamp(), cid, machine, op, outcome[0], outcome[1], ip),
                )
                self.db.execute("COMMIT")
                return outcome
            except Exception:
                self.db.execute("ROLLBACK")
                raise

    def _decide(self, op, cid, plan, issue_day, machine, ip, today):
        now = stamp()
        row = self.row(cid)
        if row is None:
            self.db.execute(
                "INSERT INTO codes(code_id,plan,issue_day,first_seen) VALUES(?,?,?,?)", (cid, plan, issue_day, now)
            )
        elif row["plan"] is None:
            # 管理员提前作废过、服务器第一次见到这个码：补上档位信息
            self.db.execute("UPDATE codes SET plan=?, issue_day=?, first_seen=? WHERE code_id=?", (plan, issue_day, now, cid))
        row = self.row(cid)
        quota = self.quota_of(row)
        left = max(quota - row["rebinds_used"], 0)

        if row["revoked"]:
            return "revoked", "", left, quota
        if today >= issue_day + PLANS[plan][1]:
            return "expired", "", left, quota

        if row["machine"] is None:
            self.db.execute(
                "UPDATE codes SET machine=?, bound_at=?, last_seen=?, last_ip=? WHERE code_id=?", (machine, now, now, ip, cid)
            )
            return "ok", "bound", left, quota
        if row["machine"] == machine:
            self.db.execute("UPDATE codes SET last_seen=?, last_ip=? WHERE code_id=?", (now, ip, cid))
            return "ok", "already", left, quota
        if op == "check":
            return "moved", "", left, quota
        if left <= 0:
            return "quota", "", 0, quota
        self.db.execute(
            "UPDATE codes SET machine=?, bound_at=?, last_seen=?, last_ip=?, rebinds_used=rebinds_used+1 WHERE code_id=?",
            (machine, now, now, ip, cid),
        )
        return "ok", "rebound", left - 1, quota


# ---------------------------------------------------------------- HTTP 服务

class Service:
    def __init__(self, ledger, master, server_key, trust_proxy=False, rate_per_minute=30):
        self.ledger = ledger
        self.master = master
        self.server_key = server_key
        self.trust_proxy = trust_proxy
        self.rate = rate_per_minute
        self.hits = defaultdict(deque)
        self.hits_lock = threading.Lock()
        self.clock = time.time  # 测试可替换

    def allow(self, ip):
        now = time.monotonic()
        with self.hits_lock:
            q = self.hits[ip]
            while q and now - q[0] > 60:
                q.popleft()
            if len(q) >= self.rate:
                return False
            q.append(now)
            return True

    def handle(self, body, ip):
        """返回 (HTTP 状态码, dict)。拒绝类错误不签名；业务结论一律签名回执。"""
        try:
            req = json.loads(body.decode("utf-8"))
        except (ValueError, UnicodeDecodeError):
            return 400, {"error": "请求不是合法 JSON"}
        if not isinstance(req, dict) or req.get("v") != 1:
            return 400, {"error": "协议版本不对，请更新插件"}
        op = req.get("op")
        if op not in ("activate", "check"):
            return 400, {"error": "未知操作"}
        fields = {}
        for name, size in (("payload", PAYLOAD_SIZE * 2), ("sig", 128), ("machine", 10), ("nonce", 32)):
            value = req.get(name)
            if not isinstance(value, str) or len(value) != size or not HEX.match(value):
                return 400, {"error": "字段 " + name + " 格式不对"}
            fields[name] = value
        payload = bytes.fromhex(fields["payload"])
        info = parse_payload(payload)
        if info is None or info["plan"] not in PLANS:
            return 400, {"error": "激活码载荷无法识别"}
        if not verify_p1363(self.master, payload, bytes.fromhex(fields["sig"])):
            return 403, {"error": "激活码签名无效"}
        cid = code_id(payload)
        machine = fields["machine"]
        if info["flags"] & 1 and payload[5:10].hex() != machine:
            return 403, {"error": "该激活码是为另一台机器签发的"}

        now = self.clock()
        today = day_offset(now)
        status, result, left, quota = self.ledger.decide(op, cid, info["plan"], info["day"], machine, ip, today)
        receipt = {
            "v": 1, "code": cid, "machine": machine, "nonce": fields["nonce"], "op": op,
            "status": status, "result": result, "left": left, "quota": quota,
            "day": today, "time": int(now),
        }
        raw = json.dumps(receipt, separators=(",", ":"), ensure_ascii=True).encode("ascii")
        return 200, {
            "receipt": base64.b64encode(raw).decode("ascii"),
            "sig": base64.b64encode(sign_p1363(self.server_key, raw)).decode("ascii"),
        }


def make_handler(service, admin=None):
    class Handler(BaseHTTPRequestHandler):
        server_version = "TianGongLicense/1"
        sys_version = ""

        def client_ip(self):
            if service.trust_proxy:
                forwarded = self.headers.get("X-Forwarded-For", "")
                if forwarded:
                    return forwarded.split(",")[0].strip()
            return self.client_address[0]

        def reply(self, code, obj):
            data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
            self.send_response(code)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(data)

        def send_raw(self, code, headers, data):
            self.send_response(code)
            for name, value in headers:
                self.send_header(name, value)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def to_admin(self, method):
            # 网页看板：没开 --signing-key / --admin 时整段不存在
            if admin is None:
                return self.reply(404, {"error": "not found"})
            body = b""
            if method == "POST":
                try:
                    length = int(self.headers.get("Content-Length", "0"))
                except ValueError:
                    length = -1
                if length < 0 or length > MAX_BODY * 4:
                    return self.reply(413, {"error": "请求体大小不对"})
                body = self.rfile.read(length)
            try:
                code, headers, data = admin.handle(method, self.path, self.headers, body, self.client_ip())
            except Exception as e:
                sys.stderr.write("[%s] ADMIN ERROR %r\n" % (stamp(), e))
                return self.reply(500, {"error": "服务器内部错误"})
            self.send_raw(code, headers, data)

        def do_GET(self):
            if self.path == "/admin" or self.path.startswith("/admin/"):
                return self.to_admin("GET")
            if self.path == "/api/v1/health":
                return self.reply(200, {"ok": True, "day": day_offset(service.clock())})
            self.reply(404, {"error": "not found"})

        def do_POST(self):
            if self.path.startswith("/admin/"):
                return self.to_admin("POST")
            if self.path != "/api/v1/license":
                return self.reply(404, {"error": "not found"})
            ip = self.client_ip()
            if not service.allow(ip):
                return self.reply(429, {"error": "请求太频繁，请稍后再试"})
            try:
                length = int(self.headers.get("Content-Length", "0"))
            except ValueError:
                length = -1
            if length <= 0 or length > MAX_BODY:
                return self.reply(413 if length > MAX_BODY else 400, {"error": "请求体大小不对"})
            body = self.rfile.read(length)
            try:
                code, obj = service.handle(body, ip)
            except Exception as e:  # 不把内部异常细节回给客户端
                sys.stderr.write("[%s] ERROR %r\n" % (stamp(), e))
                code, obj = 500, {"error": "服务器内部错误"}
            self.reply(code, obj)

        def log_message(self, fmt, *args):
            sys.stderr.write("[%s] %s %s\n" % (stamp(), self.client_ip(), fmt % args))

    return Handler


# ---------------------------------------------------------------- 命令行

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
    master = load_master(a.master)
    service = Service(ledger, master, load_server_key(a.key), a.trust_proxy, a.rate)
    admin = None
    if a.signing_key or a.admin:
        import tg_license_admin
        signing = None
        if a.signing_key:
            key_id, key = load_signing_key(a.signing_key)
            if not same_public(key.public_key(), master):
                sys.exit("签发私钥与 --master 公钥不配对：插件会拒绝这把私钥签出来的码。请重新 import-master。")
            signing = (key_id, key)
        if ledger.setting("admin_password") is None:
            print("提示：还没设置看板口令，先运行 set-password，否则无法登录。", flush=True)
        admin = tg_license_admin.AdminWeb(service, signing, secure_cookie=not a.insecure_cookie,
                                          max_issue_per_day=a.max_issue_per_day)
    host, _, port = a.listen.rpartition(":")
    httpd = ThreadingHTTPServer((host or "127.0.0.1", int(port)), make_handler(service, admin))
    print("授权服务已启动：http://%s/api/v1/license  台账 %s  默认换机次数 %d" % (a.listen, a.db, a.default_quota), flush=True)
    if admin:
        print("管理看板：http://%s/admin/  %s" % (a.listen, "可签发（密钥ID %s）" % admin.key_id if admin.signing else "只管理、不签发"),
              flush=True)
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
    import getpass
    import tg_license_admin
    first = getpass.getpass("新的看板口令（至少 10 位）：")
    if len(first) < 10:
        sys.exit("口令太短")
    if getpass.getpass("再输一次：") != first:
        sys.exit("两次输入不一致")
    ledger = Ledger(a.db, a.default_quota)
    ledger.set_setting("admin_password", tg_license_admin.hash_password(first))
    ledger.set_setting("session_epoch", str(int(time.time())))  # 改口令让所有已登录会话失效
    ledger.audit("cli", "set-password")
    print("口令已设置。正在运行的服务会在下一次请求时让旧会话全部失效。")


def cmd_import_ledger(a):
    master = load_master(a.master)
    ledger = Ledger(a.db, a.default_quota)
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
    return Ledger(a.db, a.default_quota)


def describe(ledger, row):
    plan = PLANS.get(row["plan"], ("?", 0))
    expiry = ""
    if row["issue_day"] is not None:
        expiry = datetime.fromtimestamp(EPOCH + (row["issue_day"] + plan[1]) * 86400, timezone.utc).strftime("%Y-%m-%d")
    state = "作废" if row["revoked"] else ("已绑定" if row["machine"] else "未绑定")
    quota = ledger.quota_of(row)
    return "%s  %-4s  %-6s 到期 %-10s  机器 %-10s  换机 %d/%d  最近 %s  %s" % (
        display_id(row["code_id"]), state, plan[0], expiry, row["machine"] or "-",
        row["rebinds_used"], quota, row["last_seen"] or "-", row["note"] or "",
    )


def cmd_list(a):
    ledger = open_ledger(a)
    rows = ledger.db.execute("SELECT * FROM codes ORDER BY first_seen DESC").fetchall()
    for row in rows:
        print(describe(ledger, row))
    print("共 %d 个码" % len(rows))


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


def cmd_show(a):
    ledger = open_ledger(a)
    cid, row = require_row(ledger, a.code)
    print(describe(ledger, row))
    for ev in ledger.db.execute("SELECT * FROM events WHERE code_id=? ORDER BY id DESC LIMIT 20", (cid,)):
        print("  %s  %-8s %-7s %-7s 机器 %s  IP %s" % (ev["at"], ev["op"], ev["status"], ev["result"], ev["machine"], ev["ip"]))


def cmd_revoke(a):
    ledger = open_ledger(a)
    cid, _ = require_row(ledger, a.code, create=True)  # 没见过的码也能提前作废
    ledger.db.execute("UPDATE codes SET revoked=1, note=COALESCE(?, note) WHERE code_id=?", (a.note, cid))
    print("已作废 %s：已激活的机器最迟在下一次联网复核（约 1 天内）时失效" % display_id(cid))


def cmd_unrevoke(a):
    ledger = open_ledger(a)
    cid, _ = require_row(ledger, a.code)
    ledger.db.execute("UPDATE codes SET revoked=0 WHERE code_id=?", (cid,))
    print("已恢复 %s" % display_id(cid))


def cmd_unbind(a):
    ledger = open_ledger(a)
    cid, _ = require_row(ledger, a.code)
    ledger.db.execute("UPDATE codes SET machine=NULL, bound_at=NULL WHERE code_id=?", (cid,))
    print("已解绑 %s：下一台输入这个码的机器直接绑定，不占换机次数" % display_id(cid))


def cmd_quota(a):
    ledger = open_ledger(a)
    cid, _ = require_row(ledger, a.code)
    if a.reset:
        ledger.db.execute("UPDATE codes SET rebinds_used=0 WHERE code_id=?", (cid,))
    if a.count is not None:
        ledger.db.execute("UPDATE codes SET quota=? WHERE code_id=?", (a.count, cid))
    print(describe(ledger, ledger.row(cid)))


def cmd_events(a):
    ledger = open_ledger(a)
    for ev in ledger.db.execute("SELECT * FROM events ORDER BY id DESC LIMIT ?", (a.limit,)):
        print("%s  %s  %-8s %-7s %-7s 机器 %s  IP %s" % (
            ev["at"], display_id(ev["code_id"]), ev["op"], ev["status"], ev["result"], ev["machine"], ev["ip"]))


def main(argv=None):
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
    s = sub.add_parser("events", help="最近的激活/复核记录")
    s.add_argument("--limit", type=int, default=50)
    s.set_defaults(func=cmd_events)

    a = p.parse_args(argv)
    a.func(a)


if __name__ == "__main__":
    main()
