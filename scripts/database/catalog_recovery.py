#!/usr/bin/env python3
"""Create and restore validated, versioned snapshots of public catalog JSON in R2."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

import boto3
from botocore.config import Config

SNAPSHOT_PREFIX = "Store/Recovery/Snapshots"
TARGETS = (
    # Maps and immutable-content pointers first; active catalog documents are restored after them.
    "Store/Database/screenshot-assets.json",
    "Store/Database/msstore-assets-map.json",
    "Office/Database/office-assets-map.json",
    "Store/Database/metrics-cache.json",
    "Store/Api/v1/_state/package-hashes.json",
    "Store/Api/v1/index.json",
    "Store/icon-manifest.json",
    "Office/Database/catalog.json",
    "Store/Database/msstore-catalog.json",
    "Store/Database/apps.json",
)
SNAPSHOT_ID_RE = re.compile(r"^\d{8}T\d{6}Z$")


def client():
    account = os.environ["R2_ACCOUNT_ID"]
    return boto3.client(
        "s3", endpoint_url=f"https://{account}.r2.cloudflarestorage.com",
        aws_access_key_id=os.environ["R2_ACCESS_KEY_ID"],
        aws_secret_access_key=os.environ["R2_SECRET_ACCESS_KEY"],
        config=Config(signature_version="s3v4", retries={"max_attempts": 5, "mode": "standard"}),
        region_name="auto",
    )


def bucket_name() -> str:
    return os.environ.get("R2_BUCKET") or "winprovision"


def decode_json(body: bytes, key: str):
    try:
        return json.loads(body.decode("utf-8-sig"))
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise ValueError(f"{key} não contém JSON válido: {exc}") from exc


def validate_catalogs(documents: dict[str, bytes]) -> None:
    apps = decode_json(documents["Store/Database/apps.json"], "apps.json")
    if not isinstance(apps, list) or len(apps) < 5000:
        raise ValueError("apps.json inválido ou com menos de 5000 aplicativos")
    seen = set()
    sources = {"winget": 0, "msstore": 0}
    for index, item in enumerate(apps):
        if not isinstance(item, dict) or not isinstance(item.get("id"), str) or not item["id"].strip() \
                or not isinstance(item.get("name"), str) or not item["name"].strip():
            raise ValueError(f"apps.json[{index}] sem ID/nome válido")
        folded = item["id"].casefold()
        if folded in seen:
            raise ValueError(f"ID duplicado no apps.json: {item['id']}")
        seen.add(folded)
        source = item.get("source")
        if source not in sources:
            raise ValueError(f"Origem inválida em apps.json[{index}]: {source!r}")
        sources[source] += 1
    if sources["winget"] < 5000 or sources["msstore"] < 20:
        raise ValueError(f"catálogo por origem incompleto: winget={sources['winget']}, msstore={sources['msstore']}")

    msstore = decode_json(documents["Store/Database/msstore-catalog.json"], "msstore-catalog.json")
    if not isinstance(msstore, list) or len(msstore) < 20:
        raise ValueError("msstore-catalog.json inválido ou com menos de 20 aplicativos")
    office = decode_json(documents["Office/Database/catalog.json"], "Office catalog")
    if not isinstance(office, dict) or office.get("schemaVersion") not in (1, 2) \
            or not isinstance(office.get("products"), list) or not office["products"] \
            or not isinstance(office.get("storeOffers", []), list):
        raise ValueError("catálogo Office inválido")
    for key in ("Store/Database/screenshot-assets.json", "Store/Database/msstore-assets-map.json",
                "Office/Database/office-assets-map.json"):
        value = decode_json(documents[key], key)
        if not isinstance(value, dict):
            raise ValueError(f"{key} precisa ser um objeto JSON")
    manifest = decode_json(documents["Store/icon-manifest.json"], "icon-manifest.json")
    if not isinstance(manifest, dict) or len(manifest) < 100 \
            or any(not isinstance(url, str) or not url.startswith("https://") for url in manifest.values()):
        raise ValueError("manifesto de ícones inválido ou incompleto")
    api_index = decode_json(documents["Store/Api/v1/index.json"], "installer API index")
    if not isinstance(api_index, dict) or api_index.get("schema") != 1 \
            or not isinstance(api_index.get("packages"), list) \
            or api_index.get("count") != len(api_index.get("packages", [])):
        raise ValueError("index.json da API de instaladores inválido")
    for key in ("Store/Database/metrics-cache.json", "Store/Api/v1/_state/package-hashes.json"):
        if not isinstance(decode_json(documents[key], key), dict):
            raise ValueError(f"{key} precisa ser um objeto JSON")


def snapshot(snapshot_id: str | None) -> str:
    if snapshot_id is None:
        snapshot_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    if not SNAPSHOT_ID_RE.fullmatch(snapshot_id):
        raise ValueError("ID de snapshot inválido; use o formato UTC AAAAMMDDTHHMMSSZ")
    s3 = client()
    bucket = bucket_name()
    manifest_key = f"{SNAPSHOT_PREFIX}/{snapshot_id}/manifest.json"
    try:
        s3.head_object(Bucket=bucket, Key=manifest_key)
    except Exception as exc:
        code = getattr(exc, "response", {}).get("Error", {}).get("Code", "")
        if code not in ("404", "NoSuchKey", "NotFound"):
            raise
    else:
        raise ValueError(f"snapshot {snapshot_id} já existe; não será sobrescrito")
    bodies = {}
    for key in TARGETS:
        response = s3.get_object(Bucket=bucket, Key=key)
        body = response["Body"].read()
        decode_json(body, key)
        bodies[key] = body
        print(f"Leitura OK: {key} ({len(body)} bytes)")
    validate_catalogs(bodies)
    manifest = {
        "schemaVersion": 1,
        "snapshotId": snapshot_id,
        "createdAt": datetime.now(timezone.utc).isoformat(),
        "bucket": bucket,
        "files": {key: {"sha256": hashlib.sha256(body).hexdigest(), "size": len(body)}
                  for key, body in bodies.items()},
    }
    prefix = f"{SNAPSHOT_PREFIX}/{snapshot_id}"
    for key, body in bodies.items():
        s3.put_object(Bucket=bucket, Key=f"{prefix}/{key}", Body=body,
                      ContentType="application/json", CacheControl="no-cache")
    # The manifest is the commit marker; incomplete snapshots are never restorable.
    s3.put_object(Bucket=bucket, Key=f"{prefix}/manifest.json",
                  Body=json.dumps(manifest, ensure_ascii=False, indent=2).encode("utf-8"),
                  ContentType="application/json", CacheControl="no-cache")
    print(f"Snapshot válido publicado: {snapshot_id} ({len(bodies)} arquivos)")
    return snapshot_id


def restore(snapshot_id: str) -> None:
    if not SNAPSHOT_ID_RE.fullmatch(snapshot_id):
        raise ValueError("ID de snapshot inválido; use o formato UTC AAAAMMDDTHHMMSSZ")
    s3 = client()
    bucket = bucket_name()
    prefix = f"{SNAPSHOT_PREFIX}/{snapshot_id}"
    manifest_body = s3.get_object(Bucket=bucket, Key=f"{prefix}/manifest.json")["Body"].read()
    manifest = decode_json(manifest_body, "snapshot manifest")
    files = manifest.get("files") if isinstance(manifest, dict) else None
    if not isinstance(manifest, dict) or manifest.get("schemaVersion") != 1 or manifest.get("snapshotId") != snapshot_id \
            or manifest.get("bucket") != bucket or not isinstance(files, dict) \
            or set(files) != set(TARGETS):
        raise ValueError("Manifesto ausente, incompleto ou incompatível; nada foi restaurado")

    bodies = {}
    for key in TARGETS:
        body = s3.get_object(Bucket=bucket, Key=f"{prefix}/{key}")["Body"].read()
        metadata = files[key]
        digest = hashlib.sha256(body).hexdigest()
        if digest != metadata.get("sha256") or len(body) != metadata.get("size"):
            raise ValueError(f"Integridade inválida no snapshot para {key}; nada foi restaurado")
        decode_json(body, key)
        bodies[key] = body
    validate_catalogs(bodies)

    # Validate every file before mutating any live object. Catalog references are
    # uploaded last so media maps/indexes exist before clients observe the catalogs.
    for key in TARGETS:
        s3.put_object(Bucket=bucket, Key=key, Body=bodies[key],
                      ContentType="application/json", CacheControl="no-cache")
        print(f"Restaurado: {key}")
    print(f"Restauração concluída a partir do snapshot {snapshot_id}.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    backup_parser = commands.add_parser("snapshot", help="cria cópia validada dos catálogos ativos")
    backup_parser.add_argument("--id", help="ID UTC no formato AAAAMMDDTHHMMSSZ; padrão: agora")
    restore_parser = commands.add_parser("restore", help="restaura um snapshot validado")
    restore_parser.add_argument("snapshot_id")
    args = parser.parse_args()
    try:
        if args.command == "snapshot":
            snapshot_id = snapshot(args.id)
            report = {"operation": "snapshot", "snapshotId": snapshot_id,
                      "createdAt": datetime.now(timezone.utc).isoformat(), "files": len(TARGETS)}
        else:
            restore(args.snapshot_id)
            report = {"operation": "restore", "snapshotId": args.snapshot_id,
                      "completedAt": datetime.now(timezone.utc).isoformat(), "files": len(TARGETS)}
        Path("catalog-recovery-report.json").write_text(
            json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        return 0
    except Exception as exc:
        print(f"Operação de recuperação abortada: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
