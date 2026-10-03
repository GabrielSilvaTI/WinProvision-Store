#!/usr/bin/env python3
"""Validate and publish the hierarchical Store catalog to R2."""

from __future__ import annotations

import hashlib
import os
import sys
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

import boto3
from botocore.config import Config
from botocore.exceptions import ClientError
from catalog_v2 import validate_catalog_v2

SOURCE_DIR = Path(os.environ.get("CATALOG_V2_SOURCE", "catalog-v2"))
DEST_PREFIX = os.environ.get("R2_CATALOG_V2_PREFIX", "Store/Catalog/v2").strip("/")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    try:
        manifest, files = validate_catalog_v2(SOURCE_DIR)
    except (OSError, UnicodeError, ValueError) as exc:
        print(f"Catálogo hierárquico inválido; nada foi publicado: {exc}", file=sys.stderr)
        return 1

    account_id = os.environ["R2_ACCOUNT_ID"]
    client = boto3.client(
        "s3",
        endpoint_url=f"https://{account_id}.r2.cloudflarestorage.com",
        aws_access_key_id=os.environ["R2_ACCESS_KEY_ID"],
        aws_secret_access_key=os.environ["R2_SECRET_ACCESS_KEY"],
        config=Config(signature_version="s3v4", retries={"max_attempts": 5, "mode": "standard"}),
        region_name="auto",
    )
    bucket = os.environ.get("R2_BUCKET") or "winprovision"

    manifest_key = f"{DEST_PREFIX}/manifest.json"
    try:
        published = client.head_object(Bucket=bucket, Key=manifest_key)
    except ClientError as exc:
        if exc.response.get("Error", {}).get("Code") not in {"404", "NoSuchKey", "NotFound"}:
            raise
    else:
        if published.get("Metadata", {}).get("catalog-sha256") == manifest["catalogSha256"]:
            print(f"Catálogo v2 sem alterações em {DEST_PREFIX}; {manifest['appCount']} apps.")
            return 0

    def upload(path: Path) -> tuple[str, bool]:
        relative = path.relative_to(SOURCE_DIR).as_posix()
        key = f"{DEST_PREFIX}/{manifest['basePath']}/{relative}"
        digest = sha256_file(path)
        try:
            head = client.head_object(Bucket=bucket, Key=key)
            if head.get("Metadata", {}).get("sha256") == digest:
                return key, False
        except ClientError as exc:
            if exc.response.get("Error", {}).get("Code") not in {"404", "NoSuchKey", "NotFound"}:
                raise
        client.upload_file(
            str(path),
            bucket,
            key,
            ExtraArgs={
                "ContentType": "application/json",
                "CacheControl": "public, max-age=31536000, immutable",
                "Metadata": {"sha256": digest},
            },
        )
        return key, True

    uploaded = skipped = 0
    with ThreadPoolExecutor(max_workers=24) as pool:
        futures = [pool.submit(upload, path) for path in files]
        for future in as_completed(futures):
            _, changed = future.result()
            uploaded += changed
            skipped += not changed

    # Publica o ponto de entrada por último: clientes nunca recebem um índice novo
    # antes de todos os arquivos referenciados estarem disponíveis.
    manifest_path = SOURCE_DIR / "manifest.json"
    manifest_digest = sha256_file(manifest_path)
    client.upload_file(
        str(manifest_path),
        bucket,
        manifest_key,
        ExtraArgs={
            "ContentType": "application/json",
            "CacheControl": "no-cache",
            "Metadata": {"sha256": manifest_digest, "catalog-sha256": manifest["catalogSha256"]},
        },
    )
    print(
        f"Catálogo v2 publicado em {DEST_PREFIX}: {uploaded} enviados, {skipped} inalterados, {manifest['appCount']} apps."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
