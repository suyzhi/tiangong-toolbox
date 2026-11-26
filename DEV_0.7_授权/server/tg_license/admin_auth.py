"""管理看板认证与会话管理：PBKDF2-SHA256 口令、登录限速锁定、会话 Cookie、CSRF 校验。"""
import hashlib
import hmac
import secrets
import threading
import time
from collections import defaultdict, deque

SESSION_SECONDS = 12 * 3600
PBKDF2_ROUNDS = 300000
MAX_FAIL_IPS = 2000


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


class AdminAuth:
    def __init__(self, ledger, secure_cookie=True, lock=None):
        self.ledger = ledger
        self.secure_cookie = secure_cookie
        self.lock = lock or threading.Lock()
        # 限制并发 PBKDF2 计算线程数，防止并发登录洪峰占满 CPU
        self.auth_sem = threading.Semaphore(2)
        self.sessions = {}      # token → {"token", "expires", "csrf", "created", "ip"}
        self.fails = defaultdict(deque)
        self.all_fails = deque()

    def cookie(self, token, max_age):
        parts = ["tg_admin=" + token, "Path=/admin", "HttpOnly", "SameSite=Strict", "Max-Age=%d" % max_age]
        if self.secure_cookie:
            parts.append("Secure")
        return "; ".join(parts)

    def _prune_fails_locked(self, now):
        while self.all_fails and now - self.all_fails[0] > 900:
            self.all_fails.popleft()
        if len(self.fails) > 500:
            stale = []
            for k, q in self.fails.items():
                while q and now - q[0] > 900:
                    q.popleft()
                if not q:
                    stale.append(k)
            for k in stale:
                del self.fails[k]

    def _is_locked_locked(self, ip, now):
        self._prune_fails_locked(now)
        q = self.fails.get(ip)
        if q is not None:
            while q and now - q[0] > 900:
                q.popleft()
            if not q:
                self.fails.pop(ip, None)
                q = None
        return (q is not None and len(q) >= 5) or len(self.all_fails) >= 30

    def locked(self, ip):
        now = time.monotonic()
        with self.lock:
            return self._is_locked_locked(ip, now)

    def authenticate(self, password, ip):
        """校验口令并签发新会话。返回 (http_code, body_dict, extra_headers)。"""
        if self.locked(ip):
            return 429, {"error": "登录失败次数太多，请 15 分钟后再试"}, ()
        with self.auth_sem:
            stored = self.ledger.setting("admin_password")
            if stored is None:
                return 503, {"error": "还没有设置看板口令：请在服务器上运行 set-password"}, ()
            # 在调用耗时的 PBKDF2 之前，于锁内复查锁定并预记一次失败占位；
            # 防止多并发请求同时通过 locked() 检查绕过 5 次/30 次爆破阈值（TOCTOU）。
            now_mono = time.monotonic()
            with self.lock:
                if self._is_locked_locked(ip, now_mono):
                    return 429, {"error": "登录失败次数太多，请 15 分钟后再试"}, ()
                if len(self.fails) >= MAX_FAIL_IPS and ip not in self.fails:
                    return 429, {"error": "登录失败次数太多，请 15 分钟后再试"}, ()
                self.fails[ip].append(now_mono)
                self.all_fails.append(now_mono)

            ok = isinstance(password, str) and check_password(password, stored)
            if not ok:
                self.ledger.audit(ip, "login-failed")
                return 401, {"error": "口令不对"}, ()

            token, csrf = secrets.token_urlsafe(32), secrets.token_urlsafe(24)
            with self.lock:
                # 口令正确：撤回刚才预占的失败时间戳，并清空该 IP 的失败计数
                try:
                    self.all_fails.remove(now_mono)
                except ValueError:
                    pass
                self.fails.pop(ip, None)
                now = time.time()
                self.sessions = {k: v for k, v in self.sessions.items() if v["expires"] > now}
                self.sessions[token] = {
                    "token": token,
                    "expires": now + SESSION_SECONDS,
                    "csrf": csrf,
                    "created": now,
                    "ip": ip,
                }
            self.ledger.audit(ip, "login")
            return 200, {"csrf": csrf}, [("Set-Cookie", self.cookie(token, SESSION_SECONDS))]

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

    def verify_csrf(self, headers, session):
        sent = headers.get("X-TG-CSRF", "")
        return hmac.compare_digest(sent.encode(), session["csrf"].encode())

    def logout(self, session):
        with self.lock:
            self.sessions.pop(session["token"], None)
        return [("Set-Cookie", self.cookie("", 0))]
