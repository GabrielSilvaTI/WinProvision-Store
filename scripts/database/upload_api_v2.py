#!/usr/bin/env python3
"""Validate and publish one hierarchical installer API source to R2."""

from __future__ import annotations

import hashlib
import json
import os
import re
import sys
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

import boto3
from botocore.config import Config
from botocore.exceptions import ClientError

SOURCE = os.environ.get("API_V2_SOURCE", "").strip().casefold()
SOURCE_DIR = Path(os.environ.get("API_V2_SOURCE_DIR", "api-v2-source"))
ROOT_KEY = "Store/Api/manifest.json"
SOURCE_REFERENCES = {
    name: {"manifestPath": f"{name}/manifest.json", "indexPath": f"{name}/manifest/search-index.json"}
    for name in ("winget", "msstore")
}
SHA256_RE = re.compile(r"^[0-9a-fA-F]{64}$")


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def validate() -> tuple[dict, list[Path]]:
    if SOURCE not in ("winget", "msstore"):
        raise ValueError("API_V2_SOURCE precisa ser winget ou msstore")
    manifest_bytes = (SOURCE_DIR / "manifest.json").read_bytes()
    index_bytes = (SOURCE_DIR / "manifest" / "search-index.json").read_bytes()
    manifest, rows = json.loads(manifest_bytes), json.loads(index_bytes)
    if (
        not isinstance(manifest, dict)
        or manifest.get("schemaVersion") != 2
        or manifest.get("catalog") != "installer-api"
        or manifest.get("source") != SOURCE
        or not isinstance(rows, list)
        or manifest.get("packageCount") != len(rows)
        or not rows
    ):
        raise ValueError("manifest.json ou search-index.json inválido")
    if digest(index_bytes) != manifest.get("indexSha256"):
        raise ValueError("hash do search-index.json divergente")
    seen: set[str] = set()
    catalog_digest = hashlib.sha256()
    files = [path for path in SOURCE_DIR.rglob("*") if path.is_file() and path.name != "manifest.json"]
    expected: set[str] = set()
    for row in rows:
        if not isinstance(row, dict):
            raise ValueError("entrada inválida no search-index.json")
        package_id, relative, record_hash = row.get("id"), row.get("detailPath"), row.get("recordSha256")
        if (
            not isinstance(package_id, str)
            or not isinstance(relative, str)
            or not relative.startswith("apps/")
            or row.get("source") != SOURCE
            or package_id.casefold() in seen
            or any(part in ("", ".", "..") for part in relative.split("/"))
        ):
            raise ValueError("entrada inválida ou duplicada no search-index.json")
        seen.add(package_id.casefold())
        expected.add(relative)
        path = SOURCE_DIR.joinpath(*relative.split("/"))
        body = path.read_bytes()
        package = json.loads(body)
        package_identifier = package.get("id")
        if (
            not isinstance(package, dict)
            or package.get("schema") != 2
            or package.get("source") != SOURCE
            or not isinstance(package_identifier, str)
            or package_identifier.casefold() != package_id.casefold()
            or package.get("version", "") != row.get("version", "")
            or digest(body) != record_hash
        ):
            raise ValueError(f"detalhe inválido ou hash divergente: {package_id}")
        installers = package.get("installers")
        if not isinstance(installers, list):
            raise ValueError(f"installers inválido: {package_id}")
        if SOURCE == "winget" and not installers:
            raise ValueError(f"Pacote WinGet sem instaladores: {package_id}")
        if SOURCE == "msstore" and installers:
            raise ValueError(f"Pacote MS Store não pode declarar instaladores: {package_id}")
        for installer in installers:
            if not isinstance(installer, dict):
                raise ValueError(f"Instalador inválido: {package_id}")
            url, installer_hash = installer.get("url"), installer.get("sha256")
            if not isinstance(url, str) or not url.lower().startswith("https://"):
                raise ValueError(f"Instalador sem URL HTTPS: {package_id}")
            if installer_hash is not None and (
                not isinstance(installer_hash, str) or not SHA256_RE.fullmatch(installer_hash)
            ):
                raise ValueError(f"SHA-256 inválido: {package_id}")
            if installer.get("silentSupported") is True and not SHA256_RE.fullmatch(installer_hash or ""):
                raise ValueError(f"Instalador silencioso sem SHA-256 válido: {package_id}")
        catalog_digest.update(relative.encode("utf-8"))
        catalog_digest.update(b"\0")
        catalog_digest.update(bytes.fromhex(record_hash))
    catalog_digest.update(bytes.fromhex(digest(index_bytes)))
    if catalog_digest.hexdigest() != manifest.get("catalogSha256"):
        raise ValueError("catalogSha256 divergente")
    actual = {path.relative_to(SOURCE_DIR).as_posix() for path in files}
    if actual != expected | {"manifest/search-index.json"}:
        raise ValueError("há arquivos ausentes ou fora do índice no subcatálogo")
    return manifest, [SOURCE_DIR / relative for relative in sorted(expected)]


