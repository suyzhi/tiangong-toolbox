"""授权管理看板兼容导出层（实现位于 tg_license/admin_auth.py 与 tg_license/admin_web.py）。"""
from tg_license.admin_auth import (
    PBKDF2_ROUNDS,
    SESSION_SECONDS,
    AdminAuth,
    check_password,
    hash_password,
)
from tg_license.admin_web import (
    ID_PATH,
    PAGE_CSP,
    SECURITY_HEADERS,
    STATIC_DIR,
    STATIC_FILES,
    AdminWeb,
    date_of,
)

__all__ = [
    "AdminAuth",
    "AdminWeb",
    "ID_PATH",
    "PAGE_CSP",
    "PBKDF2_ROUNDS",
    "SECURITY_HEADERS",
    "SESSION_SECONDS",
    "STATIC_DIR",
    "STATIC_FILES",
    "check_password",
    "date_of",
    "hash_password",
]
