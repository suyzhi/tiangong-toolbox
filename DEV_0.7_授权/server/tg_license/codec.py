"""激活码 / 载荷格式与时间编解码（与插件 C# LicenseCodec / LicensePayload / LicenseTime 逐字节一致）。"""
import base64
import hashlib
import hmac
import os
import sys
import time
from datetime import datetime, timezone

ALPHABET = "0123456789abcdefghjkmnpqrstvwxyz"
EPOCH = datetime(2020, 1, 1, tzinfo=timezone.utc).timestamp()
# 与 src/License/LicensePlans.cs 一致：档位编号 → (名称, 天数)
PLANS = {11: ("一个月", 30), 27: ("半年", 183), 42: ("一年", 365)}
PAYLOAD_SIZE = 27

CHECK_SALT = b"TGS-CHK-2026-01"
BODY_GROUPS = 19
CODE_CHARS = (BODY_GROUPS + 1) * 8
PLAN_LETTERS = {"M": 11, "H": 27, "Y": 42}
LETTER_OF = {v: k for k, v in PLAN_LETTERS.items()}


def day_offset(now=None):
    """距 2020-01-01 UTC 的天数，对应 LicenseTime.ToDayOffset。"""
    days = ((time.time() if now is None else now) - EPOCH) / 86400.0
    return max(int(days // 1), 0)


def date_text(day):
    """天数偏移 → YYYY-MM-DD（UTC）。"""
    return datetime.fromtimestamp(EPOCH + day * 86400, timezone.utc).strftime("%Y-%m-%d")


def parse_date(text):
    """YYYY-MM-DD → 天数偏移（CLI 用，格式错误退出）。"""
    try:
        d = datetime.strptime(text, "%Y-%m-%d").replace(tzinfo=timezone.utc)
    except ValueError:
        sys.exit("日期格式应为 YYYY-MM-DD：" + text)
    return day_offset(d.timestamp())


def stamp():
    return datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def encode_group(five):
    """5 字节 → 8 个字符，对应 LicenseCodec.EncodeGroup。"""
    word = int.from_bytes(five, "big")
    return "".join(ALPHABET[(word >> (5 * (7 - i))) & 31] for i in range(8))


def _decode_group(chars):
    word = 0
    for c in chars:
        word = (word << 5) | ALPHABET.index(c)
    return word.to_bytes(5, "big")


def code_id(payload):
    """码ID = 载荷 SHA-256 前 5 字节，对应 LicenseCodec.IdOf。"""
    return encode_group(hashlib.sha256(payload).digest()[:5])


def normalize_id(text):
    """对应 LicenseCodec.NormalizeId：去分隔符、转小写、纠正 o→0、i/l→1。"""
    out = []
    for c in (text or "").strip():
        if c in "-_" or c.isspace():
            continue
        c = c.lower()
        c = "0" if c == "o" else ("1" if c in "il" else c)
        out.append(c)
    return "".join(out)


def display_id(cid):
    return cid[:4] + "-" + cid[4:] if cid and len(cid) > 4 else (cid or "")


def parse_payload(payload):
    """对应 LicensePayload.FromBytes；格式不对返回 None。"""
    if len(payload) != PAYLOAD_SIZE or payload[1:3] != b"TG" or payload[3] != 1:
        return None
    return {
        "flags": payload[0],
        "plan": payload[4],
        "day": int.from_bytes(payload[15:19], "big", signed=True),
    }


def key_stream(seed, length):
    out = b""
    counter = 0
    while len(out) < length:
        out += hashlib.sha256(seed.encode("ascii") + counter.to_bytes(4, "big")).digest()
        counter += 1
    return out[:length]


def unmask(mask_hex, value_b64, seed):
    """对应 LicenseKeyMaterial.Unmask：从 LicenseKeySlot.cs 的掩码常量还原公钥坐标。"""
    value = base64.b64decode(value_b64)
    mask = bytes.fromhex(mask_hex)
    if len(value) != 32 or len(mask) != 32:
        raise ValueError("公钥槽常量长度不对")
    stream = key_stream(seed, 32)
    return bytes(v ^ s ^ m for v, s, m in zip(value, stream, mask))


def _checksum(body):
    return hmac.new(CHECK_SALT, body, hashlib.sha256).digest()[:5]


def compose(payload, signature):
    """载荷 + 签名 → 160 字符激活码（每 5 字符一个 '-'）。"""
    body = payload + signature
    padded = body + bytes(BODY_GROUPS * 5 - len(body))
    text = "".join(encode_group(padded[g * 5:g * 5 + 5]) for g in range(BODY_GROUPS)) + encode_group(_checksum(body))
    return "-".join(text[i:i + 5] for i in range(0, len(text), 5))


def parse_code(text):
    """激活码全文 → (payload, signature)；格式或抄写校验不对返回 None。宽容规则与插件一致。"""
    chars = []
    for c in text or "":
        if len(chars) == CODE_CHARS:
            break
        if c in "-_　" or c.isspace():
            continue
        if "A" <= c <= "Z":
            c = c.lower()
        c = "0" if c == "o" else ("1" if c in "il" else c)
        if c not in ALPHABET:
            return None
        chars.append(c)
    if len(chars) != CODE_CHARS:
        return None
    body = b"".join(_decode_group(chars[g * 8:g * 8 + 8]) for g in range(BODY_GROUPS))[:PAYLOAD_SIZE + 64]
    if _decode_group(chars[BODY_GROUPS * 8:]) != _checksum(body):
        return None
    payload, signature = body[:PAYLOAD_SIZE], body[PAYLOAD_SIZE:]
    info = parse_payload(payload)
    if info is None or info["plan"] not in PLANS:
        return None
    return payload, signature


def new_payload(plan, day):
    """未绑定机器的通用码载荷（对应 LicenseAdminKey.Create 不带机器码的分支）。"""
    return bytes([0]) + b"TG" + bytes([1, plan]) + os.urandom(10) + day.to_bytes(4, "big") + os.urandom(8)
