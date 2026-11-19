#!/usr/bin/env python3
"""天工工具箱 联网授权服务端入口（薄壳兼容层，核心实现已拆分至 tg_license/ 包）。

保留全部命令行子命令（serve / keygen / keygen-master / import-master / set-password /
import-ledger / list / show / revoke / unrevoke / unbind / quota / extend / publish /
releases / events）以及原模块对外符号，兼容现有 systemd 服务与 test_server.py。
"""
import re  # noqa: F401 - 兼容 test_server.py 中的 srv.re 引用

from tg_license.cli import (
    build_parser,
    cmd_events,
    cmd_extend,
    cmd_import_ledger,
    cmd_import_master,
    cmd_keygen,
    cmd_keygen_master,
    cmd_list,
    cmd_publish,
    cmd_quota,
    cmd_releases,
    cmd_revoke,
    cmd_serve,
    cmd_set_password,
    cmd_show,
    cmd_unbind,
    cmd_unrevoke,
    describe,
    main,
    open_ledger,
    require_row,
)
from tg_license.codec import (
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
from tg_license.crypto import (
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
from tg_license.http_server import make_handler
from tg_license.ledger import CODE_COLUMNS, SCHEMA, Ledger, expiry_day
from tg_license.service import HEX, MAX_BODY, Service
from tg_license.updates import MAX_ZIP as UPDATE_MAX_ZIP, Updates

__all__ = [
    "ALPHABET", "BODY_GROUPS", "CHECK_SALT", "CODE_CHARS", "CODE_COLUMNS",
    "EPOCH", "HEX", "LETTER_OF", "MAX_BODY", "PAYLOAD_SIZE", "PLAN_LETTERS",
    "PLANS", "SCHEMA", "TGKEY_MAGIC", "UPDATE_MAX_ZIP",
    "Ledger", "Service", "Updates",
    "build_parser", "cmd_events", "cmd_extend", "cmd_import_ledger",
    "cmd_import_master", "cmd_keygen", "cmd_keygen_master", "cmd_list",
    "cmd_publish", "cmd_quota", "cmd_releases", "cmd_revoke", "cmd_serve",
    "cmd_set_password", "cmd_show", "cmd_unbind", "cmd_unrevoke",
    "code_id", "compose", "date_text", "day_offset", "describe", "display_id",
    "encode_group", "expiry_day", "load_master", "load_server_key",
    "load_signing_key", "main", "make_handler", "master_slot_source",
    "new_payload", "normalize_id", "open_ledger", "parse_code", "parse_date",
    "parse_payload", "public_key", "re", "require_row", "same_public",
    "save_signing_key", "sign_p1363", "slot_source", "stamp", "unmask",
    "verify_p1363",
]


if __name__ == "__main__":
    main()
