"""天工工具箱 联网授权与在线更新服务端核心包。

模块分层：
  - codec.py       激活码 / 载荷编解码、日期偏移、公钥去掩码（与 C# 插件逐字节一致）
  - crypto.py      P-256 ECDSA P1363 签验、.tgkey / PEM / 公钥槽文件读写
  - ledger.py      SQLite 台账持久化与一码一机核心裁决状态机
  - service.py     插件授权 API 处理与限流
  - updates.py     安装包校验、落盘、签名清单与发布历史
  - admin_auth.py  管理看板口令、会话、CSRF 与登录失败锁定
  - admin_web.py   管理看板 /admin HTTP 接口与静态资源服务
  - http_server.py HTTP 路由与反代 IP 识别
  - cli.py         命令行入口与全部运维子命令
"""
from .codec import (
    ALPHABET,
    BODY_GROUPS,
    CHECK_SALT,
    CODE_CHARS,
    EPOCH,
    LETTER_OF,
    PAYLOAD_SIZE,
    PLAN_LETTERS,
    PLANS,
    code_id,
    compose,
    date_text,
    day_offset,
    display_id,
    encode_group,
    new_payload,
    normalize_id,
    parse_code,
    parse_date,
    parse_payload,
    stamp,
    unmask,
)
from .crypto import (
    TGKEY_MAGIC,
    load_master,
    load_server_key,
    load_signing_key,
    master_slot_source,
    public_key,
    same_public,
    save_signing_key,
    sign_p1363,
    slot_source,
    verify_p1363,
)
from .http_server import BoundedThreadingHTTPServer, make_handler
from .ledger import CODE_COLUMNS, SCHEMA, Ledger, expiry_day
from .service import HEX, MAX_BODY, Service
from .updates import MAX_ZIP as UPDATE_MAX_ZIP, Updates

__all__ = [
    "ALPHABET",
    "BODY_GROUPS",
    "CHECK_SALT",
    "CODE_CHARS",
    "CODE_COLUMNS",
    "EPOCH",
    "HEX",
    "LETTER_OF",
    "MAX_BODY",
    "PAYLOAD_SIZE",
    "PLAN_LETTERS",
    "PLANS",
    "SCHEMA",
    "TGKEY_MAGIC",
    "UPDATE_MAX_ZIP",
    "BoundedThreadingHTTPServer",
    "Ledger",
    "Service",
    "Updates",
    "code_id",
    "compose",
    "date_text",
    "day_offset",
    "display_id",
    "encode_group",
    "expiry_day",
    "load_master",
    "load_server_key",
    "load_signing_key",
    "make_handler",
    "master_slot_source",
    "new_payload",
    "normalize_id",
    "parse_code",
    "parse_date",
    "parse_payload",
    "public_key",
    "same_public",
    "save_signing_key",
    "sign_p1363",
    "slot_source",
    "stamp",
    "unmask",
    "verify_p1363",
]
