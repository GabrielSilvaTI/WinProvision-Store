#!/usr/bin/env python3
"""Build a hierarchical installer API subcatalog for WinGet or Microsoft Store."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import tempfile
import unicodedata
from datetime import UTC, datetime
from pathlib import Path
from urllib.parse import quote


def slug(value: str) -> str:
    value = unicodedata.normalize("NFKD", value.casefold())
    value = "".join(char for char in value if not unicodedata.combining(char))
    value = re.sub(r"[^a-z0-9_-]+", "-", value).strip("-_ .")
    if not value or value in {".", ".."}:
        raise ValueError(f"Componente de ID não pode formar uma pasta segura: {value!r}")
    return value


def package_path(package_id: str) -> str:
    parts = package_id.split(".")
    first = parts[0][0].casefold()
    bucket = "0-9" if first.isdigit() else first if "a" <= first <= "z" else "_"
    return "/".join(("apps", bucket, *(slug(part) for part in parts), "package.json"))


def is_safe_package_id(package_id: object) -> bool:
    """Accept catalog IDs and normalize punctuation when creating API paths."""
    return (
        isinstance(package_id, str)
        and bool(package_id)
        and len(package_id) <= 128
        and package_id[0].isalnum()
        and all(char.isprintable() and not char.isspace() and char not in "/\\" for char in package_id)
        and all(part not in {"", ".", ".."} for part in package_id.split("."))
    )


def write_json(path: Path, value) -> bytes:
    body = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(body)
    return body


def read_packages(source: str, input_dir: Path) -> list[dict]:
    if source == "winget":
        index_path = input_dir / "index.json"
        raw_index = json.loads(index_path.read_text(encoding="utf-8-sig"))
        if (
            not isinstance(raw_index, dict)
            or raw_index.get("schema") != 1
            or not isinstance(raw_index.get("packages"), list)
        ):
            raise ValueError("Índice intermediário WinGet inválido")
        result = []
        for row in raw_index["packages"]:
            if not isinstance(row, dict):
                raise ValueError("Entrada inválida no índice intermediário WinGet")
            package_id = row.get("id")
            if not is_safe_package_id(package_id):
                raise ValueError(f"ID de pacote WinGet inválido: {package_id!r}")
            path = input_dir / "packages" / f"{package_id}.json"
            package = json.loads(path.read_text(encoding="utf-8-sig"))
            if (
                not isinstance(package, dict)
                or not isinstance(package.get("id"), str)
                or package["id"].casefold() != package_id.casefold()
            ):
                raise ValueError(f"ID do manifesto WinGet não corresponde: {package_id}")
            package["schema"] = 2
            package["source"] = "winget"
            if not isinstance(package.get("installers"), list):
                raise ValueError(f"Lista de instaladores inválida: {package_id}")
            result.append(package)
        return result

    raw = json.loads(input_dir.read_text(encoding="utf-8-sig"))
    if not isinstance(raw, list):
        raise ValueError("Catálogo MS Store precisa ser uma lista")
    result = []
    for item in raw:
        if not isinstance(item, dict) or not isinstance(item.get("id"), str):
            raise ValueError("Entrada MS Store inválida ou sem ID")
        result.append(
            {
                "schema": 2,
                "id": item["id"],
                "version": str(item.get("version") or ""),
                "source": "msstore",
                "installers": [],
            }
        )
    return result


def build(source: str, input_path: Path, destination: Path) -> dict:
    packages = read_packages(source, input_path)
    if not packages:
        raise ValueError(f"Subcatálogo {source} vazio")
    seen: set[str] = set()
    owners: dict[str, str] = {}
    prepared = []
    for package in packages:
        package_id = package.get("id")
        if not is_safe_package_id(package_id):
            raise ValueError(f"ID de pacote inválido para caminho: {package_id!r}")
        if package.get("source") != source:
            raise ValueError(f"Origem de {package_id} não corresponde a {source}")
        key = package_id.casefold()
        relative = package_path(package_id)
        if key in seen or relative.casefold() in owners:
            raise ValueError(f"ID duplicado ou colisão de caminho: {package_id}")
        seen.add(key)
        owners[relative.casefold()] = package_id
        package.setdefault("installers", [])
        if source == "winget" and not package["installers"]:
            raise ValueError(f"Pacote WinGet sem instaladores: {package_id}")
        if source == "msstore" and package["installers"]:
            raise ValueError(f"Pacote MS Store não deve ter instaladores próprios: {package_id}")
        prepared.append((relative, package))
    prepared.sort(key=lambda entry: (entry[1]["id"].casefold(), entry[1]["id"]))

    destination.parent.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix=f".{destination.name}.tmp-", dir=destination.parent))
    backup = destination.parent / f".{destination.name}.previous"
    try:
        rows = []
        catalog_hash = hashlib.sha256()
        for relative, package in prepared:
            body = write_json(staging.joinpath(*relative.split("/")), package)
            digest = hashlib.sha256(body).hexdigest()
            catalog_hash.update(relative.encode("utf-8"))
            catalog_hash.update(b"\0")
            catalog_hash.update(bytes.fromhex(digest))
            rows.append(
                {
                    "id": package["id"],
                    "version": package.get("version", ""),
                    "source": source,
                    "architectures": sorted(
                        {
                            installer["architecture"]
                            for installer in package.get("installers", [])
                            if isinstance(installer, dict) and isinstance(installer.get("architecture"), str)
                        }
                    ),
                    "detailPath": quote(relative, safe="/"),
                    "recordSha256": digest,
                }
            )
        index_bytes = write_json(staging / "manifest" / "search-index.json", rows)
        catalog_hash.update(hashlib.sha256(index_bytes).digest())
        manifest = {
            "schemaVersion": 2,
            "catalog": "installer-api",
            "source": source,
            "generatedUtc": datetime.now(UTC).isoformat().replace("+00:00", "Z"),
            "packageCount": len(rows),
            "catalogSha256": catalog_hash.hexdigest(),
            "indexSha256": hashlib.sha256(index_bytes).hexdigest(),
        }
        write_json(staging / "manifest.json", manifest)
        if backup.exists():
            shutil.rmtree(backup)
        if destination.exists():
            destination.rename(backup)
        try:
            staging.rename(destination)
        except Exception:
            if backup.exists() and not destination.exists():
                backup.rename(destination)
            raise
        if backup.exists():
            shutil.rmtree(backup)
    finally:
        if staging.exists():
            shutil.rmtree(staging)
    return manifest


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", choices=("winget", "msstore"))
    parser.add_argument("input", type=Path, help="diretório api do Indexer ou msstore-catalog.json")
    parser.add_argument("output", type=Path, help="diretório de saída do subcatálogo")
    args = parser.parse_args()
    try:
        manifest = build(args.source, args.input, args.output)
    except (OSError, UnicodeError, json.JSONDecodeError, ValueError) as exc:
        parser.error(str(exc))
    print(f"API {args.source} montada: {manifest['packageCount']} pacotes, SHA-256 {manifest['catalogSha256']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
