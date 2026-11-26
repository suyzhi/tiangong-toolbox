"""P-256 ECDSA P1363 签验、.tgkey / PEM 密钥读写与 C# 公钥槽代码生成。"""
import base64
import json
import os

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.asymmetric.utils import decode_dss_signature, encode_dss_signature

from .codec import key_stream

TGKEY_MAGIC = "TGLK-1"


def public_key(x, y):
    """坐标 → P-256 公钥；点不在曲线上会抛 ValueError（顺带验证了还原结果）。"""
    return ec.EllipticCurvePublicNumbers(
        int.from_bytes(x, "big"), int.from_bytes(y, "big"), ec.SECP256R1()
    ).public_key()


def verify_p1363(key, data, signature):
    if len(signature) != 64:
        return False
    der = encode_dss_signature(int.from_bytes(signature[:32], "big"), int.from_bytes(signature[32:], "big"))
    try:
        key.verify(der, data, ec.ECDSA(hashes.SHA256()))
        return True
    except InvalidSignature:
        return False


def sign_p1363(key, data):
    r, s = decode_dss_signature(key.sign(data, ec.ECDSA(hashes.SHA256())))
    return r.to_bytes(32, "big") + s.to_bytes(32, "big")


def same_public(a, b):
    return a.public_numbers() == b.public_numbers()


def load_signing_key(path):
    """读 .tgkey：TGLK-1 / 密钥ID / base64(CNG ECCPRIVATEBLOB 104 字节 + X + Y)。返回 (密钥ID, 私钥)。"""
    with open(path, encoding="utf-8-sig") as f:
        lines = f.read().split("\n")
    if len(lines) < 3 or lines[0].strip() != TGKEY_MAGIC:
        raise ValueError("签发私钥文件格式不对：" + path)
    blob = base64.b64decode(lines[2].strip())[:104]
    if len(blob) != 104 or blob[:4] != b"ECS2" or int.from_bytes(blob[4:8], "little") != 32:
        raise ValueError("签发私钥文件内容不对：" + path)
    key = ec.derive_private_key(int.from_bytes(blob[72:104], "big"), ec.SECP256R1())
    nums = key.public_key().public_numbers()
    if nums.x.to_bytes(32, "big") != blob[8:40] or nums.y.to_bytes(32, "big") != blob[40:72]:
        raise ValueError("签发私钥文件里的公钥与私钥不配对：" + path)
    return lines[1].strip() or "master", key


def save_signing_key(path, key_id, key):
    nums = key.public_key().public_numbers()
    x, y = nums.x.to_bytes(32, "big"), nums.y.to_bytes(32, "big")
    d = key.private_numbers().private_value.to_bytes(32, "big")
    blob = b"ECS2" + (32).to_bytes(4, "little") + x + y + d
    text = TGKEY_MAGIC + "\n" + key_id + "\n" + base64.b64encode(blob + x + y).decode("ascii") + "\n"
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def load_master(path):
    with open(path, encoding="utf-8") as f:
        info = json.load(f)
    return public_key(bytes.fromhex(info["x"]), bytes.fromhex(info["y"]))


def load_server_key(path):
    with open(path, "rb") as f:
        return serialization.load_pem_private_key(f.read(), password=None)


def master_slot_source(key_id, x, y):
    """插件公钥槽 LicenseKeySlot.cs，与 TianGongLicenseAdmin keygen 一致：生成随机掩码并对坐标异或，兼容客户端现有 Unmask 解码。"""
    seed_a, seed_b = "TGS-SLOT-A-" + key_id, "TGS-SLOT-B-" + key_id
    mask_a, mask_b = os.urandom(32), os.urandom(32)
    stream_a, stream_b = key_stream(seed_a, 32), key_stream(seed_b, 32)
    masked_x = bytes(c ^ s ^ m for c, s, m in zip(x, stream_a, mask_a))
    masked_y = bytes(c ^ s ^ m for c, s, m in zip(y, stream_b, mask_b))
    return (
        "// 由 TianGongLicenseAdmin keygen 生成，请勿手工修改。密钥ID：" + key_id + "\n"
        "// 这是公钥，可以随源码一起提交；私钥在同名的 .tgkey 文件里，绝不能提交。\n"
        "namespace TianGongCadSuite.Licensing {\n"
        "    internal static class LicenseKeySlot {\n"
        '        internal const string KeyId = "' + key_id + '";\n'
        '        internal static readonly string MaskX = "' + mask_a.hex() + '";\n'
        '        internal static readonly string MaskY = "' + mask_b.hex() + '";\n'
        '        internal static readonly string SeedX = "' + seed_a + '";\n'
        '        internal static readonly string SeedY = "' + seed_b + '";\n'
        '        internal static readonly string ValueX = "' + base64.b64encode(masked_x).decode("ascii") + '";\n'
        '        internal static readonly string ValueY = "' + base64.b64encode(masked_y).decode("ascii") + '";\n'
        "    }\n"
        "}\n"
    )


def slot_source(url, key_id, x, y):
    return (
        "// 由 server/tg_license_server.py keygen 生成，请勿手工修改。密钥ID：" + key_id + "\n"
        "// 联网授权：服务端地址 + 服务端回执公钥。公钥可以提交；服务端私钥只放在服务器上。\n"
        "namespace TianGongCadSuite.Licensing {\n"
        "    internal static class LicenseServerSlot {\n"
        '        internal const string KeyId = "' + key_id + '";\n'
        '        internal const string Url = "' + url + '";\n'
        '        internal const string KeyX = "' + x.hex() + '";\n'
        '        internal const string KeyY = "' + y.hex() + '";\n'
        "    }\n"
        "}\n"
    )
