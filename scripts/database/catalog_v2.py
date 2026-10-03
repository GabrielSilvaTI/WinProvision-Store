"""Build and validate the hierarchical JSON catalog consumed by the Store app."""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import tempfile
from datetime import UTC, datetime
from pathlib import Path
from urllib.parse import quote


def _is_safe_package_id(package_id: str) -> bool:
    return (
        bool(package_id)
        and len(package_id) <= 128
        and package_id[0].isalnum()
        and all(character.isprintable() and not character.isspace() and character not in "/\\" for character in package_id)
        and all(part not in {"", ".", ".."} for part in package_id.split("."))
    )


def _prefix(package_id: str) -> str:
    first = package_id[0].lower()
    return first if first in "0123456789abcdefghijklmnopqrstuvwxyz" else "_"


def _detail_path(package_id: str) -> str:
    parts = package_id.split(".")
    products = parts[1:] if len(parts) > 1 else [package_id]
    safe_parts = [quote(part, safe="") for part in [parts[0], *products]]
    # R2 usa chaves case-sensitive. Normalizar os segmentos também evita colisão
    # de paths entre IDs que diferem apenas por maiúsculas/minúsculas no Windows.
    safe_parts = [segment.casefold() for segment in safe_parts]
    return "/".join(["apps", _prefix(package_id), *safe_parts, "app.json"])


def _write_json(path: Path, value) -> bytes:
    payload = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(payload)
    return payload


def build_catalog_v2(source_file: str | Path, output_dir: str | Path) -> dict:
    source = Path(source_file)
    destination = Path(output_dir)
    catalog = json.loads(source.read_text(encoding="utf-8-sig"))
    if not isinstance(catalog, list) or not catalog:
        raise ValueError("apps.json precisa conter uma lista não vazia")

    seen: set[str] = set()
    ordered: list[dict] = []
    path_owners: dict[str, str] = {}
    for app in catalog:
        if not isinstance(app, dict):
            raise ValueError("entrada do apps.json não é um objeto")
        package_id, name = app.get("id"), app.get("name")
        if not isinstance(package_id, str) or not package_id.strip() or not isinstance(name, str) or not name.strip():
            raise ValueError("entrada do apps.json sem ID ou nome válido")
        parts = package_id.split(".")
        if not _is_safe_package_id(package_id):
            raise ValueError(f"ID não pode ser representado com segurança na hierarquia de pastas: {package_id!r}")
        key = package_id.casefold()
        if key in seen:
            raise ValueError(f"ID duplicado no apps.json: {package_id}")
        seen.add(key)
        relative_path = _detail_path(package_id)
        owner = path_owners.get(relative_path)
        if owner is not None:
            raise ValueError(f"IDs colidem no caminho de detalhe ({owner}, {package_id})")
        path_owners[relative_path] = package_id
        ordered.append(app)

    ordered.sort(key=lambda app: (app["id"].casefold(), app["id"]))
    parent = destination.parent
    parent.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix=f".{destination.name}.tmp-", dir=parent))
    backup = parent / f".{destination.name}.previous"
    prefixes: dict[str, int] = {}
    search_index: list[dict] = []
    catalog_digest = hashlib.sha256()

    try:
        for app in ordered:
            package_id = app["id"]
            relative = _detail_path(package_id)
            detail_bytes = _write_json(staging.joinpath(*relative.split("/")), app)
            catalog_digest.update(relative.encode("utf-8"))
            catalog_digest.update(b"\0")
            catalog_digest.update(hashlib.sha256(detail_bytes).digest())
            search_index.append(
                {
                    "id": package_id,
                    "name": app["name"],
                    "publisher": app.get("publisher", ""),
                    "source": app.get("source", "winget"),
                    "packageLocale": app.get("packageLocale"),
                    "storeIconUrl": app.get("storeIconUrl"),
                    "storeBannerUrl": app.get("storeBannerUrl"),
                    "storeCategory": app.get("storeCategory"),
                    "storeSubCategory": app.get("storeSubCategory"),
                    "storeRating": app.get("storeRating"),
                    "storeRatingCount": app.get("storeRatingCount"),
                    "version": app.get("version", ""),
                    "description": app.get("description"),
                    "homepage": app.get("homepage"),
                    "packageUrl": app.get("packageUrl"),
                    "publisherUrl": app.get("publisherUrl"),
                    "score": app.get("score", 0),
                    "moniker": app.get("moniker"),
                    "tags": app.get("tags", []),
                    "regionTags": app.get("regionTags", []),
                    "gitHubStars": app.get("gitHubStars"),
                    "installerSizeBytes": app.get("installerSizeBytes"),
                    "detailPath": relative,
                }
            )
            prefix = _prefix(package_id)
            prefixes[prefix] = prefixes.get(prefix, 0) + 1

        index_bytes = _write_json(staging / "search-index.json", search_index)
        catalog_digest.update(hashlib.sha256(index_bytes).digest())
        catalog_sha256 = catalog_digest.hexdigest()
        manifest = {
            "schemaVersion": 2,
            "generatedUtc": datetime.now(UTC).isoformat().replace("+00:00", "Z"),
            "appCount": len(ordered),
            "prefixes": dict(sorted(prefixes.items())),
            "catalogSha256": catalog_sha256,
            "basePath": f"releases/{catalog_sha256}",
        }
        _write_json(staging / "manifest.json", manifest)
        validate_catalog_v2(staging)

        if backup.exists():
            shutil.rmtree(backup)
        if destination.exists():
            os.replace(destination, backup)
        try:
            os.replace(staging, destination)
        except Exception:
            if backup.exists() and not destination.exists():
                os.replace(backup, destination)
            raise
        if backup.exists():
            shutil.rmtree(backup)
    finally:
        if staging.exists():
            shutil.rmtree(staging)

    return manifest


