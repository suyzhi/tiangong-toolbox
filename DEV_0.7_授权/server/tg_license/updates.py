"""版本发布与自动更新（0.9.x）：插件启动时问一次「有没有新版本」，有就提示用户下载安装。

服务器上一个目录（默认 /opt/tg-license/updates）：
  latest.json   当前发布的版本：{"manifest": base64(清单 JSON), "sig": base64(签名)}
  history.jsonl 每次发布记一行
  *.zip         安装包（make-installer.ps1 打出来的那个 zip，原样上传）

清单用**服务端回执私钥**（server.key）签名，插件只认内嵌服务端公钥（LicenseServerSlot）签过的清单 ——
改 hosts、中间人、换安装包都没用：安装包的 SHA-256 写在签过名的清单里，下载完对不上就不装。
清单里带 "kind": "update"，和授权回执分开（回执里没有这个字段，也缺回执必需的字段，互相冒充不了）。
"""
import base64
import contextlib
import hashlib
import json
import os
import re
import secrets
import shutil
import threading
import zipfile
from datetime import datetime

try:
    import fcntl
except ImportError:
    fcntl = None

try:
    import msvcrt
except ImportError:
    msvcrt = None

# 使用 \Z 而非 $，拒绝末尾带换行符的绕过字符串
VERSION = re.compile(r"^\d{1,4}\.\d{1,4}\.\d{1,6}\Z")
# 安装包文件名：make-installer.ps1 的「天工工具箱_DEV0.9.2_安装包_20261030.zip」，或任何字母数字中文下划线点横线
SAFE_NAME = re.compile(r"^[0-9A-Za-z_一-鿿][0-9A-Za-z_.\-一-鿿]{0,120}\.zip\Z")
NAME_VERSION = re.compile(r"DEV(\d+\.\d+\.\d+)")
MAX_ZIP = 200 * 1024 * 1024
MAX_UNCOMPRESSED = 512 * 1024 * 1024
MAX_ENTRIES = 10000
MAX_NOTES = 4000
REQUIRED = ("payload/TianGongCadSuite.dll", "tools/install.ps1")


@contextlib.contextmanager
def _publish_file_lock(folder):
    """跨进程发布锁：防止命令行 publish 与网页 serve 并发改写 latest.json / history.jsonl。"""
    lock_path = os.path.join(folder, ".publish.lock")
    f = open(lock_path, "a+b")
    try:
        if fcntl is not None:
            fcntl.flock(f.fileno(), fcntl.LOCK_EX)
        elif msvcrt is not None:
            f.seek(0, os.SEEK_END)
            if f.tell() == 0:
                f.write(b"0")
                f.flush()
            f.seek(0)
            msvcrt.locking(f.fileno(), msvcrt.LK_LOCK, 1)
        yield
    finally:
        try:
            if fcntl is not None:
                fcntl.flock(f.fileno(), fcntl.LOCK_UN)
            elif msvcrt is not None:
                f.seek(0)
                msvcrt.locking(f.fileno(), msvcrt.LK_UNLCK, 1)
        except OSError:
            pass
        f.close()


def _is_safe_zip_entry(entry_name):
    if not entry_name or any(ord(c) < 32 for c in entry_name):
        return False
    if entry_name.startswith("/") or entry_name.startswith("\\") or ":" in entry_name:
        return False
    parts = entry_name.replace("\\", "/").split("/")
    return ".." not in parts


def _sha256_file(path):
    h = hashlib.sha256()
    total = 0
    with open(path, "rb") as f:
        while True:
            chunk = f.read(65536)
            if not chunk:
                break
            h.update(chunk)
            total += len(chunk)
    return h.hexdigest(), total


def version_key(text):
    return tuple(int(x) for x in text.split("."))


def version_from_name(name):
    m = NAME_VERSION.search(name or "")
    return m.group(1) if m else None


