#!/usr/bin/env python3
"""Build a standalone, hierarchical Microsoft Store catalog."""

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


def app_folder_id(value: str) -> str:
    normalized = unicodedata.normalize("NFKD", value.casefold())
    normalized = "".join(char for char in normalized if not unicodedata.combining(char))
    normalized = re.sub(r"[^a-z0-9._-]+", "-", normalized).strip(".-_")
    if not normalized or normalized in {".", ".."}:
        raise ValueError(f"ID MS Store não pode formar um nome de pasta seguro: {value!r}")
    return normalized


def detail_path(package_id: str) -> str:
    first = package_id[0].casefold()
    prefix = "0-9" if first.isdigit() else first if "a" <= first <= "z" else "_"
    return f"apps/{prefix}/{app_folder_id(package_id)}/app.json"


def write_json(path: Path, value) -> bytes:
    body = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(body)
    return body


def build(source_path: Path, destination: Path) -> dict:
    apps = json.loads(source_path.read_text(encoding="utf-8-sig"))
    if not isinstance(apps, list) or not apps:
        raise ValueError("Catálogo MS Store precisa ser uma lista não vazia.")
    seen: set[str] = set()
    owners: dict[str, str] = {}
    normalized = []
    for raw in apps:
        if not isinstance(raw, dict):
            raise ValueError("Entrada MS Store não é um objeto JSON.")
        package_id, name = raw.get("id"), raw.get("name")
        if not isinstance(package_id, str) or not package_id.strip() or not isinstance(name, str) or not name.strip():
            raise ValueError("Entrada MS Store sem ID ou nome válido.")
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}", package_id):
            raise ValueError(f"ID MS Store não suportado para caminho hierárquico: {package_id!r}")
        key = package_id.casefold()
        path = detail_path(package_id)
        if key in seen or path.casefold() in owners:
            raise ValueError(f"ID duplicado ou colisão de pasta MS Store: {package_id!r}")
        seen.add(key)
        owners[path.casefold()] = package_id
        app = dict(raw)
        app["source"] = "msstore"
        normalized.append((path, app))

    normalized.sort(key=lambda pair: (pair[1]["id"].casefold(), pair[1]["id"]))
    parent = destination.parent
    parent.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix=f".{destination.name}.tmp-", dir=parent))
    backup = parent / f".{destination.name}.previous"
    try:
        rows = []
        media_apps = {}
        prefixes = {}
        digest = hashlib.sha256()
        for path, app in normalized:
            detail = dict(app)
            detail.pop("detailPath", None)
            detail.pop("recordSha256", None)
            detail.pop("storeIconUrl", None)
            detail.pop("storeBannerUrl", None)
            detail.pop("storeScreenshotUrls", None)
            body = write_json(staging.joinpath(*path.split("/")), detail)
            record_hash = hashlib.sha256(body).hexdigest()
            digest.update(path.encode("utf-8"))
            digest.update(b"\0")
            digest.update(bytes.fromhex(record_hash))
            fields = (
                "id",
                "name",
                "publisher",
                "source",
                "packageLocale",
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
                "media",
                "screenshotUrls",
                "license",
                "licenseUrl",
                "releaseNotesUrl",
                "architectures",
                "hasGitHubMetrics",
                "gitHubStars",
                "installerSizeBytes",
                "office",
                "detailPath",
                "recordSha256",
            )
            row = {field: detail[field] for field in fields if field in detail}
            row["detailPath"] = quote(path, safe="/")
            row["recordSha256"] = record_hash
            rows.append(row)
            prefix = "0-9" if app["id"][0].isdigit() else app["id"][0].casefold()
            prefixes[prefix] = prefixes.get(prefix, 0) + 1
            if isinstance(app.get("media"), dict):
                media_apps[app["id"].casefold()] = app["media"]

        index_body = write_json(staging / "manifest" / "search-index.json", rows)
        media_document = {
            "schemaVersion": 1,
            "generatedUtc": datetime.now(UTC).isoformat().replace("+00:00", "Z"),
            "apps": media_apps,
        }
        media_body = write_json(staging / "manifest" / "media-index.json", media_document)
        digest.update(hashlib.sha256(index_body).digest())
        manifest = {
            "schemaVersion": 2,
            "catalog": "msstore",
            "generatedUtc": datetime.now(UTC).isoformat().replace("+00:00", "Z"),
            "appCount": len(rows),
            "prefixes": dict(sorted(prefixes.items())),
            "catalogSha256": digest.hexdigest(),
            "indexSha256": hashlib.sha256(index_body).hexdigest(),
            "mediaIndexSha256": hashlib.sha256(media_body).hexdigest(),
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
    parser.add_argument("source", type=Path, help="msstore-catalog.json produzido pelo Indexer")
    parser.add_argument("output", type=Path, help="diretório hierárquico do subcatálogo")
    args = parser.parse_args()
    try:
        manifest = build(args.source, args.output)
    except (OSError, UnicodeError, json.JSONDecodeError, ValueError) as exc:
        parser.error(str(exc))
    print(f"Catálogo MS Store montado: {manifest['appCount']} apps, SHA-256 {manifest['catalogSha256']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
