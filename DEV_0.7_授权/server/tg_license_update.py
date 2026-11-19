"""版本发布与自动更新兼容导出层（实现位于 tg_license/updates.py）。"""
from tg_license.updates import (
    MAX_NOTES,
    MAX_ZIP,
    NAME_VERSION,
    REQUIRED,
    SAFE_NAME,
    VERSION,
    Updates,
    version_from_name,
    version_key,
)

__all__ = [
    "MAX_NOTES",
    "MAX_ZIP",
    "NAME_VERSION",
    "REQUIRED",
    "SAFE_NAME",
    "VERSION",
    "Updates",
    "version_from_name",
    "version_key",
]
