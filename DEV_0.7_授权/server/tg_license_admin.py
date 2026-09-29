"""授权管理看板（/admin）：登录、激活码列表、签发、作废 / 解绑 / 换机次数、备注、审计。

由 tg_license_server.py serve --signing-key ...（或 --admin）挂载，不单独运行。

安全措施（签发私钥在服务器上，看板口令就是签发权限）：
  - 口令 PBKDF2-SHA256（30 万轮）存库；set-password 会让所有旧会话失效
  - 登录限速：同一 IP 15 分钟内失败 5 次锁定；全局 15 分钟内失败 30 次全部锁定
  - 会话 Cookie：HttpOnly + SameSite=Strict + Secure（HTTPS），12 小时过期，仅 Path=/admin
  - 所有写操作要带 X-TG-CSRF 头（登录时下发，与会话绑定）
  - 页面 CSP 只允许同源脚本，禁止被嵌入 iframe
  - 每日签发上限；所有写操作记审计日志（时间、IP、动作、码ID）
"""
import hashlib
import hmac
import json
import mimetypes
import os
import re
import secrets
import threading
import time
from collections import defaultdict, deque
from datetime import datetime, timedelta, timezone
from urllib.parse import parse_qs, urlsplit

import tg_license_server as core

STATIC_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "admin")
STATIC_FILES = {"/admin/": "index.html", "/admin/app.js": "app.js", "/admin/app.css": "app.css"}
SESSION_SECONDS = 12 * 3600
PBKDF2_ROUNDS = 300000
ID_PATH = re.compile(r"^/admin/api/codes/([0-9a-z]{8})(?:/(revoke|unrevoke|unbind|quota|meta))?$")
SECURITY_HEADERS = [
    ("X-Content-Type-Options", "nosniff"),
    ("Referrer-Policy", "no-referrer"),
    ("X-Frame-Options", "DENY"),
    ("Cache-Control", "no-store"),
]
PAGE_CSP = ("default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; "
            "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'")


def hash_password(password, salt=None, rounds=PBKDF2_ROUNDS):
    salt = salt or secrets.token_bytes(16)
    digest = hashlib.pbkdf2_hmac("sha256", password.encode("utf-8"), salt, rounds)
    return "pbkdf2_sha256$%d$%s$%s" % (rounds, salt.hex(), digest.hex())


def check_password(password, stored):
    try:
        scheme, rounds, salt, digest = stored.split("$")
        if scheme != "pbkdf2_sha256":
            return False
        actual = hashlib.pbkdf2_hmac("sha256", password.encode("utf-8"), bytes.fromhex(salt), int(rounds))
        return hmac.compare_digest(actual.hex(), digest)
    except (ValueError, AttributeError):
        return False


def date_of(day):
    return datetime.fromtimestamp(core.EPOCH + day * 86400, timezone.utc).strftime("%Y-%m-%d")


