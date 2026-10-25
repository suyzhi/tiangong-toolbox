"""联网授权服务端自测：python3 -m unittest server/test_server.py（需要 cryptography）。

起一个真实 HTTP 服务，用临时生成的"管理员签发密钥"签激活码，按插件的协议走一遍。
"""
import base64
import json
import os
import secrets
import sys
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer

from cryptography.hazmat.primitives.asymmetric import ec

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import tg_license_server as srv  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def make_payload(plan=42, day=None, bound_machine=None):
    body = bytearray(27)
    body[0] = 1 if bound_machine else 0
    body[1:3] = b"TG"
    body[3] = 1
    body[4] = plan
    body[5:15] = (bytes.fromhex(bound_machine) + bytes(5)) if bound_machine else secrets.token_bytes(10)
    body[15:19] = (srv.day_offset() if day is None else day).to_bytes(4, "big")
    body[19:27] = secrets.token_bytes(8)
    return bytes(body)


class ServerTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.master = ec.generate_private_key(ec.SECP256R1())
        cls.server_key = ec.generate_private_key(ec.SECP256R1())
        cls.ledger = srv.Ledger(os.path.join(cls.tmp.name, "t.db"), default_quota=1)
        cls.service = srv.Service(cls.ledger, cls.master.public_key(), cls.server_key)
        cls.httpd = ThreadingHTTPServer(("127.0.0.1", 0), srv.make_handler(cls.service))
        cls.url = "http://127.0.0.1:%d/api/v1/license" % cls.httpd.server_address[1]
        threading.Thread(target=cls.httpd.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.httpd.shutdown()
        cls.ledger.db.close()
        cls.tmp.cleanup()

    def call(self, op, payload, machine, signer=None, nonce=None):
        nonce = nonce or secrets.token_hex(16)
        sig = srv.sign_p1363(signer or self.master, payload)
        body = json.dumps({"v": 1, "op": op, "payload": payload.hex(), "sig": sig.hex(),
                           "machine": machine, "nonce": nonce, "client": "test"}).encode()
        req = urllib.request.Request(self.url, data=body, headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=5) as r:
                status, obj = r.status, json.load(r)
        except urllib.error.HTTPError as e:
            return e.code, json.load(e)
        raw = base64.b64decode(obj["receipt"])
        # 插件那一侧的动作：用服务端公钥验签，再核对 nonce
        self.assertTrue(srv.verify_p1363(self.server_key.public_key(), raw, base64.b64decode(obj["sig"])))
        receipt = json.loads(raw)
        self.assertEqual(receipt["nonce"], nonce)
        self.assertEqual(receipt["code"], srv.code_id(payload))
        self.assertEqual(receipt["machine"], machine)
        return status, receipt

    def test_bind_repeat_rebind_quota(self):
        p = make_payload()
        a, b, c = "aaaaaaaaaa", "bbbbbbbbbb", "cccccccccc"
        _, r = self.call("activate", p, a)
        self.assertEqual((r["status"], r["result"]), ("ok", "bound"))
        _, r = self.call("activate", p, a)
        self.assertEqual((r["status"], r["result"]), ("ok", "already"))
        _, r = self.call("check", p, a)
        self.assertEqual(r["status"], "ok")
        # 别的机器只复核不激活：不会被自动换绑
        _, r = self.call("check", p, b)
        self.assertEqual(r["status"], "moved")
        # 别的机器主动输码：用掉唯一一次换机
        _, r = self.call("activate", p, b)
        self.assertEqual((r["status"], r["result"], r["left"]), ("ok", "rebound", 0))
        # 原机器下次复核发现已转出
        _, r = self.call("check", p, a)
        self.assertEqual(r["status"], "moved")
        # 次数用完：第三台机器、以及原机器想抢回来，都被拒
        _, r = self.call("activate", p, c)
        self.assertEqual(r["status"], "quota")
        _, r = self.call("activate", p, a)
        self.assertEqual(r["status"], "quota")
        # 管理员解绑：下一台直接绑定，不占次数
        self.ledger.db.execute("UPDATE codes SET machine=NULL WHERE code_id=?", (srv.code_id(p),))
        _, r = self.call("activate", p, c)
        self.assertEqual((r["status"], r["result"]), ("ok", "bound"))

    def test_revoke_and_expired(self):
        p = make_payload()
        self.call("activate", p, "dddddddddd")
        self.ledger.db.execute("UPDATE codes SET revoked=1 WHERE code_id=?", (srv.code_id(p),))
        _, r = self.call("check", p, "dddddddddd")
        self.assertEqual(r["status"], "revoked")
        old = make_payload(plan=11, day=srv.day_offset() - 31)
        _, r = self.call("activate", old, "dddddddddd")
        self.assertEqual(r["status"], "expired")

    def test_rejects_forged_and_malformed(self):
        p = make_payload()
        code, obj = self.call("activate", p, "eeeeeeeeee", signer=ec.generate_private_key(ec.SECP256R1()))
        self.assertEqual(code, 403)
        self.assertNotIn("receipt", obj)
        code, _ = self.call("activate", p, "not-hex!!!")
        self.assertEqual(code, 400)
        bound = make_payload(bound_machine="1111111111")
        code, _ = self.call("activate", bound, "2222222222")
        self.assertEqual(code, 403)
        _, r = self.call("activate", bound, "1111111111")
        self.assertEqual(r["status"], "ok")

    def test_code_id_matches_csharp_alphabet(self):
        cid = srv.code_id(make_payload())
        self.assertEqual(len(cid), 8)
        self.assertTrue(all(c in srv.ALPHABET for c in cid))
        self.assertEqual(srv.normalize_id(srv.display_id(cid).upper()), cid)

    def test_unmask_repo_slot_gives_curve_point(self):
        # 仓库里提交的是 testmaster 测试公钥：还原出来的坐标必须落在 P-256 曲线上，
        # 否则说明 Python 版去掩码与 LicenseKeyMaterial.Unmask 不一致。
        with open(os.path.join(ROOT, "src", "License", "LicenseKeySlot.cs"), encoding="utf-8-sig") as f:
            text = f.read()
        consts = dict(srv.re.findall(r'(\w+)\s*=\s*"([^"]*)"', text))
        x = srv.unmask(consts["MaskX"], consts["ValueX"], consts["SeedX"])
        y = srv.unmask(consts["MaskY"], consts["ValueY"], consts["SeedY"])
        srv.public_key(x, y)


def repo_master():
    with open(os.path.join(ROOT, "src", "License", "LicenseKeySlot.cs"), encoding="utf-8-sig") as f:
        consts = dict(srv.re.findall(r'(\w+)\s*=\s*"([^"]*)"', f.read()))
    return srv.public_key(srv.unmask(consts["MaskX"], consts["ValueX"], consts["SeedX"]),
                          srv.unmask(consts["MaskY"], consts["ValueY"], consts["SeedY"]))


class CodecTest(unittest.TestCase):
    def test_real_admin_codes_roundtrip(self):
        # 仓库台账里是 C# 管理员工具（testmaster 密钥）签发的真码：
        # Python 解析出的码ID、重新组装的全文、验签，都必须与 C# 一致。
        master = repo_master()
        path = os.path.join(ROOT, "tests", "fixtures", "license-test.ledger.tsv")
        with open(path, encoding="utf-8-sig") as f:
            header = f.readline().rstrip("\r\n").split("\t")
            rows = [dict(zip(header, line.rstrip("\r\n").split("\t"))) for line in f if line.strip()]
        self.assertGreater(len(rows), 0)
        for row in rows:
            payload, sig = srv.parse_code(row["code"])
            self.assertEqual(srv.code_id(payload), row["codeId"])
            self.assertEqual(srv.compose(payload, sig), row["code"])
            self.assertTrue(srv.verify_p1363(master, payload, sig), row["codeId"])
            self.assertEqual(srv.LETTER_OF[srv.parse_payload(payload)["plan"]], row["plan"])
            # 用户粘贴时的常见走样：大写、去掉分隔符、多换行
            self.assertIsNotNone(srv.parse_code(row["code"].upper().replace("-", "\n")))
        broken = rows[0]["code"]
        broken = ("1" if broken[6] == "0" else "0").join([broken[:6], broken[7:]])
        self.assertIsNone(srv.parse_code(broken))

    def test_tgkey_and_slot_roundtrip(self):
        with tempfile.TemporaryDirectory() as tmp:
            key = ec.generate_private_key(ec.SECP256R1())
            path = os.path.join(tmp, "m.tgkey")
            srv.save_signing_key(path, "prod", key)
            key_id, loaded = srv.load_signing_key(path)
            self.assertEqual(key_id, "prod")
            self.assertEqual(loaded.private_numbers(), key.private_numbers())
            with self.assertRaises(FileExistsError):
                srv.save_signing_key(path, "prod", key)  # 绝不覆盖已有私钥
            nums = key.public_key().public_numbers()
            source = srv.master_slot_source("prod", nums.x.to_bytes(32, "big"), nums.y.to_bytes(32, "big"))
            consts = dict(srv.re.findall(r'(\w+)\s*=\s*"([^"]*)"', source))
            x = srv.unmask(consts["MaskX"], consts["ValueX"], consts["SeedX"])
            self.assertEqual(int.from_bytes(x, "big"), nums.x)


class AdminTest(unittest.TestCase):
    """网页看板：登录、CSRF、签发、作废 / 解绑 / 换机次数，签出来的码能直接走插件激活协议。"""

    @classmethod
    def setUpClass(cls):
        import tg_license_admin
        cls.tmp = tempfile.TemporaryDirectory()
        cls.master = ec.generate_private_key(ec.SECP256R1())
        cls.server_key = ec.generate_private_key(ec.SECP256R1())
        cls.ledger = srv.Ledger(os.path.join(cls.tmp.name, "a.db"), default_quota=2)
        cls.ledger.set_setting("admin_password", tg_license_admin.hash_password("correct horse battery", rounds=1000))
        cls.service = srv.Service(cls.ledger, cls.master.public_key(), cls.server_key)
        cls.admin = tg_license_admin.AdminWeb(cls.service, ("prod", cls.master), secure_cookie=False, max_issue_per_day=5)
        cls.httpd = ThreadingHTTPServer(("127.0.0.1", 0), srv.make_handler(cls.service, cls.admin))
        cls.base = "http://127.0.0.1:%d" % cls.httpd.server_address[1]
        threading.Thread(target=cls.httpd.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.httpd.shutdown()
        cls.ledger.db.close()
        cls.tmp.cleanup()

    def req(self, method, path, body=None, cookie=None, csrf=None, ip=None):
        headers = {"Content-Type": "application/json"}
        if cookie:
            headers["Cookie"] = cookie
        if csrf:
            headers["X-TG-CSRF"] = csrf
        data = None if body is None else json.dumps(body).encode()
        r = urllib.request.Request(self.base + path, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(r, timeout=5) as resp:
                return resp.status, dict(resp.headers), resp.read()
        except urllib.error.HTTPError as e:
            return e.code, dict(e.headers), e.read()

    def login(self):
        code, headers, body = self.req("POST", "/admin/api/login", {"password": "correct horse battery"})
        self.assertEqual(code, 200, body)
        cookie = headers["Set-Cookie"].split(";")[0]
        self.assertIn("HttpOnly", headers["Set-Cookie"])
        self.assertIn("SameSite=Strict", headers["Set-Cookie"])
        return cookie, json.loads(body)["csrf"]

    def test_page_and_auth(self):
        code, headers, body = self.req("GET", "/admin/")
        self.assertEqual(code, 200)
        self.assertIn("script-src 'self'", headers["Content-Security-Policy"])
        self.assertIn("授权管理".encode(), body)
        self.assertEqual(self.req("GET", "/admin/app.js")[0], 200)
        self.assertEqual(self.req("GET", "/admin/api/codes")[0], 401)
        self.assertEqual(self.req("GET", "/admin/../tg_license_server.py")[0], 404)
        self.assertEqual(self.req("POST", "/admin/api/login", {"password": "wrong"})[0], 401)
        cookie, csrf = self.login()
        self.assertEqual(self.req("GET", "/admin/api/me", cookie=cookie)[0], 200)
        # 写操作不带 CSRF 头 / 带错的：拒绝
        self.assertEqual(self.req("POST", "/admin/api/issue", {"plan": "M"}, cookie=cookie)[0], 403)
        self.assertEqual(self.req("POST", "/admin/api/issue", {"plan": "M"}, cookie=cookie, csrf="x")[0], 403)
        self.assertEqual(self.req("POST", "/admin/api/logout", {}, cookie=cookie, csrf=csrf)[0], 200)
        self.assertEqual(self.req("GET", "/admin/api/me", cookie=cookie)[0], 401)

    def test_login_lockout(self):
        self.admin.fails.clear()
        self.admin.all_fails.clear()
        for _ in range(5):
            self.assertEqual(self.req("POST", "/admin/api/login", {"password": "nope"})[0], 401)
        self.assertEqual(self.req("POST", "/admin/api/login", {"password": "correct horse battery"})[0], 429)
        self.admin.fails.clear()
        self.admin.all_fails.clear()

    def test_issue_and_manage(self):
        cookie, csrf = self.login()
        code, _, body = self.req("POST", "/admin/api/issue",
                                 {"plan": "Y", "count": 2, "customer": "甲公司 <b>王工</b>", "note": "首批"},
                                 cookie=cookie, csrf=csrf)
        self.assertEqual(code, 200, body)
        issued = json.loads(body)
        self.assertEqual(len(issued["codes"]), 2)
        first = issued["codes"][0]
        payload, sig = srv.parse_code(first["code"])
        self.assertTrue(srv.verify_p1363(self.master.public_key(), payload, sig))
        self.assertEqual(srv.code_id(payload), first["id"])

        codes = {c["id"]: c for c in json.loads(self.req("GET", "/admin/api/codes", cookie=cookie)[2])["codes"]}
        self.assertEqual(codes[first["id"]]["state"], "unused")
        self.assertEqual(codes[first["id"]]["customer"], "甲公司 <b>王工</b>")  # 原样存，前端用 textContent 显示

        # 看板签出来的码，直接走插件的激活协议
        activate = {"v": 1, "op": "activate", "payload": payload.hex(), "sig": sig.hex(), "machine": "abcdef0123",
                    "nonce": secrets.token_hex(16)}
        self.assertEqual(self.req("POST", "/api/v1/license", activate)[0], 200)
        detail = json.loads(self.req("GET", "/admin/api/codes/" + first["id"], cookie=cookie)[2])
        self.assertEqual((detail["state"], detail["machine"]), ("active", "abcdef0123"))
        self.assertEqual(detail["code"], first["code"])
        self.assertEqual(detail["events"][0]["result"], "bound")

        post = lambda action, data: json.loads(
            self.req("POST", "/admin/api/codes/%s/%s" % (first["id"], action), data, cookie=cookie, csrf=csrf)[2])
        self.assertEqual(post("revoke", {"note": "外传"})["state"], "revoked")
        self.assertEqual(post("unrevoke", {})["state"], "active")
        self.assertIsNone(post("unbind", {})["machine"])
        d = post("quota", {"quota": 5})
        self.assertEqual((d["quota"], d["quotaCustom"]), (5, True))
        self.assertFalse(post("quota", {"quota": None})["quotaCustom"])
        self.assertEqual(post("meta", {"customer": "乙公司", "note": ""})["customer"], "乙公司")
        actions = [a["action"] for a in post("meta", {"customer": "乙公司"})["audit"]]
        for expected in ("issue", "revoke", "unrevoke", "unbind", "quota", "meta"):
            self.assertIn(expected, actions)

        # 每日上限 5：已签 2，再签 4 个超限
        code, _, body = self.req("POST", "/admin/api/issue", {"plan": "M", "count": 4}, cookie=cookie, csrf=csrf)
        self.assertEqual(code, 429, body)
        self.assertEqual(self.req("POST", "/admin/api/issue", {"plan": "X"}, cookie=cookie, csrf=csrf)[0], 400)

    def test_import_ledger_rows(self):
        # 管理员工具台账里的码（testmaster 签发）用 testmaster 公钥导入
        with tempfile.TemporaryDirectory() as tmp:
            nums = repo_master().public_numbers()
            master_path = os.path.join(tmp, "m.json")
            with open(master_path, "w") as f:
                json.dump({"x": nums.x.to_bytes(32, "big").hex(), "y": nums.y.to_bytes(32, "big").hex()}, f)
            db = os.path.join(tmp, "i.db")
            tsv = os.path.join(ROOT, "tests", "fixtures", "license-test.ledger.tsv")
            srv.main(["--db", db, "import-ledger", tsv, "--master", master_path])
            ledger = srv.Ledger(db)
            rows = ledger.query("SELECT * FROM codes")
            self.assertGreater(len(rows), 0)
            self.assertTrue(all(r["code_text"] and r["source"] == "import" for r in rows))
            ledger.db.close()


if __name__ == "__main__":
    unittest.main()