class Updates:
    def __init__(self, folder, server_key, sign):
        """sign(key, data) -> 64 字节 P1363 签名（tg_license.crypto.sign_p1363）"""
        self.folder = folder
        self.key = server_key
        self.sign = sign
        self.lock = threading.Lock()
        os.makedirs(folder, exist_ok=True)

    # ------------------------------------------------------------ 查询

    def latest(self):
        """当前发布的版本：{"manifest": b64, "sig": b64}；还没发布过返回 None"""
        path = os.path.join(self.folder, "latest.json")
        try:
            with open(path, encoding="utf-8") as f:
                return json.load(f)
        except (OSError, ValueError):
            return None

    def latest_manifest(self):
        signed = self.latest()
        if not signed:
            return None
        try:
            return json.loads(base64.b64decode(signed["manifest"]).decode("utf-8"))
        except (KeyError, ValueError):
            return None

    def history(self, limit=20):
        path = os.path.join(self.folder, "history.jsonl")
        rows = []
        try:
            with open(path, encoding="utf-8") as f:
                for line in f:
                    try:
                        rows.append(json.loads(line))
                    except ValueError:
                        pass
        except OSError:
            pass
        return list(reversed(rows))[:limit]

    def file_path(self, name):
        """下载：只认 updates 目录里、名字合法的 zip"""
        if not SAFE_NAME.match(name or ""):
            return None
        path = os.path.join(self.folder, name)
        return path if os.path.isfile(path) else None

    # ------------------------------------------------------------ 发布

    def publish(self, data, name, version, notes, by="cli"):
        """发布一个版本（字节输入封装）：落入唯一临时文件后复用 publish_file。"""
        if not data or len(data) > MAX_ZIP:
            raise ValueError("安装包大小不对（0 或超过 200MB）")
        tmp = os.path.join(
            self.folder,
            ".upload-%d-%d-%s.tmp" % (os.getpid(), threading.get_ident(), secrets.token_hex(4)),
        )
        try:
            with open(tmp, "wb") as f:
                f.write(data)
            return self.publish_file(tmp, name, version, notes, by=by, size=len(data))
        finally:
            if os.path.exists(tmp):
                try:
                    os.remove(tmp)
                except OSError:
                    pass

    def publish_file(self, tmp_path, name, version, notes, by="cli", size=None):
        """发布一个已落盘的安装包：先检查 Zip 路径与解压上限防 Zip Bomb → 验 CRC → 跨进程加锁原子发布。"""
        name = os.path.basename((name or "").replace("\\", "/"))
        if not SAFE_NAME.match(name):
            raise ValueError("安装包文件名不合规（只能有字母数字、中文、下划线、点、横线，以 .zip 结尾）：" + name)
        raw_version = version or version_from_name(name) or ""
        if "\r" in raw_version or "\n" in raw_version:
            raise ValueError("版本号应形如 0.9.2（文件名里没认出来，请填写）")
        version = raw_version.strip()
        if not VERSION.match(version):
            raise ValueError("版本号应形如 0.9.2（文件名里没认出来，请填写）")
        file_size = os.path.getsize(tmp_path) if size is None else size
        if file_size <= 0 or file_size > MAX_ZIP:
            raise ValueError("安装包大小不对（0 或超过 200MB）")
        notes = (notes or "").strip()[:MAX_NOTES]

        try:
            with zipfile.ZipFile(tmp_path) as z:
                infos = z.infolist()
                if len(infos) > MAX_ENTRIES:
                    raise ValueError("zip 里文件数量过多")
                total_uncompressed = 0
                names = set()
                for info in infos:
                    if not _is_safe_zip_entry(info.filename):
                        raise ValueError("zip 里有不安全的路径")
                    total_uncompressed += max(0, info.file_size)
                    if total_uncompressed > MAX_UNCOMPRESSED:
                        raise ValueError("zip 解压后体积超过上限（512MB）")
                    names.add(info.filename)
                missing = [r for r in REQUIRED if r not in names]
                if missing:
                    raise ValueError("这不像天工工具箱安装包（缺 %s）。请上传 make-installer.ps1 打出来的 zip。" % "、".join(missing))
                # 在确认条目路径安全且未超过解压体积上限后，再执行 CRC 全量解压校验
                bad = z.testzip()
        except zipfile.BadZipFile:
            raise ValueError("不是有效的 zip 文件")
        if bad:
            raise ValueError("zip 里的文件损坏：" + bad)

        digest, actual_size = _sha256_file(tmp_path)
        if actual_size <= 0 or actual_size > MAX_ZIP:
            raise ValueError("安装包大小不对（0 或超过 200MB）")

        with self.lock, _publish_file_lock(self.folder):
            current = self.latest_manifest()
            final = os.path.join(self.folder, name)
            try:
                os.replace(tmp_path, final)
            except OSError:
                # 跨文件系统兜底：先拷到同目录唯一临时文件再原子替换
                staged = os.path.join(
                    self.folder,
                    ".stage-%d-%d-%s.tmp" % (os.getpid(), threading.get_ident(), secrets.token_hex(4)),
                )
                try:
                    shutil.copyfile(tmp_path, staged)
                    os.replace(staged, final)
                finally:
                    if os.path.exists(staged):
                        try:
                            os.remove(staged)
                        except OSError:
                            pass
            manifest = {
                "v": 1, "kind": "update", "version": version,
                "date": datetime.now().strftime("%Y-%m-%d"), "notes": notes,
                "file": name, "size": actual_size, "sha256": digest,
            }
            raw = json.dumps(manifest, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
            signed = {
                "manifest": base64.b64encode(raw).decode("ascii"),
                "sig": base64.b64encode(self.sign(self.key, raw)).decode("ascii"),
            }
            path = os.path.join(self.folder, "latest.json")
            latest_tmp = "%s.%d.%d.%s.tmp" % (path, os.getpid(), threading.get_ident(), secrets.token_hex(4))
            try:
                with open(latest_tmp, "w", encoding="utf-8") as f:
                    json.dump(signed, f)
                os.replace(latest_tmp, path)
            finally:
                if os.path.exists(latest_tmp):
                    try:
                        os.remove(latest_tmp)
                    except OSError:
                        pass
            with open(os.path.join(self.folder, "history.jsonl"), "a", encoding="utf-8") as f:
                f.write(json.dumps(dict(manifest, at=datetime.now().strftime("%Y-%m-%d %H:%M:%S"), by=by,
                                        replaced=current and current.get("version")), ensure_ascii=False) + "\n")
            return manifest
