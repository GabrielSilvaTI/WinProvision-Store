#!/usr/bin/env python3
"""Validate and publish the standalone Microsoft Store catalog to R2."""

from __future__ import annotations

import hashlib
import json
import os
import sys
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

import boto3
from botocore.config import Config
from botocore.exceptions import ClientError

SOURCE_DIR = Path(os.environ.get("MSSTORE_CATALOG_SOURCE", "msstore-output/catalog"))
DEST_PREFIX = "Store/Catalog/msstore"
CONTENT_TYPES = {
    ".png": "image/png",
    ".jpg": "image/jpeg",
    ".jpeg": "image/jpeg",
    ".webp": "image/webp",
    ".gif": "image/gif",
    ".bmp": "image/bmp",
    ".tif": "image/tiff",
    ".tiff": "image/tiff",
    ".ico": "image/x-icon",
}


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def validate() -> tuple[dict, list[Path]]:
    manifest_path = SOURCE_DIR / "manifest.json"
    index_path = SOURCE_DIR / "manifest" / "search-index.json"
    media_path = SOURCE_DIR / "manifest" / "media-index.json"
    manifest = json.loads(manifest_path.read_bytes())
    index_bytes = index_path.read_bytes()
    media_bytes = media_path.read_bytes()
    rows = json.loads(index_bytes)
    if manifest.get("schemaVersion") != 2 or manifest.get("catalog") != "msstore":
        raise ValueError("manifest.json MS Store inválido")
    if not isinstance(rows, list) or len(rows) != manifest.get("appCount") or not rows:
        raise ValueError("contagem do search-index.json divergente")
    if sha256(index_bytes) != manifest.get("indexSha256") or sha256(media_bytes) != manifest.get("mediaIndexSha256"):
        raise ValueError("hash de índice MS Store divergente")
    seen = set()
    digest = hashlib.sha256()
    for row in rows:
        package_id, relative = row.get("id"), row.get("detailPath")
        if not isinstance(package_id, str) or not isinstance(relative, str) or not relative.startswith("apps/"):
            raise ValueError("linha de busca sem ID ou caminho válido")
        if package_id.casefold() in seen or any(part in {"", ".", ".."} for part in relative.split("/")):
            raise ValueError(f"ID/caminho duplicado ou inseguro: {package_id}")
        seen.add(package_id.casefold())
        detail_path = SOURCE_DIR.joinpath(*relative.split("/"))
        body = detail_path.read_bytes()
        detail = json.loads(body)
        if detail.get("id", "").casefold() != package_id.casefold() or detail.get("source") != "msstore":
            raise ValueError(f"detalhe inválido para {package_id}")
        record_hash = sha256(body)
        if record_hash != row.get("recordSha256"):
            raise ValueError(f"hash de detalhe divergente para {package_id}")
        digest.update(relative.encode("utf-8"))
        digest.update(b"\0")
        digest.update(bytes.fromhex(record_hash))
    digest.update(hashlib.sha256(index_bytes).digest())
    if digest.hexdigest() != manifest.get("catalogSha256"):
        raise ValueError("catalogSha256 divergente")
    files = [path for path in SOURCE_DIR.rglob("*") if path.is_file() and path.name != "manifest.json"]
    return manifest, files


def main() -> int:
    try:
        manifest, files = validate()
    except (OSError, UnicodeError, json.JSONDecodeError, ValueError) as exc:
        print(f"Catálogo MS Store inválido; nada publicado: {exc}", file=sys.stderr)
        return 1
    s3 = boto3.client(
        "s3",
        endpoint_url=f"https://{os.environ['R2_ACCOUNT_ID']}.r2.cloudflarestorage.com",
        aws_access_key_id=os.environ["R2_ACCESS_KEY_ID"],
        aws_secret_access_key=os.environ["R2_SECRET_ACCESS_KEY"],
        config=Config(signature_version="s3v4", retries={"max_attempts": 5, "mode": "standard"}),
        region_name="auto",
    )
    bucket = os.environ.get("R2_BUCKET") or "winprovision"

    def upload(path: Path) -> bool:
        relative = path.relative_to(SOURCE_DIR).as_posix()
        key = f"{DEST_PREFIX}/{relative}"
        body = path.read_bytes()
        digest = sha256(body)
        try:
            head = s3.head_object(Bucket=bucket, Key=key)
            if head.get("Metadata", {}).get("sha256") == digest:
                return False
        except ClientError as exc:
            if exc.response.get("Error", {}).get("Code") not in {"404", "NoSuchKey", "NotFound"}:
                raise
        s3.put_object(
            Bucket=bucket,
            Key=key,
            Body=body,
            ContentType=CONTENT_TYPES.get(path.suffix.casefold(), "application/json"),
            CacheControl="public, max-age=31536000, immutable"
            if "/media/" in relative
            else "no-cache, max-age=0, must-revalidate"
            if relative == "manifest/search-index.json"
            else "public, max-age=300, must-revalidate",
            Metadata={"sha256": digest},
        )
        return True

    changed = 0
    with ThreadPoolExecutor(max_workers=24) as pool:
        futures = [pool.submit(upload, path) for path in files]
        for future in as_completed(futures):
            changed += future.result()

    manifest_path = SOURCE_DIR / "manifest.json"
    manifest_body = manifest_path.read_bytes()
    manifest_key = f"{DEST_PREFIX}/manifest.json"
    manifest_hash = sha256(manifest_body)
    try:
        head = s3.head_object(Bucket=bucket, Key=manifest_key)
        manifest_changed = head.get("Metadata", {}).get("sha256") != manifest_hash
    except ClientError as exc:
        if exc.response.get("Error", {}).get("Code") not in {"404", "NoSuchKey", "NotFound"}:
            raise
        manifest_changed = True
    if manifest_changed:
        s3.put_object(
            Bucket=bucket,
            Key=manifest_key,
            Body=manifest_body,
            ContentType="application/json",
            CacheControl="no-cache, max-age=0, must-revalidate",
            Metadata={"sha256": manifest_hash, "catalog-sha256": manifest["catalogSha256"]},
        )
        changed += 1
    print(
        f"Subcatálogo MS Store publicado em {DEST_PREFIX}: {changed} objeto(s) alterado(s), {manifest['appCount']} apps."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
