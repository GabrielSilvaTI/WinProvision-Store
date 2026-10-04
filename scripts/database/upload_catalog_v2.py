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
DEST_PREFIX = os.environ.get("R2_CATALOG_V2_PREFIX", "Store/Catalog").strip("/")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    print(f"Validando catálogo V2 em {SOURCE_DIR}...", flush=True)
    try:
        manifest, files = validate_catalog_v2(SOURCE_DIR)
    except (OSError, UnicodeError, ValueError) as exc:
        print(f"Catálogo hierárquico inválido; nada foi publicado: {exc}", file=sys.stderr)
        return 1
    detail_files = [path for path in files if path.is_relative_to(SOURCE_DIR / "apps")]
    print(
        f"Catálogo validado: {manifest['appCount']} apps; "
        f"iniciando verificação/publicação de {len(detail_files)} detalhes...",
        flush=True,
    )

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
        metadata = published.get("Metadata", {})
        if (
            metadata.get("catalog-sha256") == manifest["catalogSha256"]
            and manifest.get("inputFingerprint")
            and metadata.get("input-fingerprint") == manifest["inputFingerprint"]
        ):
            print(f"Catálogo v2 sem alterações em {DEST_PREFIX}; cache validado com {manifest['appCount']} apps.")
            return 0

    def upload(path: Path, *, cache_control: str = "public, max-age=300, must-revalidate") -> tuple[str, bool]:
        relative = path.relative_to(SOURCE_DIR).as_posix()
        key = f"{DEST_PREFIX}/{relative}"
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
                "CacheControl": cache_control,
                "Metadata": {"sha256": digest},
            },
        )
        return key, True

    search_index = SOURCE_DIR / "manifest" / "search-index.json"
    media_index = SOURCE_DIR / "manifest" / "media-index.json"
    if search_index not in files:
        print("Catálogo inválido: manifest/search-index.json não foi validado.", file=sys.stderr)
        return 1

    uploaded = skipped = 0
    with ThreadPoolExecutor(max_workers=24) as pool:
        futures = [pool.submit(upload, path) for path in detail_files]
        for completed, future in enumerate(as_completed(futures), start=1):
            _, changed = future.result()
            uploaded += changed
            skipped += not changed
            if completed % 250 == 0 or completed == len(futures):
                print(
                    f"Catálogo R2: {completed}/{len(futures)} apps verificados; "
                    f"{uploaded} enviados, {skipped} sem alteração.",
                    flush=True,
                )

    # Os arquivos de app ficam disponíveis antes de trocar o índice que aponta
    # para eles. As chaves são estáveis; só o conteúdo alterado é enviado.
    _, changed = upload(search_index)
    uploaded += changed
    skipped += not changed
    if manifest.get("mediaIndexSha256"):
        if media_index not in files:
            print("Catálogo inválido: manifest/media-index.json não foi validado.", file=sys.stderr)
            return 1
        _, changed = upload(media_index)
        uploaded += changed
        skipped += not changed

    # Publica o ponto de entrada por último: clientes nunca recebem um índice novo
    # antes de todos os arquivos referenciados estarem disponíveis.
    manifest_path = SOURCE_DIR / "manifest.json"
    manifest_digest = sha256_file(manifest_path)
    manifest_metadata = {
        "sha256": manifest_digest,
        "catalog-sha256": manifest["catalogSha256"],
        "input-fingerprint": manifest.get("inputFingerprint") or "",
    }
    try:
        head = client.head_object(Bucket=bucket, Key=manifest_key)
        if head.get("Metadata", {}).get("sha256") == manifest_digest:
            # Manifest byte-for-byte idêntico: preserve os metadados do cache,
            # inclusive a impressão digital escrita no primeiro envio.
            pass
        else:
            client.upload_file(
                str(manifest_path), bucket, manifest_key,
                ExtraArgs={
                    "ContentType": "application/json",
                    "CacheControl": "no-cache, max-age=0, must-revalidate",
                    "Metadata": manifest_metadata,
                },
            )
            uploaded += 1
    except ClientError as exc:
        if exc.response.get("Error", {}).get("Code") not in {"404", "NoSuchKey", "NotFound"}:
            raise
        client.upload_file(
            str(manifest_path), bucket, manifest_key,
            ExtraArgs={
                "ContentType": "application/json",
                "CacheControl": "no-cache, max-age=0, must-revalidate",
                "Metadata": manifest_metadata,
            },
        )
        uploaded += 1
    print(
        f"Catálogo v2 publicado em {DEST_PREFIX}: {uploaded} enviados, {skipped} inalterados, {manifest['appCount']} apps."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
