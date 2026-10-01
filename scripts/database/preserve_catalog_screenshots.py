#!/usr/bin/env python3
"""Carry published screenshot URLs forward when regenerating the app catalog."""

from __future__ import annotations

import json
import sys
from pathlib import Path


def read(path: Path):
    with path.open(encoding="utf-8-sig") as stream:
        value = json.load(stream)
    if not isinstance(value, list):
        raise ValueError(f"{path} deve conter uma lista JSON")
    return value


def main() -> int:
    if len(sys.argv) != 3:
        print("Uso: preserve_catalog_screenshots.py <apps.json atual> <apps.json anterior>", file=sys.stderr)
        return 2
    current_path, previous_path = map(Path, sys.argv[1:])
    try:
        current = read(current_path)
        previous = read(previous_path) if previous_path.is_file() else []
    except (OSError, json.JSONDecodeError, ValueError) as exc:
        print(f"Não foi possível preservar screenshots: {exc}", file=sys.stderr)
        return 1

    old_by_id = {str(item.get("id", "")).casefold(): item for item in previous if isinstance(item, dict)}
    preserved = 0
    for app in current:
        if not isinstance(app, dict):
            continue
        old = old_by_id.get(str(app.get("id", "")).casefold())
        if not old:
            continue
        for field in ("screenshotUrls", "storeScreenshotUrls"):
            values = []
            for url in (app.get(field) or [], old.get(field) or []):
                if isinstance(url, str) and url and url not in values:
                    values.append(url)
            if values:
                app[field] = values
                preserved += 1

    current_path.write_text(json.dumps(current, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    print(f"Capturas preservadas em {preserved} campo(s) de mídia.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
