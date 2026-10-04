"""Build and validate the hierarchical JSON catalog consumed by the Store app."""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import tempfile
import unicodedata
from datetime import UTC, datetime
from pathlib import Path
from typing import Any
from urllib.parse import quote, urlparse


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
    # R2 aceita chaves com distinção de maiúsculas, mas consumidores Windows
    # não. Usar segmentos normalizados em minúsculas mantém uma chave canônica.
    safe_parts = [quote(unicodedata.normalize("NFKC", part).casefold(), safe="") for part in [parts[0], *products]]
    return "/".join(["apps", _prefix(package_id), *safe_parts, "app.json"])


def _detail_url_path(package_id: str) -> str:
    # detailPath é usado diretamente como sufixo de URL; escapar novamente o
    # caminho preserva no URL os percentuais literais usados nas chaves do R2.
    return quote(_detail_path(package_id), safe="/")


def cache_fingerprint(source_revision: str | None, build_revision: str | None, msstore_sha256: str | None) -> str | None:
    values = (source_revision, build_revision, msstore_sha256) if msstore_sha256 else (source_revision, build_revision)
    if any(not isinstance(value, str) or not value.strip() for value in values):
        return None
    payload = json.dumps(values, ensure_ascii=True, separators=(",", ":")).encode("ascii")
    return hashlib.sha256(payload).hexdigest()


def _write_json(path: Path, value) -> bytes:
    payload = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(payload)
    return payload


