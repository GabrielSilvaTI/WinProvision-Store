"""Canonical filenames for media stored beside catalog app records."""

from __future__ import annotations

import re
import unicodedata
from pathlib import PurePosixPath


def product_slug(package_id: str) -> str:
    product = package_id.rsplit(".", 1)[-1]
    normalized = unicodedata.normalize("NFKD", product.casefold())
    normalized = "".join(char for char in normalized if not unicodedata.combining(char))
    slug = re.sub(r"[^a-z0-9]+", "-", normalized).strip("-")
    return slug or "app"


def icon_filename(package_id: str, extension: str = "png") -> str:
    return f"{product_slug(package_id)}_icon.{extension.lower().lstrip('.')}"


def banner_filename(package_id: str, extension: str = "png") -> str:
    return f"{product_slug(package_id)}_banner.{extension.lower().lstrip('.')}"


def screenshot_filename(package_id: str, number: int, extension: str) -> str:
    return f"{product_slug(package_id)}_screenshot_{number:02d}.{extension.lower().lstrip('.')}"


def screenshot_slot(path: str) -> tuple[int, str | None] | None:
    filename = PurePosixPath(path).name
    match = re.search(r"_screenshot_(\d+)(?:_([^./]+))?\.[^.]+$", filename, re.IGNORECASE)
    if not match:
        legacy = re.match(r"(\d+)-([^.]+)\.[^.]+$", filename, re.IGNORECASE)
        return (int(legacy.group(1)), legacy.group(2).casefold()) if legacy else None
    return int(match.group(1)), match.group(2).casefold() if match.group(2) else None