def main() -> int:
    try:
        manifest, details = validate()
    except (OSError, UnicodeError, json.JSONDecodeError, ValueError) as exc:
        print(f"API {SOURCE or '?'} inválida; nada publicado: {exc}", file=sys.stderr)
        return 1
    client = boto3.client(
        "s3",
        endpoint_url=f"https://{os.environ['R2_ACCOUNT_ID']}.r2.cloudflarestorage.com",
        aws_access_key_id=os.environ["R2_ACCESS_KEY_ID"],
        aws_secret_access_key=os.environ["R2_SECRET_ACCESS_KEY"],
        config=Config(signature_version="s3v4", retries={"max_attempts": 5, "mode": "standard"}),
        region_name="auto",
    )
    bucket = os.environ.get("R2_BUCKET") or "winprovision"
    prefix = f"Store/Api/{SOURCE}"
    state_key = f"{prefix}/_state/package-hashes.json"
    try:
        state_response = client.get_object(Bucket=bucket, Key=state_key)
        old_state = json.loads(state_response["Body"].read())
    except ClientError as exc:
        if exc.response.get("Error", {}).get("Code") not in {"404", "NoSuchKey", "NotFound"}:
            raise
        old_state = {}
    except (UnicodeError, json.JSONDecodeError):
        old_state = {}
    old_hashes = (
        old_state.get("packages", {})
        if isinstance(old_state, dict)
        and old_state.get("schemaVersion") == 2
        and old_state.get("source") == SOURCE
        and isinstance(old_state.get("packages"), dict)
        else {}
    )
    new_hashes = {}

    def upload(path: Path) -> bool:
        relative = path.relative_to(SOURCE_DIR).as_posix()
        key = f"{prefix}/{relative}"
        body_hash = hashlib.sha256(path.read_bytes()).hexdigest()
        if relative.startswith("apps/"):
            new_hashes[relative] = body_hash
            if old_hashes.get(relative) == body_hash:
                return False
        try:
            head = client.head_object(Bucket=bucket, Key=key)
            if head.get("Metadata", {}).get("sha256") == body_hash:
                return False
        except ClientError as exc:
            if exc.response.get("Error", {}).get("Code") not in {"404", "NoSuchKey", "NotFound"}:
                raise
        client.upload_file(
            str(path),
            bucket,
            key,
            ExtraArgs={
                "ContentType": "application/json",
                "CacheControl": "public, max-age=300, must-revalidate",
                "Metadata": {"sha256": body_hash},
            },
        )
        return True

    with ThreadPoolExecutor(max_workers=16) as executor:
        futures = {executor.submit(upload, path): path for path in details}
        changed = 0
        for future in as_completed(futures):
            changed += future.result()

    # Índice e manifesto são ponteiros de ativação: publique-os só após todos os detalhes.
    for path in (SOURCE_DIR / "manifest" / "search-index.json", SOURCE_DIR / "manifest.json"):
        changed += upload(path)

    available_sources = {}
    for name, reference in SOURCE_REFERENCES.items():
        try:
            client.head_object(Bucket=bucket, Key=f"Store/Api/{reference['manifestPath']}")
        except ClientError as exc:
            if exc.response.get("Error", {}).get("Code") not in {"404", "NoSuchKey", "NotFound"}:
                raise
        else:
            available_sources[name] = reference
    root_document = {"schemaVersion": 2, "catalog": "installer-api", "sources": available_sources}
    root_body = json.dumps(root_document, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    root_hash = digest(root_body)
    try:
        head = client.head_object(Bucket=bucket, Key=ROOT_KEY)
        root_changed = head.get("Metadata", {}).get("sha256") != root_hash
    except ClientError as exc:
        if exc.response.get("Error", {}).get("Code") not in {"404", "NoSuchKey", "NotFound"}:
            raise
        root_changed = True
    if root_changed:
        client.put_object(
            Bucket=bucket,
            Key=ROOT_KEY,
            Body=root_body,
            ContentType="application/json",
            CacheControl="public, max-age=300, must-revalidate",
            Metadata={"sha256": root_hash},
        )
    state_body = json.dumps(
        {"schemaVersion": 2, "source": SOURCE, "packages": new_hashes},
        separators=(",", ":"),
    ).encode("utf-8")
    state_hash = digest(state_body)
    client.put_object(
        Bucket=bucket,
        Key=state_key,
        Body=state_body,
        ContentType="application/json",
        CacheControl="no-cache",
        Metadata={"sha256": state_hash},
    )
    print(
        f"API {SOURCE} publicada em Store/Api/{SOURCE}: {changed} objeto(s) alterado(s), {manifest['packageCount']} pacotes."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
