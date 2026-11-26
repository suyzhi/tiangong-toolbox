"""SQLite 台账存储、表结构自动升级与一码一机核心裁决逻辑。"""
import sqlite3
import threading

from .codec import PLANS, stamp

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
    "extra_days": "INTEGER NOT NULL DEFAULT 0",   # 管理员延长（负数 = 缩短）的天数；到期日 = 签发日 + 档位天数 + 它
    "unbound_at": "TEXT",  # 管理员手动解绑时间；存在时拒绝旧机静默 check 自动回绑，仅允许主动 activate 重新绑定
}


def expiry_day(row):
    """这个码在服务器上的到期日（天数偏移）。还没见过码载荷（只提前作废/延长过）时为 None。"""
    plan = PLANS.get(row["plan"])
    if plan is None or row["issue_day"] is None:
        return None
    return row["issue_day"] + plan[1] + (row["extra_days"] or 0)


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
        self.execute(
            "INSERT INTO settings(name,value) VALUES(?,?) ON CONFLICT(name) DO UPDATE SET value=excluded.value",
            (name, value),
        )

    def audit(self, ip, action, cid=None, detail=None):
        self.execute("INSERT INTO audit(at,ip,action,code_id,detail) VALUES(?,?,?,?,?)", (stamp(), ip, action, cid, detail))

    def add_issued(self, cid, plan, day, text, customer, note, by, source):
        """记下一个签发/导入的码。已存在（客户端先联网过）就只补全文与客户信息。"""
        with self.lock:
            now = stamp()
            if self.row(cid) is None:
                self.db.execute(
                    "INSERT INTO codes(code_id,plan,issue_day,code_text,customer,note,issued_at,issued_by,source,first_seen)"
                    " VALUES(?,?,?,?,?,?,?,?,?,?)",
                    (cid, plan, day, text, customer, note, now, by, source, now),
                )
                return True
            self.db.execute(
                "UPDATE codes SET plan=COALESCE(plan,?), issue_day=COALESCE(issue_day,?), code_text=COALESCE(code_text,?),"
                " customer=COALESCE(customer,?), note=COALESCE(note,?), issued_at=COALESCE(issued_at,?),"
                " issued_by=COALESCE(issued_by,?), source=COALESCE(source,?) WHERE code_id=?",
                (plan, day, text, customer, note, now, by, source, cid),
            )
            return False

    def row(self, cid):
        with self.lock:
            return self.db.execute("SELECT * FROM codes WHERE code_id=?", (cid,)).fetchone()

    def quota_of(self, row):
        return self.default_quota if row["quota"] is None else row["quota"]

    def decide(self, op, cid, plan, issue_day, machine, ip, today):
        """核心规则。返回 (status, result, 剩余换机次数, 总次数, 到期日)。

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
        expires = expiry_day(row)

        if row["revoked"]:
            return "revoked", "", left, quota, expires
        if today >= expires:
            return "expired", "", left, quota, expires

        if row["machine"] is None:
            # 管理员显式解绑过（unbound_at 非空）时，旧机器后台静默 check 必须返回 moved 使其停用，
            # 避免旧机抢在新机激活前把码又自动绑回去；
            # 没解绑过的记录（如 0.7 离线激活老用户首次联网，unbound_at 为空）仍允许 check 自动补绑。
            if op == "check" and row["unbound_at"]:
                return "moved", "", left, quota, expires
            self.db.execute(
                "UPDATE codes SET machine=?, bound_at=?, last_seen=?, last_ip=?, unbound_at=NULL WHERE code_id=?",
                (machine, now, now, ip, cid),
            )
            return "ok", "bound", left, quota, expires
        if row["machine"] == machine:
            self.db.execute("UPDATE codes SET last_seen=?, last_ip=? WHERE code_id=?", (now, ip, cid))
            return "ok", "already", left, quota, expires
        if op == "check":
            return "moved", "", left, quota, expires
        if left <= 0:
            return "quota", "", 0, quota, expires
        self.db.execute(
            "UPDATE codes SET machine=?, bound_at=?, last_seen=?, last_ip=?, rebinds_used=rebinds_used+1 WHERE code_id=?",
            (machine, now, now, ip, cid),
        )
        return "ok", "rebound", left - 1, quota, expires
