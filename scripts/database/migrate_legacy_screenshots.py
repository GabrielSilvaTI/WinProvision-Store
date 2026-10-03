#!/usr/bin/env python3
"""Copy legacy WinGet screenshots into normalized V2 app media folders."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import unicodedata
from datetime import UTC, datetime
from pathlib import Path
from urllib.parse import unquote, urlparse

from botocore.exceptions import BotoCoreError, ClientError

sys.path.insert(0, str(Path(__file__).resolve().parent))
import catalog_media  # noqa: E402

LEGACY_INDEX_KEY = "Store/Database/screenshot-index.json"
LEGACY_PREFIX = "Store/Screenshot_Database/"
LEGACY_PUBLIC_HOST = "pub-166b41912a994dbe86583ba10596d673.r2.dev"
IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"}


def normalize_id(value: str) -> str:
    normalized = unicodedata.normalize("NFKD", value.casefold())
    normalized = "".join(char for char in normalized if not unicodedata.combining(char))
    return re.sub(r"[^a-z0-9._-]+", "-", normalized).strip(".-_")


def read_object(client, key: str) -> bytes:
    try:
        return catalog_media.read_object(client, key) or b""
    except (BotoCoreError, ClientError, OSError) as exc:
        raise ValueError(f"Não foi possível ler {key} no R2: {exc}") from exc


def legacy_object(url: str, expected_id: str) -> tuple[str, str] | None:
    parsed = urlparse(url)
    if parsed.scheme != "https" or parsed.hostname != LEGACY_PUBLIC_HOST or parsed.query or parsed.fragment:
        return None
    if not parsed.path.startswith("/" + LEGACY_PREFIX):
        return None
    key = unquote(parsed.path.lstrip("/"))
    parts = key[len(LEGACY_PREFIX):].split("/")
    if len(parts) == 2:
        folder, filename = parts
        source = "winget"
    elif len(parts) == 3 and parts[0].casefold() == "homepage":
        _, folder, filename = parts
        source = "homepage"
    else:
        return None
    if normalize_id(folder) != normalize_id(expected_id):
        return None
    if Path(filename).suffix.casefold() not in IMAGE_EXTENSIONS:
        return None
    return key, source


def v2_screenshot_paths(row: dict, media_record: dict | None) -> list[str]:
    paths: list[str] = []
    for record in (row.get("media"), media_record):
        screenshots = record.get("screenshots") if isinstance(record, dict) else None
        if isinstance(screenshots, list):
            for path in screenshots:
                if isinstance(path, str) and path.startswith("media/screenshots/") and path not in paths:
                    paths.append(path)
    return paths


def content_label(source: str, key: str) -> str:
    filename = key.rsplit("/", 1)[-1]
    stem = filename.rsplit(".", 1)[0]
    if source == "homepage" and stem.casefold().startswith("screenshot-"):
        stem = stem[len("screenshot-"):]
    token = stem.casefold() if re.fullmatch(r"[0-9a-fA-F]{64}", stem) else hashlib.sha256(key.encode("utf-8")).hexdigest()
    return f"legacy-{source}-{token[:12]}"


def read_legacy_index(client) -> dict:
    try:
        body = read_object(client, LEGACY_INDEX_KEY)
        document = json.loads(body)
    except json.JSONDecodeError as exc:
        raise ValueError("Store/Database/screenshot-index.json contém JSON inválido.") from exc
    if not isinstance(document, dict) or document.get("schemaVersion") != 1 or not isinstance(document.get("packages"), dict):
        raise ValueError("Store/Database/screenshot-index.json inválido.")
    return document


def load_plan(client) -> tuple[dict, list[dict], dict, list[dict], dict]:
    manifest, index, media_index = catalog_media.load_catalog(client)
    document = read_legacy_index(client)

    paginator = client.get_paginator("list_objects_v2")
    existing_keys = {
        item["Key"]
        for page in paginator.paginate(Bucket=catalog_media.BUCKET, Prefix=LEGACY_PREFIX)
        for item in page.get("Contents", [])
        if isinstance(item.get("Key"), str)
    }

    catalog_by_normalized_id: dict[str, list[dict]] = {}
    for row in index:
        if not isinstance(row, dict) or not isinstance(row.get("id"), str):
            continue
        if str(row.get("source", "winget")).casefold() == "msstore":
            continue
        catalog_by_normalized_id.setdefault(normalize_id(row["id"]), []).append(row)

    plan: list[dict] = []
    counts = {
        "catalogMatches": 0,
        "appsWithV2Screenshots": 0,
        "legacyReferences": 0,
        "eligibleScreenshots": 0,
        "alreadyMigrated": 0,
        "missingLegacyObject": 0,
        "invalidLegacyUrl": 0,
        "ambiguousId": 0,
        "notInCatalog": 0,
    }
    packages = document["packages"]
    for normalized_package_id, source_groups in packages.items():
        if not isinstance(normalized_package_id, str) or not isinstance(source_groups, dict):
            continue
        rows = catalog_by_normalized_id.get(normalize_id(normalized_package_id), [])
        if len(rows) != 1:
            if len(rows) > 1:
                counts["ambiguousId"] += 1
                plan.append({"id": normalized_package_id, "status": "ambiguous-id"})
            else:
                counts["notInCatalog"] += 1
                plan.append({"id": normalized_package_id, "status": "not-in-catalog"})
            continue

        row = rows[0]
        package_id = str(row["id"])
        counts["catalogMatches"] += 1
        media_record = media_index["apps"].get(package_id.casefold())
        existing_paths = v2_screenshot_paths(row, media_record)
        if existing_paths:
            counts["appsWithV2Screenshots"] += 1

        candidates: list[dict] = []
        for source_field, source_name in (("winget", "winget"), ("homepage", "homepage")):
            urls = source_groups.get(source_field, [])
            if not isinstance(urls, list):
                continue
            for url in urls:
                if not isinstance(url, str):
                    continue
                counts["legacyReferences"] += 1
                parsed = legacy_object(url, normalized_package_id)
                if parsed is None:
                    plan.append({"id": package_id, "url": url, "status": "invalid-or-missing-source"})
                    counts["invalidLegacyUrl"] += 1
                    continue
                object_key, path_source = parsed
                if object_key not in existing_keys:
                    counts["missingLegacyObject"] += 1
                    plan.append({"id": package_id, "sourceKey": object_key, "status": "missing-legacy-object"})
                    continue
                if source_name != path_source:
                    counts["invalidLegacyUrl"] += 1
                    plan.append({"id": package_id, "sourceKey": object_key, "status": "source-kind-mismatch"})
                    continue
                label = content_label(source_name, object_key)
                if any(Path(path).stem.casefold().endswith("-" + label.casefold()) for path in existing_paths):
                    counts["alreadyMigrated"] += 1
                    plan.append({"id": package_id, "sourceKey": object_key, "label": label, "status": "already-migrated"})
                    continue
                candidates.append({"id": package_id, "sourceKey": object_key, "source": source_name, "label": label, "url": url, "status": "ready"})

        candidates.sort(key=lambda item: (0 if item["source"] == "winget" else 1, item["sourceKey"].casefold(), item["sourceKey"]))
        for item in candidates:
            counts["eligibleScreenshots"] += 1
            plan.append(item)

    return manifest, index, media_index, plan, counts


def publish_batch(client, manifest: dict, index: list[dict], media_index: dict, batch: list[dict]) -> tuple[int, int]:
    published = 0
    unchanged = 0
    for item in batch:
        try:
            body = read_object(client, item["sourceKey"])
            extension = catalog_media.validate_image(body, "screenshot")
            digest = hashlib.sha256(body).hexdigest()
            # Include both source identity and content digest so publish_one()
            # appends this legacy image instead of replacing a same-label V2 image.
            label = f"legacy-{item['source']}-{digest[:12]}"
            result = catalog_media.publish_one(
                client,
                manifest,
                index,
                media_index,
                item["id"],
                "screenshot",
                body,
                extension,
                item["url"],
                label,
            )
            item.update({**result, "label": label})
            if result["status"] == "published":
                published += 1
            else:
                unchanged += 1
        except (BotoCoreError, ClientError, OSError, ValueError) as exc:
            item.update({"status": "source-error", "error": f"{type(exc).__name__}: {exc}"})
            print(f"{item['id']}: screenshot ignorada ({type(exc).__name__}: {exc})", file=sys.stderr)

    if published:
        catalog_media.publish_catalog_checkpoint(client, manifest, index, media_index, update_search_index=True)
    return published, unchanged


def resume_plan(client, index: list[dict], media_index: dict, initial_plan: list[dict]) -> list[dict]:
    """Rebuild pending work from the live catalog so repeated --apply runs resume safely."""
    document = read_legacy_index(client)
    catalog_by_normalized_id: dict[str, list[dict]] = {}
    for row in index:
        if isinstance(row, dict) and isinstance(row.get("id"), str) and str(row.get("source", "winget")).casefold() != "msstore":
            catalog_by_normalized_id.setdefault(normalize_id(row["id"]), []).append(row)
    paginator = client.get_paginator("list_objects_v2")
    existing_keys = {
        item["Key"]
        for page in paginator.paginate(Bucket=catalog_media.BUCKET, Prefix=LEGACY_PREFIX)
        for item in page.get("Contents", [])
        if isinstance(item.get("Key"), str)
    }
    pending: list[dict] = []
    seen: set[tuple[str, str]] = set()
    for old_id, groups in document["packages"].items():
        rows = catalog_by_normalized_id.get(normalize_id(old_id), [])
        if len(rows) != 1 or not isinstance(groups, dict):
            continue
        package_id = str(rows[0]["id"])
        media_record = media_index["apps"].get(package_id.casefold())
        existing_paths = v2_screenshot_paths(rows[0], media_record)
        for source in ("winget", "homepage"):
            urls = groups.get(source, [])
            if not isinstance(urls, list):
                continue
            for url in urls:
                if not isinstance(url, str):
                    continue
                parsed = legacy_object(url, old_id)
                if parsed is None:
                    continue
                key, path_source = parsed
                if path_source != source or key not in existing_keys or (package_id.casefold(), key) in seen:
                    continue
                seen.add((package_id.casefold(), key))
                label = content_label(source, key)
                if any(Path(path).stem.casefold().endswith("-" + label.casefold()) for path in existing_paths):
                    continue
                pending.append({"id": package_id, "sourceKey": key, "source": source, "label": label, "url": url, "status": "ready"})
    initial_ready = {(item.get("id", "").casefold(), item.get("sourceKey")) for item in initial_plan if item.get("status") == "ready"}
    return [item for item in pending if (item["id"].casefold(), item["sourceKey"]) in initial_ready]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true", help="publica as screenshots; sem isso apenas audita")
    parser.add_argument("--limit", type=int, default=0, help="limite de imagens nesta execução; 0 = todas")
    parser.add_argument("--batch-size", type=int, default=100, help="imagens por checkpoint publicado")
    parser.add_argument("--report", type=Path, default=Path("legacy-screenshot-migration-report.json"))
    args = parser.parse_args()
    if args.limit < 0 or args.batch_size < 1:
        parser.error("limit precisa ser >= 0 e batch-size precisa ser positivo")

    try:
        client = catalog_media.r2_client()
        manifest, index, media_index, plan, counts = load_plan(client)
        ready = [item for item in plan if item["status"] == "ready"]
        report = {
            "schemaVersion": 1,
            "generatedUtc": datetime.now(UTC).isoformat().replace("+00:00", "Z"),
            "mode": "apply" if args.apply else "audit",
            "counts": counts,
            "items": plan,
        }
        if args.apply:
            ready = resume_plan(client, index, media_index, plan)
            if args.limit:
                ready = ready[: args.limit]
            report["counts"]["selectedForApply"] = len(ready)
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
            for offset in range(0, len(ready), args.batch_size):
                batch = ready[offset : offset + args.batch_size]
                published, unchanged = publish_batch(client, manifest, index, media_index, batch)
                print(f"Lote {offset // args.batch_size + 1}: {published} screenshot(s) publicada(s), {unchanged} já existente(s); última: {batch[-1]['id']}", flush=True)
                args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        else:
            report["counts"]["eligibleToCopy"] = len(ready)

        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps(report["counts"], ensure_ascii=False, indent=2))
        print(f"Relatório: {args.report.resolve()}")
        return 0
    except (BotoCoreError, ClientError, KeyError, OSError, ValueError, json.JSONDecodeError) as exc:
        print(f"Migração cancelada: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
