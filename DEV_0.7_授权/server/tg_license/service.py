"""插件授权 API 服务：请求校验、限流、验码与回执签名。"""
import base64
import json
import re
import threading
import time
from collections import defaultdict, deque

from .codec import PAYLOAD_SIZE, PLANS, code_id, day_offset, parse_payload
from .crypto import sign_p1363, verify_p1363

MAX_BODY = 4096
MAX_TRACKED_IPS = 5000
HEX = re.compile(r"^[0-9a-f]+$")


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
        self.updates = None     # 版本发布（tg_license.updates.Updates），serve 时挂上

    def _prune_hits_locked(self, now):
        # 定期清理 60 秒窗口已过期的 IP 键，避免长期运行或伪造多 IP 导致字典无限增长。
        stale = []
        for k, q in self.hits.items():
            while q and now - q[0] > 60:
                q.popleft()
            if not q:
                stale.append(k)
        for k in stale:
            del self.hits[k]

    def allow(self, ip):
        now = time.monotonic()
        with self.hits_lock:
            if len(self.hits) > 1000:
                self._prune_hits_locked(now)
            q = self.hits.get(ip)
            if q is None:
                if len(self.hits) >= MAX_TRACKED_IPS:
                    self._prune_hits_locked(now)
                    if len(self.hits) >= MAX_TRACKED_IPS:
                        return False
                q = deque()
                self.hits[ip] = q
            else:
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
        status, result, left, quota, expires = self.ledger.decide(op, cid, info["plan"], info["day"], machine, ip, today)
        receipt = {
            "v": 1, "code": cid, "machine": machine, "nonce": fields["nonce"], "op": op,
            "status": status, "result": result, "left": left, "quota": quota,
            "expires": expires, "day": today, "time": int(now),
        }
        raw = json.dumps(receipt, separators=(",", ":"), ensure_ascii=True).encode("ascii")
        return 200, {
            "receipt": base64.b64encode(raw).decode("ascii"),
            "sig": base64.b64encode(sign_p1363(self.server_key, raw)).decode("ascii"),
        }