def validate_catalog_v2(directory: str | Path) -> tuple[dict, list[Path]]:
    root = Path(directory)
    manifest_path = root / "manifest.json"
    search_path = root / "search-index.json"
    if not manifest_path.is_file() or not search_path.is_file():
        raise ValueError("catalog-v2 precisa conter manifest.json e search-index.json")

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    search = json.loads(search_path.read_text(encoding="utf-8"))
    if manifest.get("schemaVersion") != 2 or not isinstance(search, list):
        raise ValueError("schema do catalog-v2 inválido")
    if manifest.get("appCount") != len(search) or not search:
        raise ValueError("contagem do manifesto não corresponde ao índice de busca")
    if not isinstance(manifest.get("catalogSha256"), str) or len(manifest["catalogSha256"]) != 64:
        raise ValueError("catalogSha256 ausente ou inválido")
    if manifest.get("basePath") != f"releases/{manifest['catalogSha256']}":
        raise ValueError("basePath não corresponde ao hash do catálogo")

    seen: set[str] = set()
    detail_paths: set[str] = set()
    prefix_counts: dict[str, int] = {}
    digest = hashlib.sha256()
    for row in search:
        if not isinstance(row, dict):
            raise ValueError("entrada do índice não é um objeto")
        package_id, detail_path = row.get("id"), row.get("detailPath")
        if not isinstance(package_id, str) or not package_id.strip():
            raise ValueError("entrada do índice sem ID")
        if not isinstance(detail_path, str) or detail_path != _detail_path(package_id):
            raise ValueError(f"hierarquia de pastas incompatível com PackageIdentifier: {package_id}")
        key = package_id.casefold()
        if key in seen or detail_path in detail_paths:
            raise ValueError(f"ID ou caminho duplicado: {package_id}")
        seen.add(key)
        detail_paths.add(detail_path)

        detail_file = root.joinpath(*detail_path.split("/"))
        if not detail_file.is_file():
            raise ValueError(f"detalhe ausente para {package_id}: {detail_path}")
        detail_bytes = detail_file.read_bytes()
        detail = json.loads(detail_bytes)
        if not isinstance(detail, dict) or str(detail.get("id", "")).casefold() != key:
            raise ValueError(f"JSON de detalhe não corresponde ao índice: {package_id}")
        comparable_fields = (
            "name",
            "publisher",
            "source",
            "packageLocale",
            "storeIconUrl",
            "storeBannerUrl",
            "storeCategory",
            "storeSubCategory",
            "storeRating",
            "storeRatingCount",
            "version",
            "description",
            "homepage",
            "packageUrl",
            "publisherUrl",
            "score",
            "moniker",
            "tags",
            "regionTags",
            "gitHubStars",
            "installerSizeBytes",
        )
        defaults = {"publisher": "", "source": "winget", "version": "", "score": 0, "tags": [], "regionTags": []}
        if any(row.get(field) != detail.get(field, defaults.get(field)) for field in comparable_fields):
            raise ValueError(f"resumo diverge dos detalhes para {package_id}")

        digest.update(detail_path.encode("utf-8"))
        digest.update(b"\0")
        digest.update(hashlib.sha256(detail_bytes).digest())
        prefix = _prefix(package_id)
        prefix_counts[prefix] = prefix_counts.get(prefix, 0) + 1

    detail_files = sorted((root / "apps").rglob("app.json"))
    if len(detail_files) != len(search):
        raise ValueError("quantidade de JSONs de detalhe não corresponde ao índice")
    if manifest.get("prefixes") != dict(sorted(prefix_counts.items())):
        raise ValueError("contagem por prefixo diverge do manifesto")
    digest.update(hashlib.sha256(search_path.read_bytes()).digest())
    if digest.hexdigest() != manifest["catalogSha256"]:
        raise ValueError("catalogSha256 não corresponde ao conteúdo")
    return manifest, [search_path, *detail_files]