class AdminWeb:
    def __init__(self, service, signing=None, secure_cookie=True, max_issue_per_day=50):
        self.service = service
        self.ledger = service.ledger
        self.signing = signing
        self.key_id = signing[0] if signing else None
        self.secure_cookie = secure_cookie
        self.max_issue = max_issue_per_day
        self.sessions = {}      # token → {"expires", "csrf", "created", "ip"}
        self.fails = defaultdict(deque)
        self.all_fails = deque()
        self.lock = threading.Lock()

    # ------------------------------------------------------------ 分发

    def handle(self, method, path, headers, body, ip):
        url = urlsplit(path)
        route = url.path
        if method == "GET" and route == "/admin":
            return 302, [("Location", "/admin/")] + SECURITY_HEADERS, b""
        if method == "GET" and route in STATIC_FILES:
            return self.static(STATIC_FILES[route])
        if not route.startswith("/admin/api/"):
            return self.json(404, {"error": "not found"})
        if method == "POST" and route == "/admin/api/login":
            return self.login(body, ip)

        session = self.session(headers)
        if session is None:
            return self.json(401, {"error": "请先登录"})
        if method == "POST":
            sent = headers.get("X-TG-CSRF", "")
            if not hmac.compare_digest(sent.encode(), session["csrf"].encode()):
                return self.json(403, {"error": "页面已过期，请刷新后重试"})
            try:
                data = json.loads(body.decode("utf-8") or "{}")
            except (ValueError, UnicodeDecodeError):
                return self.json(400, {"error": "请求不是合法 JSON"})
            if not isinstance(data, dict):
                return self.json(400, {"error": "请求格式不对"})
        query = parse_qs(url.query)

        if method == "GET" and route == "/admin/api/me":
            return self.json(200, self.me(session))
        if method == "POST" and route == "/admin/api/logout":
            with self.lock:
                self.sessions.pop(session["token"], None)
            return self.json(200, {"ok": True}, [("Set-Cookie", self.cookie("", 0))])
        if method == "GET" and route == "/admin/api/codes":
            return self.json(200, {"codes": self.codes(), "stats": self.stats(), "today": core.day_offset(self.now())})
        if method == "GET" and route == "/admin/api/events":
            limit = min(int((query.get("limit") or ["100"])[0] or 100), 500)
            return self.json(200, {"events": self.events(None, limit)})
        if method == "POST" and route == "/admin/api/issue":
            return self.issue(data, ip)
        m = ID_PATH.match(route)
        if m:
            cid, action = m.group(1), m.group(2)
            if self.ledger.row(cid) is None:
                return self.json(404, {"error": "没有这个码"})
            if method == "GET" and action is None:
                return self.json(200, self.detail(cid))
            if method == "POST" and action:
                return self.act(cid, action, data, ip)
        return self.json(404, {"error": "not found"})

    # ------------------------------------------------------------ 响应

    def json(self, code, obj, extra=()):
        data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        return code, [("Content-Type", "application/json; charset=utf-8")] + SECURITY_HEADERS + list(extra), data

    def static(self, name):
        with open(os.path.join(STATIC_DIR, name), "rb") as f:
            data = f.read()
        kind = mimetypes.guess_type(name)[0] or "application/octet-stream"
        if kind.startswith("text/") or kind.endswith("javascript"):
            kind += "; charset=utf-8"
        return 200, [("Content-Type", kind), ("Content-Security-Policy", PAGE_CSP)] + SECURITY_HEADERS, data

    def now(self):
        return self.service.clock()

    # ------------------------------------------------------------ 登录与会话

    def cookie(self, token, max_age):
        parts = ["tg_admin=" + token, "Path=/admin", "HttpOnly", "SameSite=Strict", "Max-Age=%d" % max_age]
        if self.secure_cookie:
            parts.append("Secure")
        return "; ".join(parts)

    def locked(self, ip):
        now = time.monotonic()
        with self.lock:
            q = self.fails[ip]
            while q and now - q[0] > 900:
                q.popleft()
            while self.all_fails and now - self.all_fails[0] > 900:
                self.all_fails.popleft()
            return len(q) >= 5 or len(self.all_fails) >= 30

    def login(self, body, ip):
        if self.locked(ip):
            return self.json(429, {"error": "登录失败次数太多，请 15 分钟后再试"})
        try:
            password = json.loads(body.decode("utf-8")).get("password", "")
        except (ValueError, UnicodeDecodeError, AttributeError):
            password = ""
        stored = self.ledger.setting("admin_password")
        if stored is None:
            return self.json(503, {"error": "还没有设置看板口令：请在服务器上运行 set-password"})
        if not isinstance(password, str) or not check_password(password, stored):
            with self.lock:
                self.fails[ip].append(time.monotonic())
                self.all_fails.append(time.monotonic())
            self.ledger.audit(ip, "login-failed")
            return self.json(401, {"error": "口令不对"})
        token, csrf = secrets.token_urlsafe(32), secrets.token_urlsafe(24)
        with self.lock:
            self.fails.pop(ip, None)
            now = time.time()
            self.sessions = {k: v for k, v in self.sessions.items() if v["expires"] > now}
            self.sessions[token] = {"token": token, "expires": now + SESSION_SECONDS, "csrf": csrf,
                                    "created": now, "ip": ip}
        self.ledger.audit(ip, "login")
        return self.json(200, {"csrf": csrf}, [("Set-Cookie", self.cookie(token, SESSION_SECONDS))])

    def session(self, headers):
        token = None
        for part in (headers.get("Cookie") or "").split(";"):
            name, _, value = part.strip().partition("=")
            if name == "tg_admin":
                token = value
        if not token:
            return None
        epoch = float(self.ledger.setting("session_epoch") or 0)
        with self.lock:
            s = self.sessions.get(token)
            if s is None or s["expires"] < time.time() or s["created"] < epoch:
                self.sessions.pop(token, None)
                return None
            return s

    def me(self, session):
        today = core.day_offset(self.now())
        return {
            "csrf": session["csrf"],
            "canIssue": self.signing is not None,
            "keyId": self.key_id,
            "today": today,
            "todayDate": date_of(today),
            "issueLimit": self.max_issue,
            "defaultQuota": self.ledger.default_quota,
            "plans": [{"letter": core.LETTER_OF[c], "name": n, "days": d, "expiry": date_of(today + d)}
                      for c, (n, d) in sorted(core.PLANS.items(), key=lambda kv: kv[1][1])],
        }

    # ------------------------------------------------------------ 查询

    def shape(self, row, today):
        plan = core.PLANS.get(row["plan"])
        expiry_day = row["issue_day"] + plan[1] if plan and row["issue_day"] is not None else None
        days_left = expiry_day - today if expiry_day is not None else None
        if row["revoked"]:
            state = "revoked"
        elif days_left is not None and days_left <= 0:
            state = "expired"
        elif row["machine"]:
            state = "active"
        elif plan:
            state = "unused"
        else:
            state = "unknown"
        return {
            "id": row["code_id"],
            "display": core.display_id(row["code_id"]),
            "plan": plan[0] if plan else None,
            "planLetter": core.LETTER_OF.get(row["plan"]),
            "issueDate": date_of(row["issue_day"]) if row["issue_day"] is not None else None,
            "expiry": date_of(expiry_day) if expiry_day is not None else None,
            "daysLeft": days_left,
            "state": state,
            "machine": row["machine"],
            "boundAt": row["bound_at"],
            "lastSeen": row["last_seen"],
            "lastIp": row["last_ip"],
            "rebindsUsed": row["rebinds_used"],
            "quota": self.ledger.quota_of(row),
            "quotaCustom": row["quota"] is not None,
            "customer": row["customer"],
            "note": row["note"],
            "issuedAt": row["issued_at"] or row["first_seen"],
            "source": row["source"],
            "hasText": bool(row["code_text"]),
        }

    def codes(self):
        today = core.day_offset(self.now())
        rows = self.ledger.query("SELECT * FROM codes ORDER BY COALESCE(issued_at, first_seen) DESC, code_id")
        return [self.shape(r, today) for r in rows]

    def stats(self):
        today = core.day_offset(self.now())
        items = [self.shape(r, today) for r in self.ledger.query("SELECT * FROM codes")]
        count = lambda st: sum(1 for c in items if c["state"] == st)
        since = (datetime.now() - timedelta(hours=24)).strftime("%Y-%m-%d %H:%M:%S")
        today_prefix = datetime.now().strftime("%Y-%m-%d")
        return {
            "total": len(items),
            "active": count("active"),
            "unused": count("unused"),
            "revoked": count("revoked"),
            "expired": count("expired"),
            "expiring": sum(1 for c in items if c["state"] in ("active", "unused") and c["daysLeft"] is not None
                            and c["daysLeft"] <= 14),
            "checks24h": self.ledger.query("SELECT COUNT(*) n FROM events WHERE at>=?", (since,))[0]["n"],
            "rejected24h": self.ledger.query(
                "SELECT COUNT(*) n FROM events WHERE at>=? AND status IN ('moved','quota','revoked')", (since,))[0]["n"],
            "issuedToday": self.issued_today(today_prefix),
            "issueLimit": self.max_issue,
        }

    def issued_today(self, prefix):
        return self.ledger.query("SELECT COUNT(*) n FROM codes WHERE source='web' AND issued_at LIKE ?",
                                 (prefix + "%",))[0]["n"]

    def events(self, cid, limit):
        if cid:
            rows = self.ledger.query("SELECT * FROM events WHERE code_id=? ORDER BY id DESC LIMIT ?", (cid, limit))
        else:
            rows = self.ledger.query("SELECT * FROM events ORDER BY id DESC LIMIT ?", (limit,))
        return [{"at": r["at"], "id": r["code_id"], "display": core.display_id(r["code_id"]), "machine": r["machine"],
                 "op": r["op"], "status": r["status"], "result": r["result"], "ip": r["ip"]} for r in rows]

    def detail(self, cid):
        today = core.day_offset(self.now())
        row = self.ledger.row(cid)
        out = self.shape(row, today)
        out["code"] = row["code_text"]
        out["events"] = self.events(cid, 50)
        out["audit"] = [{"at": r["at"], "ip": r["ip"], "action": r["action"], "detail": r["detail"]}
                        for r in self.ledger.query("SELECT * FROM audit WHERE code_id=? ORDER BY id DESC LIMIT 50", (cid,))]
        return out

    # ------------------------------------------------------------ 写操作

    def issue(self, data, ip):
        if self.signing is None:
            return self.json(403, {"error": "服务器没有配置签发私钥（serve --signing-key），只能管理、不能签发"})
        plan = core.PLAN_LETTERS.get(str(data.get("plan", "")).upper())
        try:
            count = int(data.get("count", 1))
        except (TypeError, ValueError):
            count = 0
        customer = str(data.get("customer") or "").strip()[:100] or None
        note = str(data.get("note") or "").strip()[:200] or None
        if plan is None:
            return self.json(400, {"error": "请选择档位"})
        if not 1 <= count <= 50:
            return self.json(400, {"error": "一次签发 1 ~ 50 个"})
        with self.lock:  # 上限检查与写入之间不能被另一个签发请求插进来
            used = self.issued_today(datetime.now().strftime("%Y-%m-%d"))
            if used + count > self.max_issue:
                return self.json(429, {"error": "今天已签发 %d 个，上限 %d（serve --max-issue-per-day 可调）" % (used, self.max_issue)})
            key_id, key = self.signing
            day = core.day_offset(self.now())
            issued = []
            for _ in range(count):
                payload = core.new_payload(plan, day)
                signature = core.sign_p1363(key, payload)
                if not core.verify_p1363(self.service.master, payload, signature):
                    return self.json(500, {"error": "签发自检失败：私钥与验码公钥不配对"})
                text = core.compose(payload, signature)
                cid = core.code_id(payload)
                self.ledger.add_issued(cid, plan, day, text, customer, note, ip, "web")
                issued.append({"id": cid, "display": core.display_id(cid), "code": text})
        name, days = core.PLANS[plan]
        self.ledger.audit(ip, "issue", None, "%s × %d %s" % (name, count, customer or ""))
        for item in issued:
            self.ledger.audit(ip, "issue", item["id"], "%s %s" % (name, customer or ""))
        return self.json(200, {"codes": issued, "plan": name, "expiry": date_of(day + days), "customer": customer})

    def act(self, cid, action, data, ip):
        L = self.ledger
        if action == "revoke":
            note = str(data.get("note") or "").strip()[:200] or None
            L.execute("UPDATE codes SET revoked=1, note=COALESCE(?, note) WHERE code_id=?", (note, cid))
            L.audit(ip, "revoke", cid, note)
        elif action == "unrevoke":
            L.execute("UPDATE codes SET revoked=0 WHERE code_id=?", (cid,))
            L.audit(ip, "unrevoke", cid)
        elif action == "unbind":
            before = L.row(cid)["machine"]
            L.execute("UPDATE codes SET machine=NULL, bound_at=NULL WHERE code_id=?", (cid,))
            L.audit(ip, "unbind", cid, before)
        elif action == "quota":
            if data.get("reset"):
                L.execute("UPDATE codes SET rebinds_used=0 WHERE code_id=?", (cid,))
            if "quota" in data:
                value = data["quota"]
                if value is not None:
                    try:
                        value = int(value)
                    except (TypeError, ValueError):
                        return self.json(400, {"error": "换机次数应为 0 ~ 99 的整数"})
                    if not 0 <= value <= 99:
                        return self.json(400, {"error": "换机次数应为 0 ~ 99 的整数"})
                L.execute("UPDATE codes SET quota=? WHERE code_id=?", (value, cid))
            L.audit(ip, "quota", cid, json.dumps({k: data.get(k) for k in ("quota", "reset") if k in data}))
        elif action == "meta":
            customer = str(data.get("customer") or "").strip()[:100] or None
            note = str(data.get("note") or "").strip()[:200] or None
            L.execute("UPDATE codes SET customer=?, note=? WHERE code_id=?", (customer, note, cid))
            L.audit(ip, "meta", cid, "%s | %s" % (customer or "", note or ""))
        return self.json(200, self.detail(cid))
