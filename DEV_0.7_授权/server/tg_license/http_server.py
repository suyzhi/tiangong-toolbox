"""HTTP 请求分发层：挂接插件授权接口、版本更新下载与管理看板。"""
import ipaddress
import json
import os
import secrets
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import unquote, urlsplit

from .codec import day_offset, stamp
from .service import MAX_BODY
from .updates import MAX_ZIP as UPDATE_MAX_ZIP


class BoundedThreadingHTTPServer(ThreadingHTTPServer):
    """带并发线程上限的 HTTP 服务，防止慢速连接（Slowloris）耗尽线程与文件描述符。"""

    daemon_threads = True
    request_queue_size = 64

    def __init__(self, server_address, RequestHandlerClass, bind_and_activate=True, max_workers=64):
        self._worker_sem = threading.Semaphore(max_workers)
        super().__init__(server_address, RequestHandlerClass, bind_and_activate=bind_and_activate)

    def process_request(self, request, client_address):
        if not self._worker_sem.acquire(timeout=2.0):
            self.shutdown_request(request)
            return
        try:
            super().process_request(request, client_address)
        except Exception:
            self._worker_sem.release()
            raise

    def process_request_thread(self, request, client_address):
        try:
            super().process_request_thread(request, client_address)
        finally:
            self._worker_sem.release()


def _valid_ip(text):
    text = (text or "").strip()
    if not text or len(text) > 64:
        return None
    try:
        return str(ipaddress.ip_address(text))
    except ValueError:
        return None


def make_handler(service, admin=None):
    class Handler(BaseHTTPRequestHandler):
        server_version = "TianGongLicense/1"
        sys_version = ""
        # 单次连接读写超时，防止恶意客户端慢速发包永久占住工作线程
        timeout = 30

        def client_ip(self):
            if service.trust_proxy:
                # 经 Cloudflare Tunnel 进来时必然带 CF-Ray；直连其他反代若未剥离 CF-Connecting-IP，
                # 仅在同时存在 CF-Ray 时信任该头，避免客户端伪造绕过限流与锁定。
                if self.headers.get("CF-Ray", "").strip():
                    real = _valid_ip(self.headers.get("CF-Connecting-IP", ""))
                    if real:
                        return real
                forwarded = self.headers.get("X-Forwarded-For", "")
                if forwarded:
                    # 反代会把真实客户端 IP 追加在最右侧；取左侧 [0] 会被客户端伪造前置头绕过。
                    parts = [p.strip() for p in forwarded.split(",") if p.strip()]
                    if parts:
                        real = _valid_ip(parts[-1])
                        if real:
                            return real
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

        def _stream_to_temp(self, folder, length):
            """分块把上传包流式写入临时文件，避免 200MB 整包读入内存导致 OOM。"""
            os.makedirs(folder, exist_ok=True)
            tmp = os.path.join(
                folder,
                ".upload-%d-%d-%s.tmp" % (os.getpid(), threading.get_ident(), secrets.token_hex(4)),
            )
            remaining = length
            written = 0
            try:
                with open(tmp, "wb") as f:
                    while remaining > 0:
                        chunk = self.rfile.read(min(65536, remaining))
                        if not chunk:
                            break
                        f.write(chunk)
                        written += len(chunk)
                        remaining -= len(chunk)
            except Exception:
                if os.path.exists(tmp):
                    try:
                        os.remove(tmp)
                    except OSError:
                        pass
                raise
            return tmp, written

        def to_admin(self, method):
            # 网页看板：没开 --signing-key / --admin 时整段不存在
            if admin is None:
                return self.reply(404, {"error": "not found"})
            route = urlsplit(self.path).path
            ip = self.client_ip()
            body = b""
            if method == "POST":
                try:
                    length = int(self.headers.get("Content-Length", "0"))
                except ValueError:
                    length = -1
                if not route.startswith("/admin/api/"):
                    self.close_connection = True
                    return self.send_raw(*admin.json(404, {"error": "not found"}))
                # 登录接口以外的所有管理写请求：先验会话与 CSRF，再读请求体，
                # 彻底阻断未认证客户端向 /admin/api/updates 伪造大 Content-Length 打爆内存。
                if route != "/admin/api/login":
                    session = admin.session(self.headers)
                    if session is None:
                        self.close_connection = True
                        return self.send_raw(*admin.json(401, {"error": "请先登录"}))
                    if not admin.auth.verify_csrf(self.headers, session):
                        self.close_connection = True
                        return self.send_raw(*admin.json(403, {"error": "页面已过期，请刷新后重试"}))
                if route == "/admin/api/updates":
                    if service.updates is None:
                        self.close_connection = True
                        return self.send_raw(*admin.json(400, {"error": "服务器没有配置版本发布目录"}))
                    if length <= 0 or length > UPDATE_MAX_ZIP:
                        self.close_connection = True
                        return self.reply(413 if length > UPDATE_MAX_ZIP else 400, {"error": "请求体大小不对"})
                    tmp = None
                    try:
                        tmp, written = self._stream_to_temp(service.updates.folder, length)
                        if written != length:
                            self.close_connection = True
                            return self.reply(400, {"error": "上传中断"})
                        code, headers, data = admin.handle_update_upload(self.path, self.headers, tmp, written, ip)
                    except Exception as e:
                        sys.stderr.write("[%s] ADMIN ERROR %r\n" % (stamp(), e))
                        self.close_connection = True
                        return self.reply(500, {"error": "服务器内部错误"})
                    finally:
                        if tmp and os.path.exists(tmp):
                            try:
                                os.remove(tmp)
                            except OSError:
                                pass
                    return self.send_raw(code, headers, data)
                limit = MAX_BODY * 4
                if length < 0 or length > limit:
                    self.close_connection = True
                    return self.reply(413, {"error": "请求体大小不对"})
                body = self.rfile.read(length) if length > 0 else b""
            try:
                code, headers, data = admin.handle(method, self.path, self.headers, body, ip)
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
            self.send_header("X-Content-Type-Options", "nosniff")
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
                self.close_connection = True
                return self.reply(413 if length > MAX_BODY else 400, {"error": "请求体大小不对"})
            body = self.rfile.read(length)
            try:
                code, obj = service.handle(body, ip)
            except Exception as e:  # 不把内部异常细节回给客户端
                sys.stderr.write("[%s] ERROR %r\n" % (stamp(), e))
                code, obj = 500, {"error": "服务器内部错误"}
            self.reply(code, obj)

        def log_message(self, fmt, *args):
            # 过滤控制换行符，防止客户端构造路径注入伪造日志行
            msg = (fmt % args).replace("\r", "\\r").replace("\n", "\\n")
            sys.stderr.write("[%s] %s %s\n" % (stamp(), self.client_ip(), msg))

    return Handler
