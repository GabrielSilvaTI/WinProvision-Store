#!/usr/bin/env python3
"""Reutiliza a última geração validada quando todos os inputs são idênticos."""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

import boto3
from botocore.config import Config
from botocore.exceptions import ClientError
from catalog_v2 import cache_fingerprint

BUCKET = os.environ.get("R2_BUCKET") or "winprovision"
DEST_PREFIX = os.environ.get("R2_CATALOG_V2_PREFIX", "Store/Catalog").strip("/")
WINGET_REMOTE = "https://github.com/microsoft/winget-pkgs.git"
REVISION_PATTERN = re.compile(r"^[0-9a-f]{40,64}$")


def object_bytes(client, key: str) -> bytes | None:
    try:
        response = client.get_object(Bucket=BUCKET, Key=key)
        return response["Body"].read()
    except ClientError as exc:
        if exc.response.get("Error", {}).get("Code") in {"404", "NoSuchKey", "NotFound"}:
            return None
        raise


def winget_revision() -> str | None:
    try:
        result = subprocess.run(
            ["git", "ls-remote", WINGET_REMOTE, "refs/heads/master"],
            check=True,
            capture_output=True,
            text=True,
            timeout=45,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        print(f"Aviso: não foi possível consultar a revisão do WinGet; cache desativado: {exc}", file=sys.stderr)
        return None
    revision = result.stdout.split(maxsplit=1)[0].lower() if result.stdout.strip() else ""
    return revision if REVISION_PATTERN.fullmatch(revision) else None


def main() -> int:
    env_file = os.environ.get("BASH_ENV")
    if not env_file:
        print("BASH_ENV não foi definido pelo CircleCI", file=sys.stderr)
        return 2

    account_id = os.environ["R2_ACCOUNT_ID"]
    client = boto3.client(
        "s3",
        endpoint_url=f"https://{account_id}.r2.cloudflarestorage.com",
        aws_access_key_id=os.environ["R2_ACCESS_KEY_ID"],
        aws_secret_access_key=os.environ["R2_SECRET_ACCESS_KEY"],
        config=Config(signature_version="s3v4", retries={"max_attempts": 5, "mode": "standard"}),
        region_name="auto",
    )

    source_revision = winget_revision()
    build_revision = os.environ.get("CIRCLE_SHA1", "").lower()
    if not REVISION_PATTERN.fullmatch(build_revision):
        build_revision = ""

    output_dir = Path("catalog-output")
    output_dir.mkdir(parents=True, exist_ok=True)

    manifest_bytes = object_bytes(client, f"{DEST_PREFIX}/manifest.json")
    published = None
    if manifest_bytes is not None:
        try:
            published = json.loads(manifest_bytes)
        except (UnicodeError, json.JSONDecodeError):
            pass

    fingerprint = cache_fingerprint(source_revision, build_revision, None)
    try:
        app_count = int(published.get("appCount", 0)) if isinstance(published, dict) else 0
    except (TypeError, ValueError):
        app_count = 0
    published_digest = str(published.get("catalogSha256", "")) if isinstance(published, dict) else ""
    cache_hit = bool(
        fingerprint
        and isinstance(published, dict)
        and published.get("schemaVersion") == 2
        and app_count >= 5000
        and re.fullmatch(r"[0-9a-f]{64}", published_digest)
        and re.fullmatch(r"[0-9a-f]{64}", str(published.get("indexSha256", "")))
        and published.get("inputFingerprint") == fingerprint
    )

    values = {
        "CATALOG_CACHE_HIT": "true" if cache_hit else "false",
        "CATALOG_NEEDS_REINDEX": "true",
        "WINGET_SOURCE_REVISION": source_revision or "",
        "CATALOG_BUILD_REVISION": build_revision,
    }
    with Path(env_file).open("a", encoding="utf-8") as stream:
        for name, value in values.items():
            stream.write(f"export {name}={value}\n")

    if cache_hit:
        # Um cache miss anterior pode ter salvo um apps.json intermediário sem
        # o conjunto derivado usado na publicação. Evite tratá-lo como completo.
        shutil.rmtree(output_dir, ignore_errors=True)
        output_dir.mkdir(parents=True, exist_ok=True)
        print(
            f"Cache hit: catálogo validado para WinGet {source_revision[:12]}, "
            f"build {build_revision[:12]} e {app_count:,} apps; geração ignorada."
        )
    else:
        print("Cache miss: um input mudou ou não há manifesto V2 publicado; a geração será executada.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
