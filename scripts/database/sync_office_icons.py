#!/usr/bin/env python3
"""Migrate and synchronize the Office icon files into Store/Catalog/office/media/icons."""

from __future__ import annotations

import argparse
import hashlib
import os
import re
import sys
import unicodedata
from pathlib import PurePosixPath

import boto3
from botocore.config import Config
from botocore.exceptions import ClientError

BUCKET = os.environ.get("R2_BUCKET", "winprovision")
DEST_PREFIX = "Store/Catalog/office/media/icons"
LEGACY_PREFIX = "Office/Icon"
OFFICE_PREFIX = "Store/Catalog/office/"
MAX_ICON_BYTES = 12 * 1024 * 1024
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def list_keys(client, prefix: str) -> list[str]:
    paginator = client.get_paginator("list_objects_v2")
    return sorted(
        item["Key"] for page in paginator.paginate(Bucket=BUCKET, Prefix=prefix) for item in page.get("Contents", [])
    )


def read_object(client, key: str) -> bytes:
    return client.get_object(Bucket=BUCKET, Key=key)["Body"].read()


def normalized_name(key: str) -> str:
    stem = PurePosixPath(key).stem
    value = unicodedata.normalize("NFKC", stem).casefold()
    slug = re.sub(r"[^a-z0-9]+", "-", value).strip("-")
    if not slug:
        raise ValueError(f"nome de ícone não pode ser normalizado: {key}")
    return f"{DEST_PREFIX}/{slug}.png"


def validate_png(data: bytes, key: str) -> None:
    if not data or len(data) > MAX_ICON_BYTES or not data.startswith(PNG_SIGNATURE):
        raise ValueError(f"ícone vazio, maior que 12 MiB ou PNG inválido: {key}")


def upload_icon(client, key: str, data: bytes, digest: str) -> bool:
    try:
        head = client.head_object(Bucket=BUCKET, Key=key)
    except ClientError as exc:
        if exc.response.get("Error", {}).get("Code") not in {"404", "NoSuchKey", "NotFound"}:
            raise
    else:
        if head.get("Metadata", {}).get("sha256") == digest:
            return False

    client.put_object(
        Bucket=BUCKET,
        Key=key,
        Body=data,
        ContentType="image/png",
        CacheControl="public, max-age=86400, must-revalidate",
        Metadata={"sha256": digest, "source": "office-icon-migration"},
    )
    return True


def delete_keys(client, keys: list[str]) -> None:
    for offset in range(0, len(keys), 1000):
        batch = keys[offset : offset + 1000]
        response = client.delete_objects(
            Bucket=BUCKET,
            Delete={"Objects": [{"Key": key} for key in batch], "Quiet": True},
        )
        errors = response.get("Errors", [])
        if errors:
            raise RuntimeError("falha ao limpar objetos antigos: " + ", ".join(error["Key"] for error in errors))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--apply",
        action="store_true",
        help="publica e verifica os ícones; depois remove JSON/índices do Office e as cópias legadas",
    )
    args = parser.parse_args()

    account_id = os.environ.get("R2_ACCOUNT_ID")
    access_key = os.environ.get("R2_ACCESS_KEY_ID")
    secret_key = os.environ.get("R2_SECRET_ACCESS_KEY")
    if not all((account_id, access_key, secret_key)):
        print("Defina R2_ACCOUNT_ID, R2_ACCESS_KEY_ID e R2_SECRET_ACCESS_KEY.", file=sys.stderr)
        return 2

    client = boto3.client(
        "s3",
        endpoint_url=f"https://{account_id}.r2.cloudflarestorage.com",
        aws_access_key_id=access_key,
        aws_secret_access_key=secret_key,
        config=Config(signature_version="s3v4", retries={"max_attempts": 5, "mode": "standard"}),
        region_name="auto",
    )

    legacy_objects = list_keys(client, f"{LEGACY_PREFIX}/")
    legacy_keys = [key for key in legacy_objects if not key.endswith("/")]
    office_keys = list_keys(client, OFFICE_PREFIX)
    legacy_icons: dict[str, tuple[str, bytes, str]] = {}
    desired: dict[str, tuple[bytes, str]] = {}

    for source_key in legacy_keys:
        if PurePosixPath(source_key).suffix.casefold() != ".png":
            raise ValueError(f"arquivo não PNG encontrado em {LEGACY_PREFIX}: {source_key}")
        target_key = normalized_name(source_key)
        data = read_object(client, source_key)
        validate_png(data, source_key)
        digest = sha256(data)
        if target_key in desired and desired[target_key][1] != digest:
            raise ValueError(f"dois ícones diferentes resultam no mesmo destino: {target_key}")
        desired[target_key] = (data, digest)
        legacy_icons[source_key] = (target_key, data, digest)

    existing_icons = [
        key for key in office_keys if key.startswith(f"{DEST_PREFIX}/") and key.casefold().endswith(".png")
    ]
    if not legacy_keys:
        for key in existing_icons:
            data = read_object(client, key)
            validate_png(data, key)
            desired[key] = (data, sha256(data))
    if not desired:
        raise ValueError("nenhum ícone encontrado na pasta legada ou em Store/Catalog/office/media/icons")

    keep = set(desired)
    stale_catalog_keys = [key for key in office_keys if key not in keep]
    cleanup_keys = sorted(set(stale_catalog_keys + legacy_objects))
    total_bytes = sum(len(data) for data, _ in desired.values())
    mode = "APLICAÇÃO" if args.apply else "PRÉVIA"
    print(f"{mode}: {len(desired)} ícones ({total_bytes:,} bytes) em {DEST_PREFIX}/", flush=True)
    for key, (data, digest) in sorted(desired.items()):
        print(f"  {key}  {len(data):,} bytes  sha256={digest}", flush=True)
    print(f"Objetos antigos a remover após verificação: {len(cleanup_keys)}", flush=True)
    for key in cleanup_keys:
        print(f"  remover {key}", flush=True)

    if not args.apply:
        print("Prévia apenas. Para migrar, execute novamente com --apply.", flush=True)
        return 0

    uploaded = unchanged = 0
    for number, (key, (data, digest)) in enumerate(sorted(desired.items()), start=1):
        if upload_icon(client, key, data, digest):
            uploaded += 1
        else:
            unchanged += 1
        published = read_object(client, key)
        if sha256(published) != digest:
            raise ValueError(f"verificação de leitura divergiu após publicar {key}")
        print(f"Office icons: {number}/{len(desired)} verificados ({key})", flush=True)

    delete_keys(client, cleanup_keys)
    print(
        f"Concluído: {uploaded} enviados, {unchanged} inalterados e {len(cleanup_keys)} objetos antigos removidos. "
        f"Destino: {DEST_PREFIX}/",
        flush=True,
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ClientError, ValueError, RuntimeError) as exc:
        print(f"Falha na sincronização de ícones Office: {exc}", file=sys.stderr)
        raise SystemExit(1) from exc
