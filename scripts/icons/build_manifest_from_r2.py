#!/usr/bin/env python3
"""Gera o manifesto público de ícones a partir dos objetos no R2."""

import json
import os
import sys
from collections import defaultdict
from pathlib import PurePosixPath

import boto3

ACCOUNT_ID = os.environ["R2_ACCOUNT_ID"]
ACCESS_KEY_ID = os.environ["R2_ACCESS_KEY_ID"]
SECRET_ACCESS_KEY = os.environ["R2_SECRET_ACCESS_KEY"]

BUCKET = os.environ.get("R2_BUCKET", "winprovision")
PREFIX = os.environ.get("R2_ICON_PREFIX", "Store/Icon_Database/")
PUBLIC_BASE = os.environ.get(
    "R2_PUBLIC_BASE",
    "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/Icon_Database",
)
ENDPOINT_URL = f"https://{ACCOUNT_ID}.r2.cloudflarestorage.com"

EXT_PRIORITY = [".png", ".webp", ".jpg", ".jpeg", ".gif", ".bmp", ".svg", ".ico"]


def ext_rank(name: str) -> int:
    ext = PurePosixPath(name).suffix.lower()
    return EXT_PRIORITY.index(ext) if ext in EXT_PRIORITY else len(EXT_PRIORITY)


def list_bucket_objects(s3):
    paginator = s3.get_paginator("list_objects_v2")
    for page in paginator.paginate(Bucket=BUCKET, Prefix=PREFIX):
        for obj in page.get("Contents", []):
            yield obj["Key"]


def main() -> None:
    s3 = boto3.client(
        "s3",
        endpoint_url=ENDPOINT_URL,
        aws_access_key_id=ACCESS_KEY_ID,
        aws_secret_access_key=SECRET_ACCESS_KEY,
        region_name="auto",
    )

    keys = list(list_bucket_objects(s3))
    if not keys:
        print(f"Nenhum objeto encontrado em {BUCKET}/{PREFIX}")
        sys.exit(1)

    groups = defaultdict(list)
    for key in keys:
        filename = PurePosixPath(key).name
        app_id = PurePosixPath(filename).stem.lower()
        groups[app_id].append(filename)

    manifest = {}
    conflicts = {}
    for app_id, candidates in groups.items():
        if len(candidates) > 1:
            candidates.sort(key=ext_rank)
            conflicts[app_id] = candidates

        manifest[app_id] = f"{PUBLIC_BASE}/{candidates[0]}"

    with open("icon-manifest.json", "w", encoding="utf-8") as manifest_file:
        json.dump(manifest, manifest_file, ensure_ascii=False, indent=2, sort_keys=True)

    if conflicts:
        with open("conflitos_extensao.json", "w", encoding="utf-8") as conflicts_file:
            json.dump(conflicts, conflicts_file, ensure_ascii=False, indent=2)

    print(f"Objetos no bucket: {len(keys)}")
    print(f"Ícones únicos no manifesto: {len(manifest)}")
    print(f"Conflitos de extensão: {len(conflicts)}")


if __name__ == "__main__":
    main()
