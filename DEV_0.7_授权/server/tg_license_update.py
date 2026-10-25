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
import hashlib
import json
import os
import re
import threading
import zipfile
from datetime import datetime

VERSION = re.compile(r"^\d{1,4}\.\d{1,4}\.\d{1,6}$")
# 安装包文件名：make-installer.ps1 的「天工工具箱_DEV0.9.2_安装包_20261030.zip」，或任何字母数字中文下划线点横线
SAFE_NAME = re.compile(r"^[0-9A-Za-z_一-鿿][0-9A-Za-z_.\-一-鿿]{0,120}\.zip$")
NAME_VERSION = re.compile(r"DEV(\d+\.\d+\.\d+)")
MAX_ZIP = 200 * 1024 * 1024
MAX_NOTES = 4000
REQUIRED = ("payload/TianGongCadSuite.dll", "tools/install.ps1")


def version_key(text):
    return tuple(int(x) for x in text.split("."))


def version_from_name(name):
    m = NAME_VERSION.search(name or "")
    return m.group(1) if m else None


class Updates:
    def __init__(self, folder, server_key, sign):
        """sign(key, data) -> 64 字节 P1363 签名（tg_license_server.sign_p1363）"""
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
        """发布一个版本：校验安装包 → 存盘 → 写签名清单。返回清单 dict；不合格抛 ValueError（消息给人看）"""
        name = os.path.basename(name or "")
        if not SAFE_NAME.match(name):
            raise ValueError("安装包文件名不合规（只能有字母数字、中文、下划线、点、横线，以 .zip 结尾）：" + name)
        version = (version or version_from_name(name) or "").strip()
        if not VERSION.match(version):
            raise ValueError("版本号应形如 0.9.2（文件名里没认出来，请填写）")
        if not data or len(data) > MAX_ZIP:
            raise ValueError("安装包大小不对（0 或超过 200MB）")
        notes = (notes or "").strip()[:MAX_NOTES]
        tmp = os.path.join(self.folder, ".upload-%d.tmp" % threading.get_ident())
        with open(tmp, "wb") as f:
            f.write(data)
        try:
            try:
                with zipfile.ZipFile(tmp) as z:
                    names = set(z.namelist())
                    bad = z.testzip()
            except zipfile.BadZipFile:
                raise ValueError("不是有效的 zip 文件")
            if bad:
                raise ValueError("zip 里的文件损坏：" + bad)
            missing = [r for r in REQUIRED if r not in names]
            if missing:
                raise ValueError("这不像天工工具箱安装包（缺 %s）。请上传 make-installer.ps1 打出来的 zip。" % "、".join(missing))
            if any(n.startswith("/") or ".." in n.replace("\\", "/").split("/") for n in names):
                raise ValueError("zip 里有不安全的路径")
            with self.lock:
                current = self.latest_manifest()
                final = os.path.join(self.folder, name)
                os.replace(tmp, final)
                manifest = {
                    "v": 1, "kind": "update", "version": version,
                    "date": datetime.now().strftime("%Y-%m-%d"), "notes": notes,
                    "file": name, "size": len(data), "sha256": hashlib.sha256(data).hexdigest(),
                }
                raw = json.dumps(manifest, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
                signed = {"manifest": base64.b64encode(raw).decode("ascii"),
                          "sig": base64.b64encode(self.sign(self.key, raw)).decode("ascii")}
                path = os.path.join(self.folder, "latest.json")
                with open(path + ".tmp", "w", encoding="utf-8") as f:
                    json.dump(signed, f)
                os.replace(path + ".tmp", path)
                with open(os.path.join(self.folder, "history.jsonl"), "a", encoding="utf-8") as f:
                    f.write(json.dumps(dict(manifest, at=datetime.now().strftime("%Y-%m-%d %H:%M:%S"), by=by,
                                            replaced=current and current.get("version")), ensure_ascii=False) + "\n")
                return manifest
        finally:
            if os.path.exists(tmp):
                os.remove(tmp)