def build_catalog_v2(
    source_file: str | Path,
    output_dir: str | Path,
    *,
    source_revision: str | None = None,
    build_revision: str | None = None,
    msstore_sha256: str | None = None,
    media_index_file: str | Path | None = None,
) -> dict[str, Any]:
    source = Path(source_file)
    destination = Path(output_dir)
    catalog = json.loads(source.read_text(encoding="utf-8-sig"))
    if not isinstance(catalog, list) or not catalog:
        raise ValueError("apps.json precisa conter uma lista não vazia")

    media_by_id: dict[str, dict] = {}
    if media_index_file:
        media_document = json.loads(Path(media_index_file).read_text(encoding="utf-8-sig"))
        media_entries = media_document.get("apps", {}) if isinstance(media_document, dict) else None
        if not isinstance(media_entries, dict):
            raise ValueError("media-index.json precisa conter o objeto 'apps'")
        media_by_id = {str(package_id).casefold(): value for package_id, value in media_entries.items() if isinstance(value, dict)}

    seen: set[str] = set()
    ordered: list[dict] = []
    path_owners: dict[str, str] = {}
    for app in catalog:
        if not isinstance(app, dict):
            raise ValueError("entrada do apps.json não é um objeto")
        package_id, name = app.get("id"), app.get("name")
        if not isinstance(package_id, str) or not package_id.strip() or not isinstance(name, str) or not name.strip():
            raise ValueError("entrada do apps.json sem ID ou nome válido")
        if not _is_safe_package_id(package_id):
            raise ValueError(f"ID não pode ser representado com segurança na hierarquia de pastas: {package_id!r}")
        key = package_id.casefold()
        # Apps WinGet passam a obter screenshots somente da mídia associada na V2.
        # Não carregue referências do Screenshot_Database legado para o novo catálogo.
        if str(app.get("source", "winget")).casefold() != "msstore":
            app.pop("screenshotUrls", None)
            installers = app.get("installers")
            if not isinstance(installers, list):
                raise ValueError(f"Lista de instaladores ausente ou inválida para {package_id}")
        if key in seen:
            raise ValueError(f"ID duplicado no apps.json: {package_id}")
        seen.add(key)
        if key in media_by_id:
            app["media"] = media_by_id[key]
            screenshot_paths = app["media"].get("screenshots", [])
            if isinstance(screenshot_paths, list) and screenshot_paths:
                base_path = _detail_url_path(package_id).rsplit("/", 1)[0]
                screenshot_hashes = app["media"].get("screenshotSha256", {})
                app["screenshotUrls"] = [
                    "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/Catalog/"
                    + base_path + "/" + quote(path, safe="/")
                    + ("?v=" + screenshot_hashes[path][:16] if isinstance(screenshot_hashes, dict) and isinstance(screenshot_hashes.get(path), str) else "")
                    for path in screenshot_paths
                    if isinstance(path, str) and path.startswith("media/screenshots/")
                ]
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
            search_row = {
                "id": package_id,
                "name": app["name"],
                "publisher": app.get("publisher", ""),
                "source": app.get("source", "winget"),
                "architectures": app.get("architectures", []),
                "installerCount": len(app.get("installers", [])),
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
                "media": app.get("media"),
                "screenshotUrls": app.get("screenshotUrls"),
                "storeScreenshotUrls": app.get("storeScreenshotUrls"),
                "detailPath": _detail_url_path(package_id),
                "recordSha256": hashlib.sha256(detail_bytes).hexdigest(),
            }
            search_index.append(search_row)
            prefix = _prefix(package_id)
            prefixes[prefix] = prefixes.get(prefix, 0) + 1

        index_bytes = _write_json(staging / "manifest" / "search-index.json", search_index)
        media_index_bytes = None
        if media_index_file:
            media_index_bytes = Path(media_index_file).read_bytes()
            (staging / "manifest" / "media-index.json").write_bytes(media_index_bytes)
        catalog_digest.update(hashlib.sha256(index_bytes).digest())
        catalog_sha256 = catalog_digest.hexdigest()
        manifest = {
            "schemaVersion": 2,
            "generatedUtc": datetime.now(UTC).isoformat().replace("+00:00", "Z"),
            "appCount": len(ordered),
            "prefixes": dict(sorted(prefixes.items())),
            "catalogSha256": catalog_sha256,
            "sourceRevision": source_revision,
            "buildRevision": build_revision,
            "msstoreCatalogSha256": msstore_sha256,
            "installerSchemaVersion": 1,
            "inputFingerprint": cache_fingerprint(source_revision, build_revision, msstore_sha256),
            "indexSha256": hashlib.sha256(index_bytes).hexdigest(),
            "mediaIndexSha256": hashlib.sha256(media_index_bytes).hexdigest() if media_index_bytes is not None else None,
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
    search_path = root / "manifest" / "search-index.json"
    if not manifest_path.is_file() or not search_path.is_file():
        raise ValueError("catalog-v2 precisa conter manifest.json e search-index.json")

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    search = json.loads(search_path.read_text(encoding="utf-8"))
    if manifest.get("schemaVersion") != 2 or not isinstance(search, list):
        raise ValueError("schema do catalog-v2 inválido")
    if manifest.get("installerSchemaVersion") != 1:
        raise ValueError("catalog-v2 não inclui o contrato de instaladores")
    if manifest.get("appCount") != len(search) or not search:
        raise ValueError("contagem do manifesto não corresponde ao índice de busca")
    if not isinstance(manifest.get("catalogSha256"), str) or not re.fullmatch(r"[0-9a-f]{64}", manifest["catalogSha256"]):
        raise ValueError("catalogSha256 ausente ou inválido")
    index_digest = hashlib.sha256(search_path.read_bytes()).hexdigest()
    if manifest.get("indexSha256") != index_digest:
        raise ValueError("indexSha256 não corresponde ao índice de busca")
    media_index_path = root / "manifest" / "media-index.json"
    media_index_hash = manifest.get("mediaIndexSha256")
    if media_index_hash:
        if not media_index_path.is_file() or hashlib.sha256(media_index_path.read_bytes()).hexdigest() != media_index_hash:
            raise ValueError("media-index.json ausente ou seu hash diverge do manifesto")
    expected_fingerprint = cache_fingerprint(
        manifest.get("sourceRevision"),
        manifest.get("buildRevision"),
        manifest.get("msstoreCatalogSha256"),
    )
    if manifest.get("inputFingerprint") != expected_fingerprint:
        if manifest.get("inputFingerprint") is not None or expected_fingerprint is not None:
            raise ValueError("inputFingerprint não corresponde aos inputs do catálogo")

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
        if not isinstance(detail_path, str) or detail_path != _detail_url_path(package_id):
            raise ValueError(f"hierarquia de pastas incompatível com PackageIdentifier: {package_id}")
        key = package_id.casefold()
        if key in seen or detail_path in detail_paths:
            raise ValueError(f"ID ou caminho duplicado: {package_id}")
        seen.add(key)
        detail_paths.add(detail_path)

        detail_file = root.joinpath(*_detail_path(package_id).split("/"))
        if not detail_file.is_file():
            raise ValueError(f"detalhe ausente para {package_id}: {detail_path}")
        detail_bytes = detail_file.read_bytes()
        if row.get("recordSha256") != hashlib.sha256(detail_bytes).hexdigest():
            raise ValueError(f"recordSha256 não corresponde ao detalhe de {package_id}")
        detail = json.loads(detail_bytes)
        if not isinstance(detail, dict) or str(detail.get("id", "")).casefold() != key:
            raise ValueError(f"JSON de detalhe não corresponde ao índice: {package_id}")
        installers = detail.get("installers")
        if not isinstance(installers, list) or row.get("installerCount") != len(installers):
            raise ValueError(f"lista de instaladores inválida para {package_id}")
        for installer in installers:
            if not isinstance(installer, dict):
                raise ValueError(f"instalador inválido para {package_id}")
            installer_url = installer.get("url")
            parsed_url = urlparse(installer_url) if isinstance(installer_url, str) else None
            if parsed_url is None or parsed_url.scheme != "https" or not parsed_url.hostname:
                raise ValueError(f"URL de instalador não HTTPS para {package_id}")
            installer_sha = installer.get("sha256")
            if installer_sha is not None and (
                not isinstance(installer_sha, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", installer_sha)
            ):
                raise ValueError(f"SHA-256 de instalador inválido para {package_id}")
            if installer.get("silentSupported") is True and not re.fullmatch(r"[0-9a-fA-F]{64}", installer_sha or ""):
                raise ValueError(f"instalador silencioso sem SHA-256 válido para {package_id}")
        comparable_fields = (
            "name",
            "publisher",
            "source",
            "architectures",
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
            "media",
            "screenshotUrls",
            "storeScreenshotUrls",
        )
        defaults = {"publisher": "", "source": "winget", "version": "", "score": 0, "tags": [], "regionTags": [], "architectures": []}
        if any(row.get(field) != detail.get(field, defaults.get(field)) for field in comparable_fields):
            raise ValueError(f"resumo diverge dos detalhes para {package_id}")

        digest.update(_detail_path(package_id).encode("utf-8"))
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
    return manifest, [*detail_files, search_path, *([media_index_path] if media_index_hash else [])]
