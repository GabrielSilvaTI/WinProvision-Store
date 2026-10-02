#!/usr/bin/env python3
"""Build a compact package-ID screenshot index from existing R2 objects.

The script lists objects under the screenshot prefix in paginated S3 requests.
It does not crawl package websites, download images, or rewrite apps.json.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
import unicodedata
from collections import defaultdict
from datetime import UTC, datetime
from pathlib import Path
from urllib.parse import quote, unquote

import boto3
from botocore.config import Config
from botocore.exceptions import BotoCoreError, ClientError

DEFAULT_PUBLIC_BASE = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev"
DEFAULT_PREFIX = "Store/Screenshot_Database/"
IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"}
MAX_SCREENSHOTS_PER_APP = 12


def normalize_id(value: str) -> str:
    normalized = unicodedata.normalize("NFKD", value.casefold())
    normalized = "".join(char for char in normalized if not unicodedata.combining(char))
    return re.sub(r"[^a-z0-9._-]+", "-", normalized).strip(".-_")


def make_r2_client():
    return boto3.client(
        "s3",
        endpoint_url=f"https://{os.environ['R2_ACCOUNT_ID']}.r2.cloudflarestorage.com",
        aws_access_key_id=os.environ["R2_ACCESS_KEY_ID"],
        aws_secret_access_key=os.environ["R2_SECRET_ACCESS_KEY"],
        config=Config(
            signature_version="s3v4",
            retries={"max_attempts": 5, "mode": "standard"},
            max_pool_connections=4,
        ),
        region_name="auto",
    )


def object_package(key: str, prefix: str) -> tuple[str, str] | None:
    parts = key[len(prefix) :].split("/")
    if len(parts) == 2:
        package_folder, filename = parts
        source_kind = "curated"
    elif len(parts) == 3 and parts[0].casefold() == "homepage":
        _, package_folder, filename = parts
        source_kind = "homepage"
    else:
        return None
    if Path(filename).suffix.casefold() not in IMAGE_EXTENSIONS:
        return None
    package_id = normalize_id(unquote(package_folder))
    return (source_kind, package_id) if package_id else None


def build_index(client, bucket: str, prefix: str, public_base: str) -> dict:
    grouped: dict[str, dict[str, list[str]]] = defaultdict(lambda: {"winget": [], "homepage": []})
    listed_objects = 0
    object_folders: set[tuple[str, str]] = set()
    paginator = client.get_paginator("list_objects_v2")

    for page in paginator.paginate(Bucket=bucket, Prefix=prefix):
        for item in page.get("Contents", []):
            key = item.get("Key")
            if not isinstance(key, str):
                continue
            parsed = object_package(key, prefix)
            if not parsed:
                continue
            listed_objects += 1
            source_kind, package_id = parsed
            url = f"{public_base.rstrip('/')}/{quote(key, safe='/-._~')}"
            kind = "winget" if source_kind == "curated" else "homepage"
            urls = grouped[package_id][kind]
            if url not in urls and len(urls) < MAX_SCREENSHOTS_PER_APP:
                urls.append(url)
            object_folders.add((source_kind, package_id))

    packages = {
        package_id: {
            source: urls
            for source, urls in sorted(sources.items())
            if urls
        }
        for package_id, sources in sorted(grouped.items())
        if any(sources.values())
    }
    if not listed_objects:
        raise ValueError(f"Nenhuma imagem encontrada sob o prefixo {prefix!r}; índice anterior preservado")
    if not packages:
        raise ValueError("Nenhum objeto de imagem válido foi localizado; índice anterior preservado")

    screenshot_count = sum(len(urls) for sources in packages.values() for urls in sources.values())
    return {
        "document": {
            "schemaVersion": 1,
            "packageCount": len(packages),
            "screenshotCount": screenshot_count,
            "packages": packages,
        },
        "report": {
            "status": "ok",
            "objectsListed": listed_objects,
            "objectFolders": len(object_folders),
            "packagesIndexed": len(packages),
            "screenshotsIndexed": screenshot_count,
            "r2ListPrefix": prefix,
        },
    }


def write_json_atomic(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    temporary.replace(path)


def main() -> int:
    started = time.perf_counter()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="arquivo de índice de screenshots separado")
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--prefix", default=DEFAULT_PREFIX)
    parser.add_argument("--public-base", default=os.environ.get("R2_PUBLIC_BASE", DEFAULT_PUBLIC_BASE))
    args = parser.parse_args()
    if not args.prefix.endswith("/") or not args.public_base.startswith("https://"):
        parser.error("prefix precisa terminar com '/' e public-base precisa usar HTTPS")

    try:
        client = make_r2_client()
        indexed = build_index(
            client,
            os.environ.get("R2_BUCKET") or "winprovision",
            args.prefix,
            args.public_base,
        )
        indexed["report"]["durationSeconds"] = round(time.perf_counter() - started, 2)
        write_json_atomic(args.output, indexed["document"])
        write_json_atomic(args.report, indexed["report"])
        print(json.dumps(indexed["report"], ensure_ascii=False))
        return 0
    except (OSError, json.JSONDecodeError, ValueError, BotoCoreError, ClientError, KeyError) as exc:
        print(f"Índice de screenshots não gerado; arquivo anterior preservado: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
