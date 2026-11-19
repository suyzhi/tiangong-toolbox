"""HTTP 请求分发层：挂接插件授权接口、版本更新下载与管理看板。"""
import json
import os
import sys
from http.server import BaseHTTPRequestHandler
from urllib.parse import unquote

from .codec import day_offset, stamp
from .service import MAX_BODY
from .updates import MAX_ZIP as UPDATE_MAX_ZIP


def make_handler(service, admin=None):
    class Handler(BaseHTTPRequestHandler):
        server_version = "TianGongLicense/1"
        sys_version = ""

        def client_ip(self):
            if service.trust_proxy:
                # 经 Cloudflare Tunnel 进来：CF-Connecting-IP 是客户端真实 IP（X-Forwarded-For 可能被客户端自己塞值）
                real = self.headers.get("CF-Connecting-IP", "").strip()
                if real:
                    return real
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
                # 发布新版本（看板上传安装包）放宽到安装包的上限，其余管理请求仍是小 JSON
                limit = UPDATE_MAX_ZIP if self.path.split("?")[0] == "/admin/api/updates" else MAX_BODY * 4
                if length < 0 or length > limit:
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
            if self.path == "/api/v1/update" or self.path.startswith("/api/v1/update?"):
                # 插件问「有没有新版本」：回签过名的清单（插件自己比版本号），没发布过回 404
                signed = service.updates.latest() if service.updates else None
                if not signed:
                    return self.reply(404, {"error": "还没有发布版本"})
                return self.reply(200, signed)
            if self.path.startswith("/api/v1/update/file/"):
                return self.send_update_file(unquote(self.path[len("/api/v1/update/file/"):].split("?")[0]))
            self.reply(404, {"error": "not found"})

        def send_update_file(self, name):
            path = service.updates.file_path(name) if service.updates else None
            if path is None:
                return self.reply(404, {"error": "没有这个安装包"})
            size = os.path.getsize(path)
            self.send_response(200)
            self.send_header("Content-Type", "application/zip")
            self.send_header("Content-Length", str(size))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            with open(path, "rb") as f:
                while True:
                    chunk = f.read(1 << 16)
                    if not chunk:
                        break
                    self.wfile.write(chunk)

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
