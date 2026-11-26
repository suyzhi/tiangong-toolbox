"""授权管理看板（/admin）：静态资源、激活码查询与统计、签发、作废 / 解绑 / 换机次数 / 延长 / 备注 / 版本发布。"""
import base64
import json
import mimetypes
import os
import re
import threading
from datetime import datetime, timedelta, timezone
from urllib.parse import parse_qs, urlsplit

from .admin_auth import AdminAuth
from .codec import LETTER_OF, PLAN_LETTERS, PLANS, code_id, compose, date_text, day_offset, display_id, new_payload, stamp
from .crypto import sign_p1363, verify_p1363
from .ledger import expiry_day

# 静态资源目录位于 server/admin/（本模块在 server/tg_license/ 下）
STATIC_DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "admin")
STATIC_FILES = {"/admin/": "index.html", "/admin/app.js": "app.js", "/admin/app.css": "app.css"}
ID_PATH = re.compile(r"^/admin/api/codes/([0-9a-z]{8})(?:/(revoke|unrevoke|unbind|quota|meta|extend))?\Z")
SECURITY_HEADERS = [
    ("X-Content-Type-Options", "nosniff"),
    ("Referrer-Policy", "no-referrer"),
    ("X-Frame-Options", "DENY"),
    ("Cache-Control", "no-store"),
]
PAGE_CSP = (
    "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; "
    "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'"
)


def date_of(day):
    return date_text(day)


