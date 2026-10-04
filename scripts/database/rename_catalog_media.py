#!/usr/bin/env python3
"""Rename already-published catalog media to app and role based filenames."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
from datetime import UTC, datetime
from urllib.parse import quote, unquote

import boto3
from botocore.config import Config
from botocore.exceptions import ClientError

from catalog_media_naming import banner_filename, icon_filename, screenshot_filename, screenshot_slot

BUCKET = os.environ.get("R2_BUCKET") or "winprovision"
PUBLIC_BASE = os.environ.get(
    "R2_PUBLIC_BASE", "https://pub-166b41912a994dbe86583ba10596d673.r2.dev"
).rstrip("/")
CATALOGS = ("Store/Catalog", "Store/Catalog/msstore")


def compact(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def client_from_env():
    return boto3.client(
        "s3",
        endpoint_url=f"https://{os.environ['R2_ACCOUNT_ID']}.r2.cloudflarestorage.com",
        aws_access_key_id=os.environ["R2_ACCESS_KEY_ID"],
        aws_secret_access_key=os.environ["R2_SECRET_ACCESS_KEY"],
        config=Config(signature_version="s3v4", retries={"max_attempts": 5, "mode": "standard"}),
        region_name="auto",
    )


def object_bytes(client, key: str) -> bytes:
    return client.get_object(Bucket=BUCKET, Key=key)["Body"].read()


def object_digest(client, key: str) -> str:
    head = client.head_object(Bucket=BUCKET, Key=key)
    value = head.get("Metadata", {}).get("sha256")
    if isinstance(value, str) and len(value) == 64:
        return value.lower()
    return hashlib.sha256(object_bytes(client, key)).hexdigest()


def safe_media_path(path: object) -> str | None:
    if not isinstance(path, str) or not path.startswith("media/"):
        return None
    parts = path.split("/")
    if any(part in {"", ".", ".."} for part in parts):
        return None
    return path


def migrate_catalog(client, prefix: str, apply: bool) -> tuple[int, int, int]:
    manifest_key = f"{prefix}/manifest.json"
    index_key = f"{prefix}/manifest/search-index.json"
    media_key = f"{prefix}/manifest/media-index.json"
    manifest = json.loads(object_bytes(client, manifest_key))
    rows = json.loads(object_bytes(client, index_key))
    media_doc = json.loads(object_bytes(client, media_key))
    if manifest.get("schemaVersion") != 2 or not isinstance(rows, list) or not isinstance(media_doc.get("apps"), dict):
        raise ValueError(f"Estrutura V2 inválida em {prefix}")

    changed_assets = 0
    changed_details = 0
    source_keys: set[str] = set()
    destination_keys: set[str] = set()
    pending_uploads: list[tuple[str, str]] = []
    details: list[tuple[str, dict, dict]] = []
    url_updates: dict[str, str] = {}
    destination_urls: dict[str, str] = {}
    destination_digests: dict[str, str] = {}

    for row in rows:
        app_id, detail_path = row.get("id"), row.get("detailPath")
        if not isinstance(app_id, str) or not isinstance(detail_path, str):
            raise ValueError(f"Linha sem ID/detalhe em {prefix}")
        detail_key = f"{prefix}/{unquote(detail_path)}"
        detail = json.loads(object_bytes(client, detail_key))
        if str(detail.get("id", "")).casefold() != app_id.casefold():
            raise ValueError(f"Detalhe de app incompatível: {app_id}")
        app_dir = detail_key.rsplit("/", 1)[0]
        media = next(
            (
                candidate
                for candidate in (
                    detail.get("media"),
                    row.get("media"),
                    media_doc["apps"].get(app_id.casefold()),
                )
                if isinstance(candidate, dict) and candidate
            ),
            {},
        )
        updated = dict(media)
        for slot, filename_builder in (("icon", icon_filename), ("banner", banner_filename)):
            old_path = safe_media_path(media.get(slot))
            if not old_path:
                continue
            extension = old_path.rsplit(".", 1)[-1].lower() if "." in old_path else "png"
            new_path = f"media/{filename_builder(app_id, extension)}"
            old_key = f"{app_dir}/{old_path}"
            new_key = f"{app_dir}/{new_path}"
            digest = object_digest(client, old_key)
            if old_key != new_key:
                source_keys.add(old_key)
                pending_uploads.append((old_key, new_key))
                changed_assets += 1
            destination_keys.add(new_key)
            updated[slot] = new_path
            if slot == "icon":
                updated["iconSha256"] = digest
            url_updates[old_key] = f"{PUBLIC_BASE}/{quote(new_key, safe='/')}?v={digest[:16]}"
            destination_urls[new_key] = url_updates[old_key]
            destination_digests[new_key] = digest

        screenshots = media.get("screenshots") if isinstance(media.get("screenshots"), list) else []
        new_screenshots = []
        screenshot_labels = dict(media.get("screenshotLabels", {})) if isinstance(media.get("screenshotLabels"), dict) else {}
        screenshot_hashes = {}
        for number, item in enumerate(screenshots, start=1):
            old_path = safe_media_path(item)
            if not old_path or not old_path.startswith("media/screenshots/"):
                new_screenshots.append(item)
                continue
            extension = old_path.rsplit(".", 1)[-1].lower() if "." in old_path else "png"
            previous_slot = screenshot_slot(old_path)
            label = previous_slot[1] if previous_slot else None
            filename = screenshot_filename(app_id, number, extension)
            new_path = f"media/screenshots/{filename}"
            old_key = f"{app_dir}/{old_path}"
            new_key = f"{app_dir}/{new_path}"
            digest = object_digest(client, old_key)
            if old_key != new_key:
                source_keys.add(old_key)
                pending_uploads.append((old_key, new_key))
                changed_assets += 1
            destination_keys.add(new_key)
            new_screenshots.append(new_path)
            screenshot_hashes[new_path] = digest
            if label:
                screenshot_labels[label.casefold()] = number
            url_updates[old_key] = f"{PUBLIC_BASE}/{quote(new_key, safe='/')}?v={digest[:16]}"
            destination_urls[new_key] = url_updates[old_key]
            destination_digests[new_key] = digest
        if "screenshots" in media:
            updated["screenshots"] = new_screenshots
            updated["screenshotLabels"] = screenshot_labels
            updated["screenshotSha256"] = screenshot_hashes

        if updated != media or (updated and detail.get("media") != updated):
            if updated:
                detail["media"] = updated
                row["media"] = updated
            changed_details += 1
        if updated or app_id.casefold() in media_doc["apps"]:
            media_doc["apps"][app_id.casefold()] = updated
        details.append((detail_key, detail, row))

    # Update public fields that point to catalog media, but leave external source URLs alone.
    for detail_key, detail, row in details:
        app_id = detail["id"]
        app_dir = detail_key.rsplit("/", 1)[0]
        media = detail.get("media", {})
        if isinstance(media.get("icon"), str):
            key = f"{app_dir}/{media['icon']}"
            if key in destination_keys and "storeIconUrl" in detail:
                detail["storeIconUrl"] = destination_urls.get(key, detail["storeIconUrl"])
        if isinstance(media.get("banner"), str):
            key = f"{app_dir}/{media['banner']}"
            if key in destination_keys and "storeBannerUrl" in detail:
                detail["storeBannerUrl"] = destination_urls.get(key, detail["storeBannerUrl"])
        for field in ("screenshotUrls", "storeScreenshotUrls"):
            paths = media.get("screenshots") if isinstance(media.get("screenshots"), list) else []
            urls = [destination_urls.get(f"{app_dir}/{path}") for path in paths]
            if field in detail and urls and all(urls):
                detail[field] = urls
        detail_bytes = compact(detail)
        digest = hashlib.sha256(detail_bytes).hexdigest()
        row["recordSha256"] = digest
        for field in ("media", "screenshotUrls", "storeScreenshotUrls", "storeIconUrl", "storeBannerUrl"):
            if field in detail:
                row[field] = detail[field]
            else:
                row.pop(field, None)
    # Carry out media copies before publishing metadata that refers to the new keys.
    if apply:
        for old_key, new_key in dict.fromkeys(pending_uploads):
            client.copy_object(
                Bucket=BUCKET,
                Key=new_key,
                CopySource={"Bucket": BUCKET, "Key": old_key},
                MetadataDirective="COPY",
                CacheControlDirective="COPY",
            )
        for key, expected in destination_digests.items():
            if object_digest(client, key) != expected:
                raise ValueError(f"SHA-256 da mídia copiada diverge: {key}")

    # Recompute source object bytes and hashes for the two catalog contracts.
    index_bytes = compact(rows)
    media_doc["generatedUtc"] = datetime.now(UTC).isoformat().replace("+00:00", "Z")
    media_bytes = compact(media_doc)
    digest = hashlib.sha256()
    for row in rows:
        detail_path = unquote(row["detailPath"])
        detail_key = f"{prefix}/{detail_path}"
        detail_bytes = compact(next(item[1] for item in details if item[0] == detail_key))
        digest.update(detail_path.encode("utf-8"))
        digest.update(b"\0")
        digest.update(hashlib.sha256(detail_bytes).digest())
    digest.update(hashlib.sha256(index_bytes).digest())
    manifest["catalogSha256"] = digest.hexdigest()
    manifest["indexSha256"] = hashlib.sha256(index_bytes).hexdigest()
    manifest["mediaIndexSha256"] = hashlib.sha256(media_bytes).hexdigest()
    manifest["generatedUtc"] = datetime.now(UTC).isoformat().replace("+00:00", "Z")

    print(f"{prefix}: {changed_assets} objeto(s) de mídia para renomear; {changed_details} detalhes afetados.", flush=True)
    if not apply:
        return changed_assets, changed_details, len(source_keys)

    client.put_object(Bucket=BUCKET, Key=media_key, Body=media_bytes, ContentType="application/json", CacheControl="no-cache, max-age=0, must-revalidate")
    for detail_key, detail, _ in details:
        detail_bytes = compact(detail)
        client.put_object(Bucket=BUCKET, Key=detail_key, Body=detail_bytes, ContentType="application/json", CacheControl="public, max-age=300, must-revalidate", Metadata={"sha256": hashlib.sha256(detail_bytes).hexdigest()})
    client.put_object(Bucket=BUCKET, Key=index_key, Body=index_bytes, ContentType="application/json", CacheControl="no-cache, max-age=0, must-revalidate")
    client.put_object(Bucket=BUCKET, Key=manifest_key, Body=compact(manifest), ContentType="application/json", CacheControl="no-cache, max-age=0, must-revalidate")

    orphaned = source_keys - destination_keys
    keys_to_delete = sorted(orphaned)
    for offset in range(0, len(keys_to_delete), 1000):
        batch = keys_to_delete[offset : offset + 1000]
        response = client.delete_objects(
            Bucket=BUCKET,
            Delete={"Objects": [{"Key": key} for key in batch], "Quiet": True},
        )
        errors = response.get("Errors", [])
        if errors:
            raise ValueError(f"Falha ao remover {len(errors)} mídia(s) antiga(s) após publicar o catálogo")
    return changed_assets, changed_details, len(orphaned)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true", help="copia, atualiza os catálogos e remove nomes antigos após publicar")
    args = parser.parse_args()
    try:
        client = client_from_env()
        totals = [0, 0, 0]
        for prefix in CATALOGS:
            values = migrate_catalog(client, prefix, args.apply)
            totals = [left + right for left, right in zip(totals, values, strict=True)]
        action = "Migração concluída" if args.apply else "Prévia concluída"
        print(f"{action}: {totals[0]} mídias renomeadas; {totals[1]} detalhes revisados; {totals[2]} chaves antigas removíveis.")
        if not args.apply:
            print("Nenhum objeto foi alterado. Adicione --apply para publicar a migração.")
        return 0
    except (ClientError, KeyError, OSError, UnicodeError, json.JSONDecodeError, ValueError) as exc:
        print(f"Migração interrompida: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
