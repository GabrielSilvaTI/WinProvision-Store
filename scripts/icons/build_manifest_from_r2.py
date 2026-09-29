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

EXT_PRIORITY = [".ico", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff"]
IMAGE_EXTENSIONS = set(EXT_PRIORITY)


def ext_rank(name: str) -> int:
    ext = PurePosixPath(name).suffix.lower()
    return EXT_PRIORITY.index(ext) if ext in EXT_PRIORITY else len(EXT_PRIORITY)


def list_bucket_objects(s3):
    paginator = s3.get_paginator("list_objects_v2")
    for page in paginator.paginate(Bucket=BUCKET, Prefix=PREFIX):
        for obj in page.get("Contents", []):
            yield obj["Key"]


def build_manifest(keys: list[str], prefix: str, public_base: str) -> tuple[dict[str, str], dict, dict]:
    """Create one deterministic, WPF-decodable URL per lowercase package ID."""
    normalized_prefix = prefix.rstrip("/")
    groups: dict[str, list[str]] = defaultdict(list)
    ignored: list[str] = []
    for key in keys:
        path = PurePosixPath(key)
        if path.parent.as_posix() != normalized_prefix or path.suffix.lower() not in IMAGE_EXTENSIONS:
            ignored.append(key)
            continue
        package_id = path.stem.strip().lower()
        if not package_id:
            ignored.append(key)
            continue
        groups[package_id].append(key)

    manifest: dict[str, str] = {}
    conflicts: dict[str, list[str]] = {}
    inventory: dict[str, dict] = {}
    for package_id, candidates in sorted(groups.items()):
        candidates.sort(key=lambda key: (ext_rank(key), key.casefold(), key))
        selected = candidates[0]
        filename = PurePosixPath(selected).name
        manifest[package_id] = f"{public_base.rstrip('/')}/{filename}"
        inventory[package_id] = {
            "selected": selected,
            "selectedExtension": PurePosixPath(selected).suffix.lower(),
            "candidates": candidates,
        }
        if len(candidates) > 1:
            conflicts[package_id] = candidates

    return manifest, conflicts, {"ignoredObjects": sorted(ignored), "packages": inventory}


def main() -> None:
    s3 = boto3.client(
        "s3",
        endpoint_url=ENDPOINT_URL,
        aws_access_key_id=ACCESS_KEY_ID,
        aws_secret_access_key=SECRET_ACCESS_KEY,
        region_name="auto",
    )

    keys = list(list_bucket_objects(s3))
    manifest, conflicts, inventory = build_manifest(keys, PREFIX, PUBLIC_BASE)
    if not manifest:
        print(f"Nenhuma imagem WPF compatível encontrada em {BUCKET}/{PREFIX}")
        sys.exit(1)

    with open("icon-manifest.json", "w", encoding="utf-8") as manifest_file:
        json.dump(manifest, manifest_file, ensure_ascii=False, indent=2, sort_keys=True)

    with open("conflitos_extensao.json", "w", encoding="utf-8") as conflicts_file:
        json.dump(conflicts, conflicts_file, ensure_ascii=False, indent=2, sort_keys=True)
    inventory_report = {
        "schemaVersion": 1,
        "prefix": PREFIX,
        "objectCount": len(keys),
        "usableIconCount": sum(len(item["candidates"]) for item in inventory["packages"].values()),
        "packageCount": len(manifest),
        "duplicatePackageIds": len(conflicts),
        "ignoredObjectCount": len(inventory["ignoredObjects"]),
        **inventory,
    }
    with open("icon-inventory.json", "w", encoding="utf-8") as inventory_file:
        json.dump(inventory_report, inventory_file, ensure_ascii=False, indent=2, sort_keys=True)

    print(f"Objetos no bucket: {len(keys)}")
    print(f"Ícones únicos no manifesto: {len(manifest)}")
    print(f"IDs com múltiplos objetos: {len(conflicts)}")
    print(f"Objetos ignorados (metadados, subpastas ou formato não suportado): {len(inventory['ignoredObjects'])}")


if __name__ == "__main__":
    main()