class AdminWeb:
    def __init__(self, service, signing=None, secure_cookie=True, max_issue_per_day=50):
        self.service = service
        self.ledger = service.ledger
        self.signing = signing
        self.key_id = signing[0] if signing else None
        self.secure_cookie = secure_cookie
        self.max_issue = max_issue_per_day
        self.lock = threading.Lock()
        self.auth = AdminAuth(self.ledger, secure_cookie=secure_cookie, lock=self.lock)
        # 兼容外部/测试直接访问 admin.fails / admin.all_fails / admin.sessions
        self.fails = self.auth.fails
        self.all_fails = self.auth.all_fails

    @property
    def sessions(self):
        return self.auth.sessions

    @sessions.setter
    def sessions(self, value):
        self.auth.sessions = value

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
            if not self.auth.verify_csrf(headers, session):
                return self.json(403, {"error": "页面已过期，请刷新后重试"})
            if route == "/admin/api/updates":
                # 发布新版本：请求体就是安装包 zip 原文（不是 JSON），文件名 / 版本在查询参数，说明优先走 X-TG-Notes 头
                return self.publish_update(parse_qs(url.query), headers, body, ip)
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
            return self.json(200, {"ok": True}, self.auth.logout(session))
        if method == "GET" and route == "/admin/api/codes":
            return self.json(200, {"codes": self.codes(), "stats": self.stats(), "today": day_offset(self.now())})
        if method == "GET" and route == "/admin/api/events":
            raw_limit = (query.get("limit") or ["100"])[0] or "100"
            try:
                limit = int(raw_limit)
            except (TypeError, ValueError):
                return self.json(400, {"error": "limit 应为正整数"})
            limit = max(1, min(limit, 500))
            return self.json(200, {"events": self.events(None, limit)})
        if method == "GET" and route == "/admin/api/updates":
            return self.json(200, self.updates_state())
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

    # ------------------------------------------------------------ 版本发布

    def updates_state(self):
        u = self.service.updates
        if u is None:
            return {"enabled": False, "latest": None, "history": []}
        return {"enabled": True, "latest": u.latest_manifest(), "history": u.history(10)}

    @staticmethod
    def _extract_update_notes(headers, query):
        # 优先从请求头 X-TG-Notes 读取 UTF-8 Base64，避免长中文说明触发 URL 长度上限或落进访问日志；
        # 保留 ?notes=... 查询参数兼容旧版脚本与现有单测。
        b64 = (headers.get("X-TG-Notes") or "").strip() if headers else ""
        if b64:
            try:
                return base64.b64decode(b64.encode("ascii")).decode("utf-8")
            except (ValueError, UnicodeDecodeError):
                raise ValueError("更新说明编码格式不对")
        return (query.get("notes") or [""])[0]

    def publish_update(self, query, headers, body, ip):
        u = self.service.updates
        if u is None:
            return self.json(400, {"error": "服务器没有配置版本发布目录"})
        first = lambda k: (query.get(k) or [""])[0]
        try:
            notes = self._extract_update_notes(headers, query)
            m = u.publish(body, first("name"), first("version"), notes, by=ip)
        except ValueError as e:
            return self.json(400, {"error": str(e)})
        self.ledger.audit(ip, "publish", None, "%s %s" % (m["version"], m["file"]))
        return self.json(200, self.updates_state())

    def handle_update_upload(self, path, headers, tmp_path, size, ip):
        """流式上传版本入口（由 http_server 提前验完登录与 CSRF 并流式写盘后调用）。"""
        u = self.service.updates
        if u is None:
            return self.json(400, {"error": "服务器没有配置版本发布目录"})
        query = parse_qs(urlsplit(path).query)
        first = lambda k: (query.get(k) or [""])[0]
        try:
            notes = self._extract_update_notes(headers, query)
            m = u.publish_file(tmp_path, first("name"), first("version"), notes, by=ip, size=size)
        except ValueError as e:
            return self.json(400, {"error": str(e)})
        self.ledger.audit(ip, "publish", None, "%s %s" % (m["version"], m["file"]))
        return self.json(200, self.updates_state())

    # ------------------------------------------------------------ 响应与登录代理

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

    def cookie(self, token, max_age):
        return self.auth.cookie(token, max_age)

    def locked(self, ip):
        return self.auth.locked(ip)

    def login(self, body, ip):
        try:
            password = json.loads(body.decode("utf-8")).get("password", "")
        except (ValueError, UnicodeDecodeError, AttributeError):
            password = ""
        code, obj, extra = self.auth.authenticate(password, ip)
        return self.json(code, obj, extra)

    def session(self, headers):
        return self.auth.session(headers)

    def me(self, session):
        today = day_offset(self.now())
        return {
            "csrf": session["csrf"],
            "canIssue": self.signing is not None,
            "keyId": self.key_id,
            "today": today,
            "todayDate": date_of(today),
            "issueLimit": self.max_issue,
            "defaultQuota": self.ledger.default_quota,
            "plans": [
                {"letter": LETTER_OF[c], "name": n, "days": d, "expiry": date_of(today + d)}
                for c, (n, d) in sorted(PLANS.items(), key=lambda kv: kv[1][1])
            ],
        }

    # ------------------------------------------------------------ 查询

    def shape(self, row, today):
        plan = PLANS.get(row["plan"])
        exp_day = expiry_day(row)   # 含管理员延长 / 缩短的天数
        days_left = exp_day - today if exp_day is not None else None
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
            "display": display_id(row["code_id"]),
            "plan": plan[0] if plan else None,
            "planLetter": LETTER_OF.get(row["plan"]),
            "issueDate": date_of(row["issue_day"]) if row["issue_day"] is not None else None,
            "expiry": date_of(exp_day) if exp_day is not None else None,
            "extraDays": row["extra_days"] or 0,
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
        today = day_offset(self.now())
        rows = self.ledger.query("SELECT * FROM codes ORDER BY COALESCE(issued_at, first_seen) DESC, code_id")
        return [self.shape(r, today) for r in rows]

    def stats(self):
        today = day_offset(self.now())
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
            "expiring": sum(
                1 for c in items if c["state"] in ("active", "unused") and c["daysLeft"] is not None and c["daysLeft"] <= 14
            ),
            "checks24h": self.ledger.query("SELECT COUNT(*) n FROM events WHERE at>=?", (since,))[0]["n"],
            "rejected24h": self.ledger.query(
                "SELECT COUNT(*) n FROM events WHERE at>=? AND status IN ('moved','quota','revoked')", (since,)
            )[0]["n"],
            "issuedToday": self.issued_today(today_prefix),
            "issueLimit": self.max_issue,
        }

    def issued_today(self, prefix):
        return self.ledger.query(
            "SELECT COUNT(*) n FROM codes WHERE source='web' AND issued_at LIKE ?", (prefix + "%",)
        )[0]["n"]

    def events(self, cid, limit):
        if cid:
            rows = self.ledger.query("SELECT * FROM events WHERE code_id=? ORDER BY id DESC LIMIT ?", (cid, limit))
        else:
            rows = self.ledger.query("SELECT * FROM events ORDER BY id DESC LIMIT ?", (limit,))
        return [
            {
                "at": r["at"], "id": r["code_id"], "display": display_id(r["code_id"]), "machine": r["machine"],
                "op": r["op"], "status": r["status"], "result": r["result"], "ip": r["ip"],
            }
            for r in rows
        ]

    def detail(self, cid):
        today = day_offset(self.now())
        row = self.ledger.row(cid)
        out = self.shape(row, today)
        out["code"] = row["code_text"]
        out["events"] = self.events(cid, 50)
        out["audit"] = [
            {"at": r["at"], "ip": r["ip"], "action": r["action"], "detail": r["detail"]}
            for r in self.ledger.query("SELECT * FROM audit WHERE code_id=? ORDER BY id DESC LIMIT 50", (cid,))
        ]
        return out

    # ------------------------------------------------------------ 写操作

    def issue(self, data, ip):
        if self.signing is None:
            return self.json(403, {"error": "服务器没有配置签发私钥（serve --signing-key），只能管理、不能签发"})
        plan = PLAN_LETTERS.get(str(data.get("plan", "")).upper())
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
            _, key = self.signing
            day = day_offset(self.now())
            issued = []
            for _ in range(count):
                payload = new_payload(plan, day)
                signature = sign_p1363(key, payload)
                if not verify_p1363(self.service.master, payload, signature):
                    return self.json(500, {"error": "签发自检失败：私钥与验码公钥不配对"})
                text = compose(payload, signature)
                cid = code_id(payload)
                self.ledger.add_issued(cid, plan, day, text, customer, note, ip, "web")
                issued.append({"id": cid, "display": display_id(cid), "code": text})
        name, days = PLANS[plan]
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
            with L.lock:
                L.db.execute("BEGIN IMMEDIATE")
                try:
                    before = L.row(cid)["machine"]
                    # 记录 unbound_at，阻止旧机后台静默 check 自动回绑
                    L.db.execute(
                        "UPDATE codes SET machine=NULL, bound_at=NULL, unbound_at=? WHERE code_id=?",
                        (stamp(), cid),
                    )
                    L.db.execute("COMMIT")
                except Exception:
                    L.db.execute("ROLLBACK")
                    raise
            L.audit(ip, "unbind", cid, before)
        elif action == "quota":
            if data.get("reset"):
                L.execute("UPDATE codes SET rebinds_used=0 WHERE code_id=?", (cid,))
            if "quota" in data:
                value = data["quota"]
                if value is not None:
                    try:
                        if isinstance(value, bool):
                            raise ValueError()
                        value = int(value)
                    except (TypeError, ValueError):
                        return self.json(400, {"error": "换机次数应为 0 ~ 99 的整数"})
                    if not 0 <= value <= 99:
                        return self.json(400, {"error": "换机次数应为 0 ~ 99 的整数"})
                L.execute("UPDATE codes SET quota=? WHERE code_id=?", (value, cid))
            L.audit(ip, "quota", cid, json.dumps({k: data.get(k) for k in ("quota", "reset") if k in data}))
        elif action == "extend":
            # 延长 / 缩短有效期：在台账锁与写事务内原子读改写，避免并发连点导致累加丢失。
            with L.lock:
                L.db.execute("BEGIN IMMEDIATE")
                try:
                    row = L.row(cid)
                    extra = row["extra_days"] or 0
                    if data.get("to"):
                        base = expiry_day(row)
                        if base is None:
                            L.db.execute("ROLLBACK")
                            return self.json(400, {"error": "服务器还没见过这个码（客户没联网激活过），只能按天数延长"})
                        try:
                            target = day_offset(datetime.strptime(str(data["to"]), "%Y-%m-%d").replace(tzinfo=timezone.utc).timestamp())
                        except ValueError:
                            L.db.execute("ROLLBACK")
                            return self.json(400, {"error": "日期格式应为 YYYY-MM-DD"})
                        extra += target - base
                    else:
                        raw_days = data.get("days")
                        try:
                            if isinstance(raw_days, bool):
                                raise ValueError()
                            days = int(raw_days)
                        except (TypeError, ValueError):
                            L.db.execute("ROLLBACK")
                            return self.json(400, {"error": "延长天数应为整数（负数 = 缩短）"})
                        if not -3650 <= days <= 3650:
                            L.db.execute("ROLLBACK")
                            return self.json(400, {"error": "一次最多延长 / 缩短 3650 天"})
                        extra += days
                    L.db.execute("UPDATE codes SET extra_days=? WHERE code_id=?", (extra, cid))
                    L.db.execute("COMMIT")
                except Exception:
                    L.db.execute("ROLLBACK")
                    raise
            L.audit(ip, "extend", cid, "累计 %+d 天" % extra)
        elif action == "meta":
            customer = str(data.get("customer") or "").strip()[:100] or None
            note = str(data.get("note") or "").strip()[:200] or None
            L.execute("UPDATE codes SET customer=?, note=? WHERE code_id=?", (customer, note, cid))
            L.audit(ip, "meta", cid, "%s | %s" % (customer or "", note or ""))
        return self.json(200, self.detail(cid))
