#!/usr/bin/env python3
"""Carry published screenshot URLs forward when regenerating the app catalog."""

from __future__ import annotations

import json
import sys
from pathlib import Path
from urllib.parse import urlparse

PUBLIC_SCREENSHOT_BASE = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/Screenshot_Database/"


def read(path: Path):
    with path.open(encoding="utf-8-sig") as stream:
        value = json.load(stream)
    if not isinstance(value, list):
        raise ValueError(f"{path} deve conter uma lista JSON")
    return value


def read_screenshot_assets(path: Path | None) -> dict[str, list[str]]:
    if path is None or not path.is_file():
        return {}

    with path.open(encoding="utf-8-sig") as stream:
        payload = json.load(stream)
    assets = payload.get("assets", {}) if isinstance(payload, dict) else {}
    if not isinstance(assets, dict):
        raise ValueError(f"{path} precisa conter um objeto 'assets'")

    by_package: dict[str, list[str]] = {}
    for asset in assets.values():
        if not isinstance(asset, dict):
            continue
        package_id = asset.get("packageId")
        public_url = asset.get("publicUrl")
        if not isinstance(package_id, str) or not package_id.strip() or not isinstance(public_url, str):
            continue

        parsed = urlparse(public_url)
        if parsed.scheme != "https" or not public_url.startswith(PUBLIC_SCREENSHOT_BASE):
            continue

        urls = by_package.setdefault(package_id.casefold(), [])
        if public_url not in urls and len(urls) < 12:
            urls.append(public_url)
    return by_package


def main() -> int:
    if len(sys.argv) not in (3, 4):
        print(
            "Uso: preserve_catalog_screenshots.py <apps.json atual> <apps.json anterior> [screenshot-assets.json]",
            file=sys.stderr,
        )
        return 2
    current_path, previous_path = map(Path, sys.argv[1:3])
    assets_path = Path(sys.argv[3]) if len(sys.argv) == 4 else None
    try:
        current = read(current_path)
        previous = read(previous_path) if previous_path.is_file() else []
        screenshot_assets = read_screenshot_assets(assets_path)
    except (OSError, json.JSONDecodeError, ValueError) as exc:
        print(f"Não foi possível preservar screenshots: {exc}", file=sys.stderr)
        return 1

    old_by_id = {str(item.get("id", "")).casefold(): item for item in previous if isinstance(item, dict)}
    preserved = 0
    restored = 0
    for app in current:
        if not isinstance(app, dict):
            continue
        old = old_by_id.get(str(app.get("id", "")).casefold())
        package_key = str(app.get("id", "")).casefold()
        for field in ("screenshotUrls", "storeScreenshotUrls"):
            values = []
            candidates = [app.get(field) or []]
            if old:
                candidates.append(old.get(field) or [])
            if field == "screenshotUrls" and str(app.get("source", "winget")).casefold() == "winget":
                candidates.append(screenshot_assets.get(package_key, []))
            for group in candidates:
                if not isinstance(group, list):
                    continue
                for url in group:
                    if isinstance(url, str) and url and url not in values:
                        values.append(url)
            if values:
                app[field] = values
                if old and old.get(field):
                    preserved += 1
                if field == "screenshotUrls" and screenshot_assets.get(package_key):
                    restored += 1

    current_path.write_text(json.dumps(current, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    print(
        f"Capturas preservadas do catálogo anterior: {preserved} campo(s); reconstruídas do R2: {restored} pacote(s)."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
