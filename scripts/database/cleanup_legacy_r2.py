#!/usr/bin/env python3
"""Remove only retired WinProvision Store objects after verifying V2 endpoints."""

from __future__ import annotations

import argparse
import os
import sys

import boto3
from botocore.config import Config
from botocore.exceptions import ClientError

BUCKET = os.environ.get("R2_BUCKET") or "winprovision"
RETIRED_PREFIXES = (
    "Store/Catalog/v2/",
    "Store/Api/v1/",
    "Store/Icon_Database/",
    "Store/Screenshot_Database/",
    "Store/Recovery/",
)
RETIRED_KEYS = (
    "Store/icon-manifest.json",
    "Store/Database/apps.json",
    "Store/Database/screenshot-assets.json",
    "Store/Database/screenshot-index.json",
    "Store/Database/msstore-catalog.json",
)
REQUIRED_V2_KEYS = (
    "Store/Catalog/manifest.json",
    "Store/Catalog/manifest/search-index.json",
    "Store/Catalog/manifest/media-index.json",
    "Store/Catalog/msstore/manifest.json",
    "Store/Catalog/msstore/manifest/search-index.json",
    "Store/Catalog/msstore/manifest/media-index.json",
    "Store/Api/manifest.json",
    "Store/Api/winget/manifest.json",
    "Store/Api/winget/manifest/search-index.json",
    "Store/Api/msstore/manifest.json",
    "Store/Api/msstore/manifest/search-index.json",
)


def create_client():
    missing = [name for name in ("R2_ACCOUNT_ID", "R2_ACCESS_KEY_ID", "R2_SECRET_ACCESS_KEY") if not os.environ.get(name)]
    if missing:
        raise RuntimeError("Variáveis R2 ausentes: " + ", ".join(missing))
    return boto3.client(
        "s3",
        endpoint_url=f"https://{os.environ['R2_ACCOUNT_ID']}.r2.cloudflarestorage.com",
        aws_access_key_id=os.environ["R2_ACCESS_KEY_ID"],
        aws_secret_access_key=os.environ["R2_SECRET_ACCESS_KEY"],
        config=Config(signature_version="s3v4", retries={"max_attempts": 5, "mode": "standard"}),
        region_name="auto",
    )


def list_prefix(client, prefix: str) -> list[dict]:
    paginator = client.get_paginator("list_objects_v2")
    return [item for page in paginator.paginate(Bucket=BUCKET, Prefix=prefix) for item in page.get("Contents", [])]


def verify_v2(client) -> None:
    missing: list[str] = []
    for key in REQUIRED_V2_KEYS:
        try:
            client.head_object(Bucket=BUCKET, Key=key)
        except ClientError as exc:
            if exc.response.get("Error", {}).get("Code") in {"404", "NoSuchKey", "NotFound"}:
                missing.append(key)
            else:
                raise
    if missing:
        raise RuntimeError("V2 incompleta no R2; nada será removido. Ausentes:\n  " + "\n  ".join(missing))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true", help="apaga os objetos listados; sem esta opção apenas mostra a prévia")
    args = parser.parse_args()

    try:
        client = create_client()
        verify_v2(client)
        print("Catálogo e API V2 verificados; cache operacional e assets Office serão preservados.", flush=True)

        targets: list[tuple[str, list[dict]]] = []
        for prefix in RETIRED_PREFIXES:
            targets.append((prefix, list_prefix(client, prefix)))
        for key in RETIRED_KEYS:
            try:
                head = client.head_object(Bucket=BUCKET, Key=key)
            except ClientError as exc:
                if exc.response.get("Error", {}).get("Code") in {"404", "NoSuchKey", "NotFound"}:
                    targets.append((key, []))
                    continue
                raise
            targets.append((key, [{"Key": key, "Size": head.get("ContentLength", 0)}]))

        all_objects: list[dict] = []
        total_bytes = 0
        for target, objects in targets:
            size = sum(int(item.get("Size", 0)) for item in objects)
            total_bytes += size
            all_objects.extend(objects)
            print(f"{target}: {len(objects)} objeto(s), {size:,} bytes", flush=True)
            for item in objects[:3]:
                print(f"  {item['Key']} ({int(item.get('Size', 0)):,} bytes)")

        unique = {item["Key"]: item for item in all_objects}
        objects = list(unique.values())
        total_bytes = sum(int(item.get("Size", 0)) for item in objects)
        print(f"Total: {len(objects)} objeto(s), {total_bytes:,} bytes ({total_bytes / (1024 ** 3):.3f} GiB).", flush=True)
        if not args.apply:
            print("Prévia apenas. Para aplicar exatamente esta allowlist: adicione --apply.")
            return 0

        deleted = 0
        for offset in range(0, len(objects), 1000):
            batch = objects[offset : offset + 1000]
            response = client.delete_objects(
                Bucket=BUCKET,
                Delete={"Objects": [{"Key": item["Key"]} for item in batch], "Quiet": True},
            )
            errors = response.get("Errors", [])
            deleted += len(batch) - len(errors)
            for error in errors:
                print(f"Falha ao remover {error.get('Key')}: {error.get('Code')} {error.get('Message', '')}", file=sys.stderr)
            print(f"Remoção R2: {deleted}/{len(objects)} objeto(s).", flush=True)
        if deleted != len(objects):
            return 1
        print(f"Limpeza concluída: {deleted} objeto(s) legados removidos; V2, metrics-cache e assets Office preservados.")
        return 0
    except (ClientError, RuntimeError) as exc:
        print(f"Limpeza interrompida; {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
