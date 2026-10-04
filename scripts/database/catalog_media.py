#!/usr/bin/env python3
"""Capture validated images and attach them to the stable per-app catalog objects."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import re
import sys
import unicodedata
from concurrent.futures import ThreadPoolExecutor
from datetime import UTC, datetime
from pathlib import Path
from urllib.parse import quote, unquote, urlparse

import boto3
from botocore.config import Config
from botocore.exceptions import ClientError
from PIL import Image, UnidentifiedImageError

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "icons"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import probe_cdn_icons as icon_probe  # noqa: E402
import sync_cdn_icons as icon_sync  # noqa: E402
from catalog_v2 import _detail_path  # noqa: E402
from catalog_media_naming import icon_filename, screenshot_filename, screenshot_slot  # noqa: E402
from sync_homepage_screenshots import SafeHttp, fetch_screenshot  # noqa: E402

PUBLIC_BASE = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/Catalog"
BUCKET = os.environ.get("R2_BUCKET") or "winprovision"
MANIFEST_KEY = "Store/Catalog/manifest.json"
SEARCH_KEY = "Store/Catalog/manifest/search-index.json"
MEDIA_INDEX_KEY = "Store/Catalog/manifest/media-index.json"
MAX_MEDIA_BYTES = 20 * 1024 * 1024
CONTENT_TYPES = {"png": "image/png", "jpg": "image/jpeg", "webp": "image/webp", "gif": "image/gif", "bmp": "image/bmp", "ico": "image/x-icon", "tif": "image/tiff"}


def r2_client():
    return boto3.client(
        "s3",
        endpoint_url=f"https://{os.environ['R2_ACCOUNT_ID']}.r2.cloudflarestorage.com",
        aws_access_key_id=os.environ["R2_ACCESS_KEY_ID"],
        aws_secret_access_key=os.environ["R2_SECRET_ACCESS_KEY"],
        config=Config(signature_version="s3v4", retries={"max_attempts": 5, "mode": "standard"}),
        region_name="auto",
    )


def read_object(client, key: str) -> bytes | None:
    try:
        return client.get_object(Bucket=BUCKET, Key=key)["Body"].read()
    except ClientError as exc:
        if exc.response.get("Error", {}).get("Code") in {"404", "NoSuchKey", "NotFound"}:
            return None
        raise


def object_sha256(client, key: str) -> str | None:
    """Read an object's stored digest, calculating it when older metadata is absent."""
    try:
        head = client.head_object(Bucket=BUCKET, Key=key)
    except ClientError as exc:
        if exc.response.get("Error", {}).get("Code") in {"404", "NoSuchKey", "NotFound"}:
            return None
        raise
    digest = head.get("Metadata", {}).get("sha256")
    if isinstance(digest, str) and re.fullmatch(r"[0-9a-fA-F]{64}", digest):
        return digest.lower()
    body = read_object(client, key)
    return hashlib.sha256(body).hexdigest() if body is not None else None


def put_json(client, key: str, value: dict | list, cache: str = "no-cache, max-age=0, must-revalidate") -> bytes:
    body = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    client.put_object(Bucket=BUCKET, Key=key, Body=body, ContentType="application/json", CacheControl=cache)
    return body


def slug(value: str) -> str:
    normalized = unicodedata.normalize("NFKD", value.casefold())
    normalized = "".join(char for char in normalized if not unicodedata.combining(char))
    normalized = re.sub(r"[^a-z0-9]+", "-", normalized).strip("-")
    return (normalized[:64].rstrip("-") or "image")


def validate_image(data: bytes, kind: str) -> str:
    if not data or len(data) > MAX_MEDIA_BYTES:
        raise ValueError("imagem vazia ou acima do limite de 20 MiB")
    try:
        with Image.open(io.BytesIO(data)) as image:
            image.verify()
            image_format, dimensions = image.format, image.size
    except (OSError, UnidentifiedImageError, Image.DecompressionBombError) as exc:
        raise ValueError(f"imagem inválida: {exc}") from exc
    extensions = {"PNG": "png", "JPEG": "jpg", "WEBP": "webp", "GIF": "gif", "BMP": "bmp", "TIFF": "tif", "ICO": "ico"}
    extension = extensions.get(image_format or "")
    if not extension or min(dimensions) < 16:
        raise ValueError(f"formato ou dimensões inválidos: {image_format} {dimensions}")
    if kind == "icon" and max(dimensions) > 2048:
        raise ValueError("ícone acima do limite de 2048 px")
    return extension


def download_image(url: str, *, homepage: bool = False) -> tuple[bytes, str, str]:
    parsed = urlparse(url)
    if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password:
        raise ValueError("A URL da imagem precisa usar HTTPS e não pode conter credenciais.")
    if homepage:
        found = fetch_screenshot(SafeHttp(), url)
        if not found:
            raise ValueError("Nenhuma imagem identificada como screenshot foi encontrada na página.")
        body, extension, source_url = found
        validate_image(body, "screenshot")
        return body, extension, source_url
    body, content_type, final_url = SafeHttp().get(url, MAX_MEDIA_BYTES)
    if content_type and not content_type.startswith("image/"):
        raise ValueError(f"A URL retornou {content_type}, não uma imagem.")
    extension = validate_image(body, "screenshot")
    return body, extension, final_url


def load_catalog(client) -> tuple[dict, list[dict], dict]:
    manifest_bytes = read_object(client, MANIFEST_KEY)
    index_bytes = read_object(client, SEARCH_KEY)
    if not manifest_bytes or not index_bytes:
        raise ValueError("Catálogo estável ainda não foi publicado em Store/Catalog/.")
    manifest = json.loads(manifest_bytes)
    index = json.loads(index_bytes)
    if not isinstance(manifest, dict) or manifest.get("schemaVersion") != 2 or not isinstance(index, list):
        raise ValueError("Manifesto ou índice do catálogo inválido.")
    index_hash = hashlib.sha256(index_bytes).hexdigest()
    if index_hash != manifest.get("indexSha256") or len(index) != manifest.get("appCount"):
        raise ValueError("O índice do catálogo não corresponde ao manifesto.")
    media_index_bytes = read_object(client, MEDIA_INDEX_KEY)
    media_index = json.loads(media_index_bytes) if media_index_bytes else {"schemaVersion": 1, "apps": {}}
    if not isinstance(media_index, dict) or not isinstance(media_index.get("apps"), dict):
        raise ValueError("media-index.json inválido; publicação cancelada.")
    return manifest, index, media_index


def media_key(package_id: str, kind: str, filename: str) -> str:
    detail = _detail_path(package_id)
    parent = detail.rsplit("/", 1)[0]
    return f"Store/Catalog/{parent}/media/{'screenshots/' if kind == 'screenshot' else ''}{filename}"


def normalize_media(media: dict) -> dict:
    if not isinstance(media, dict):
        media = {}
    icon = media.get("icon") if isinstance(media.get("icon"), str) else None
    icon_sha256 = media.get("iconSha256") if isinstance(media.get("iconSha256"), str) else None
    screenshots = media.get("screenshots", [])
    if not isinstance(screenshots, list):
        screenshots = []
    screenshots = list(dict.fromkeys(path for path in screenshots if isinstance(path, str)))
    labels = media.get("screenshotLabels", {})
    if not isinstance(labels, dict):
        labels = {}
    labels = {str(label): int(number) for label, number in labels.items() if str(number).isdigit() and int(number) > 0}
    hashes = media.get("screenshotSha256", {})
    if not isinstance(hashes, dict):
        hashes = {}
    hashes = {str(path): str(value).lower() for path, value in hashes.items() if isinstance(value, str) and re.fullmatch(r"[0-9a-fA-F]{64}", value)}
    return {"icon": icon, "iconSha256": icon_sha256, "screenshots": screenshots, "screenshotLabels": labels, "screenshotSha256": hashes}


def screenshot_public_urls(client, detail_path: str, screenshot_paths: list[str]) -> list[str]:
    app_directory = unquote(detail_path).rsplit("/", 1)[0]
    urls = []
    for path in screenshot_paths:
        key = media_key_from_detail(detail_path, path)
        url = f"{PUBLIC_BASE}/{quote(app_directory + '/' + path, safe='/')}"
        digest = object_sha256(client, key)
        if digest:
            url += f"?v={digest[:16]}"
        urls.append(url)
    return urls


def media_key_from_detail(detail_path: str, media_path: str) -> str:
    app_directory = unquote(detail_path).rsplit("/", 1)[0]
    return f"Store/Catalog/{app_directory}/{media_path}"


def publish_catalog_checkpoint(client, manifest: dict, index: list[dict], media_index: dict, *, update_search_index: bool) -> None:
    media_index["generatedUtc"] = datetime.now(UTC).isoformat().replace("+00:00", "Z")
    media_index_bytes = put_json(client, MEDIA_INDEX_KEY, media_index)
    manifest["mediaIndexSha256"] = hashlib.sha256(media_index_bytes).hexdigest()

    if update_search_index:
        index_bytes = json.dumps(index, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        digest_builder = hashlib.sha256()
        for entry in index:
            raw_path = unquote(str(entry["detailPath"]))
            digest_builder.update(raw_path.encode("utf-8"))
            digest_builder.update(b"\0")
            digest_builder.update(bytes.fromhex(entry["recordSha256"]))
        digest_builder.update(hashlib.sha256(index_bytes).digest())
        manifest["indexSha256"] = hashlib.sha256(index_bytes).hexdigest()
        manifest["catalogSha256"] = digest_builder.hexdigest()
        manifest["generatedUtc"] = datetime.now(UTC).isoformat().replace("+00:00", "Z")
        client.put_object(Bucket=BUCKET, Key=SEARCH_KEY, Body=index_bytes, ContentType="application/json", CacheControl="public, max-age=300, must-revalidate", Metadata={"sha256": manifest["indexSha256"]})

    put_json(client, MANIFEST_KEY, manifest)


def rotate_after_id(rows: list[dict], package_id: str | None) -> list[dict]:
    if not rows or not package_id:
        return rows
    cursor = next(
        (index for index, row in enumerate(rows) if str(row.get("id", "")).casefold() == package_id.casefold()),
        None,
    )
    if cursor is None:
        return rows
    start = (cursor + 1) % len(rows)
    return rows[start:] + rows[:start]


def row_matches_prefix(row: dict, scan_prefix: str) -> bool:
    if not scan_prefix:
        return True
    package_id = str(row.get("id", ""))
    first = package_id[:1].casefold()
    return first.isdigit() if scan_prefix == "0-9" else first == scan_prefix


def store_scan_cursor(media_index: dict, kind: str, scan_prefix: str, package_id: str) -> None:
    if scan_prefix:
        cursors = media_index.setdefault("scanCursors", {})
        if not isinstance(cursors, dict):
            cursors = media_index["scanCursors"] = {}
        kind_cursors = cursors.setdefault(kind, {})
        if not isinstance(kind_cursors, dict):
            kind_cursors = cursors[kind] = {}
        kind_cursors[scan_prefix] = package_id
    else:
        media_index["iconCursorId" if kind == "icon" else "screenshotCursorId"] = package_id


def get_scan_cursor(media_index: dict, kind: str, scan_prefix: str) -> str | None:
    if scan_prefix:
        cursors = media_index.get("scanCursors", {})
        kind_cursors = cursors.get(kind, {}) if isinstance(cursors, dict) else {}
        return kind_cursors.get(scan_prefix) if isinstance(kind_cursors, dict) else None
    legacy_key = "iconCursorId" if kind == "icon" else "screenshotCursorId"
    value = media_index.get(legacy_key)
    return str(value) if isinstance(value, str) else None


def parse_manual_urls(value: str, parameter_name: str) -> list[str]:
    """Accept a URL, whitespace-separated URLs, or a JSON array from CI inputs."""
    value = value.strip()
    if not value:
        return []
    if value.startswith("["):
        try:
            parsed = json.loads(value)
        except json.JSONDecodeError as exc:
            raise ValueError(f"{parameter_name} precisa ser uma lista JSON válida ou URLs separadas por espaços: {exc}") from exc
        if not isinstance(parsed, list) or any(not isinstance(url, str) for url in parsed):
            raise ValueError(f"{parameter_name} em formato JSON deve conter somente URLs em texto")
        return [url.strip() for url in parsed if url.strip()]
    # CircleCI pode achatar quebras de linha em espaços ao receber parâmetros
    # de pipeline. URLs não podem conter espaços literais; use %20 se necessário.
    return value.split()


def publish_one(client, manifest: dict, index: list[dict], media_index: dict, package_id: str, kind: str, data: bytes, extension: str, source_url: str, name: str = "") -> dict:
    row = next((entry for entry in index if str(entry.get("id", "")).casefold() == package_id.casefold()), None)
    detail_rel = row.get("detailPath") if row else quote(_detail_path(package_id), safe="/")
    if not isinstance(detail_rel, str):
        raise ValueError(f"Não consegui derivar o caminho do app {package_id}.")
    detail_key = "Store/Catalog/" + unquote(detail_rel)
    current_detail_bytes = read_object(client, detail_key)
    if row is not None and current_detail_bytes is None:
        raise ValueError(f"O detalhe publicado para {package_id} está ausente; mídia não foi associada.")
    detail = json.loads(current_detail_bytes) if current_detail_bytes else None
    if detail is not None and (not isinstance(detail, dict) or str(detail.get("id", "")).casefold() != package_id.casefold()):
        raise ValueError(f"O JSON do catálogo não corresponde ao ID {package_id}.")

    normalized_id = package_id.casefold()
    registry = media_index["apps"].setdefault(normalized_id, {"icon": None, "screenshots": []})
    media = normalize_media(registry)
    detail_media = normalize_media(detail.get("media", {})) if detail is not None else normalize_media({})
    if not media["icon"] and detail_media["icon"]:
        media["icon"] = detail_media["icon"]
    if not media["iconSha256"] and detail_media["iconSha256"]:
        media["iconSha256"] = detail_media["iconSha256"]
    media["screenshots"] = list(dict.fromkeys(media["screenshots"] + detail_media["screenshots"]))
    media["screenshotLabels"].update(detail_media["screenshotLabels"])
    media["screenshotSha256"].update(detail_media["screenshotSha256"])
    original_media = normalize_media(registry)
    now = datetime.now(UTC).isoformat().replace("+00:00", "Z")
    label = slug(name or Path(urlparse(source_url).path).stem or kind)
    digest = hashlib.sha256(data).hexdigest()
    old_object_keys_to_delete: list[str] = []
    upload_asset = True
    if kind == "icon":
        with Image.open(io.BytesIO(data)) as image:
            converted = io.BytesIO()
            image.convert("RGBA").save(converted, format="PNG", optimize=True)
        data = converted.getvalue()
        extension = "png"
        digest = hashlib.sha256(data).hexdigest()
        filename = icon_filename(package_id, "png")
        relative = f"media/{filename}"
        old_icon_path = media["icon"]
        if old_icon_path and old_icon_path != relative and old_icon_path.startswith("media/"):
            old_object_keys_to_delete.append(media_key(package_id, "icon", old_icon_path.rsplit("/", 1)[-1]))
        media["icon"] = relative
    else:
        old_paths = media["screenshots"]
        labels = media["screenshotLabels"]
        matching_number = labels.get(label.casefold())
        matching_path = next(
            (path for path in old_paths if (slot := screenshot_slot(path)) and slot[0] == matching_number),
            None,
        ) if matching_number else None
        if matching_path:
            # Keep the physical filename independent from image origin; a
            # normalized label-to-slot map lets an intentional resend replace it.
            number = matching_number
            filename = screenshot_filename(package_id, number, extension)
            relative = f"media/screenshots/{filename}"
            existing_key = media_key(package_id, "screenshot", matching_path.rsplit("/", 1)[-1])
            if object_sha256(client, existing_key) == digest:
                upload_asset = False
            if matching_path != relative:
                old_object_keys_to_delete.append(existing_key)
                upload_asset = True
            media["screenshots"] = list(dict.fromkeys(relative if path == matching_path else path for path in old_paths))
        else:
            # Avoid another entry if the same screenshot is already associated
            # under a different source name for this app.
            for existing_path in old_paths:
                if not existing_path.startswith("media/screenshots/"):
                    continue
                existing_key = media_key(package_id, "screenshot", existing_path.rsplit("/", 1)[-1])
                if object_sha256(client, existing_key) == digest:
                    filename = existing_path.rsplit("/", 1)[-1]
                    relative = existing_path
                    upload_asset = False
                    break
            else:
                next_number = max((slot[0] for path in old_paths if (slot := screenshot_slot(path))), default=0) + 1
                filename = screenshot_filename(package_id, next_number, extension)
                relative = f"media/screenshots/{filename}"
                old_paths.append(relative)
            labels[label.casefold()] = next((slot[0] for path in media["screenshots"] if (slot := screenshot_slot(path)) and path == relative), 0)
        media["screenshots"] = old_paths
        if matching_path and matching_path != relative:
            media["screenshotSha256"].pop(matching_path, None)
        media["screenshotSha256"][relative] = digest

    object_key = media_key(package_id, kind, filename)
    if kind == "icon":
        existing_digest = object_sha256(client, object_key)
        media["iconSha256"] = digest
        upload_asset = existing_digest != digest
    # Keep stable normalized object paths. Catalog URLs carry the content hash
    # separately so an intentional replacement bypasses old immutable caches.
    if upload_asset:
        client.put_object(Bucket=BUCKET, Key=object_key, Body=data, ContentType=CONTENT_TYPES[extension], CacheControl="public, max-age=31536000, immutable", Metadata={"sha256": digest})
    detail_bytes = None
    detail_changed = False
    if detail is not None:
        detail["media"] = media
        if kind == "screenshot":
            detail["screenshotUrls"] = screenshot_public_urls(client, detail_rel, media["screenshots"])
        detail_bytes = json.dumps(detail, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        detail_changed = detail_bytes != current_detail_bytes
        if detail_changed:
            client.put_object(Bucket=BUCKET, Key=detail_key, Body=detail_bytes, ContentType="application/json", CacheControl="public, max-age=300, must-revalidate", Metadata={"sha256": hashlib.sha256(detail_bytes).hexdigest()})

    for old_object_key in old_object_keys_to_delete:
        try:
            client.delete_object(Bucket=BUCKET, Key=old_object_key)
        except Exception as exc:
            print(f"Aviso: não foi possível remover a imagem substituída {old_object_key}: {exc}", file=sys.stderr)

    media_index["generatedUtc"] = now
    media_index["apps"][normalized_id] = media
    row_changed = False
    if row is not None:
        row_changed = row.get("media") != media
        row["media"] = media
        if kind == "screenshot":
            row_changed = row_changed or row.get("screenshotUrls") != detail["screenshotUrls"]
            row["screenshotUrls"] = detail["screenshotUrls"]
        record_digest = hashlib.sha256(detail_bytes or b"").hexdigest()
        row_changed = row_changed or row.get("recordSha256") != record_digest
        row["recordSha256"] = record_digest
    if row is not None:
        status = "published" if upload_asset or detail_changed or row_changed else "unchanged"
    else:
        status = "queued-until-catalog-sync" if upload_asset or media != original_media else "unchanged"
    return {"id": package_id, "type": kind, "path": object_key.removeprefix("Store/Catalog/"), "sha256": digest, "status": status}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("manual", "auto"), required=True)
    parser.add_argument("--type", choices=("icon", "screenshot"), required=True)
    parser.add_argument("--package-id", default="")
    parser.add_argument("--url", default="", help="URL direta da imagem (modo manual)")
    parser.add_argument("--urls-text", default="", help="URLs diretas adicionais por linha ou em array JSON (modo manual)")
    parser.add_argument("--homepage-url", default="", help="homepage a percorrer (modo automático screenshot)")
    parser.add_argument("--scan-prefix", default="", help="prefixo inicial dos IDs no modo automático: 0-9 ou uma letra a-z")
    parser.add_argument("--batch-size", type=int, default=100)
    parser.add_argument("--publish-batch-size", type=int, default=200, help="apps por lote publicado no modo automático de ícones")
    parser.add_argument("--out", type=Path, default=Path("catalog-media-report.json"))
    parser.add_argument("--dry-run", action="store_true", help="resolve and validate assets without publishing")
    args = parser.parse_args()
    args.scan_prefix = args.scan_prefix.strip().casefold()
    if args.scan_prefix and args.scan_prefix != "0-9" and not re.fullmatch(r"[a-z]", args.scan_prefix):
        parser.error("scan-prefix precisa ser 0-9 ou uma letra de a-z")
    if args.mode == "manual" and args.scan_prefix:
        parser.error("scan-prefix só pode ser usado no modo automático")
    if args.publish_batch_size < 1:
        parser.error("publish-batch-size precisa ser positivo")
    try:
        urls_from_url = parse_manual_urls(args.url, "media_url")
        urls_from_text = parse_manual_urls(args.urls_text, "media_urls")
    except ValueError as exc:
        parser.error(str(exc))
    manual_urls = list(dict.fromkeys(urls_from_url + urls_from_text))
    if args.mode == "manual" and (not args.package_id.strip() or not manual_urls):
        parser.error("modo manual exige --package-id e pelo menos uma URL em --url ou --urls-text")
    if args.mode == "manual" and args.type == "icon" and len(manual_urls) > 1:
        parser.error("modo manual de ícone aceita uma URL; use a captura automática para processar vários ícones")
    if args.mode == "auto" and args.batch_size < 1:
        parser.error("batch-size precisa ser positivo")
    if args.mode == "auto" and args.type == "icon" and args.package_id:
        parser.error("modo automático de ícones processa a cobertura; não aceite um ID manual")

    client = r2_client()
    manifest, index, media_index = load_catalog(client)
    jobs = []
    selected_count = 0
    next_cursor_id = None
    auto_icons_checkpointed = False
    results = []
    if args.mode == "manual":
        for url in manual_urls:
            try:
                body, extension, source = download_image(url)
                extension = validate_image(body, args.type)
                jobs.append((args.package_id.strip(), body, extension, source, ""))
            except Exception as exc:
                results.append({
                    "id": args.package_id.strip(),
                    "type": args.type,
                    "url": url,
                    "status": "error",
                    "error": f"{type(exc).__name__}: {exc}",
                })
    elif args.type == "screenshot":
        requested_homepage = args.homepage_url.strip()
        catalog_rows = [row for row in index if isinstance(row, dict) and row.get("id")]
        if args.scan_prefix:
            catalog_rows = [row for row in catalog_rows if row_matches_prefix(row, args.scan_prefix)]
        if args.package_id:
            candidates = [row for row in catalog_rows if str(row["id"]).casefold() == args.package_id.casefold() and (row.get("homepage") or requested_homepage)]
        elif requested_homepage:
            candidates = [row for row in catalog_rows if str(row.get("homepage", "")).casefold() == requested_homepage.casefold()]
        else:
            rotated_rows = rotate_after_id(catalog_rows, get_scan_cursor(media_index, args.type, args.scan_prefix))
            candidates = [
                row for row in rotated_rows
                if row.get("homepage") and not normalize_media(row.get("media")).get("screenshots")
            ][:args.batch_size]
            selected_count = len(candidates)
            if candidates:
                next_cursor_id = str(candidates[-1]["id"])
        for row in candidates:
            try:
                page_url = requested_homepage or str(row.get("homepage") or "")
                body, extension, source = download_image(page_url, homepage=True)
                jobs.append((str(row["id"]), body, extension, source, str(row.get("name") or "screenshot")))
            except Exception as exc:  # Per-app automatic failures are isolated.
                print(f"{row.get('id')}: {type(exc).__name__}: {exc}", file=sys.stderr)
                status = "not-found" if isinstance(exc, ValueError) and "Nenhuma imagem identificada" in str(exc) else "error"
                results.append({
                    "id": str(row["id"]),
                    "type": args.type,
                    "status": status,
                    "error": f"{type(exc).__name__}: {exc}",
                })
    else:
        catalog_rows = [row for row in index if isinstance(row, dict) and row.get("id")]
        if args.scan_prefix:
            catalog_rows = [row for row in catalog_rows if row_matches_prefix(row, args.scan_prefix)]
        rotated_rows = rotate_after_id(catalog_rows, get_scan_cursor(media_index, args.type, args.scan_prefix))
        pending = [
            str(row["id"]) for row in rotated_rows
            if not normalize_media(row.get("media")).get("icon")
        ][:args.batch_size]
        selected_count = len(pending)
        if pending:
            next_cursor_id = pending[-1]
            out = Path("catalog-icon-cdn-cache")
            out.mkdir(parents=True, exist_ok=True)
            print("Ícones: baixando o índice atualizado da CDN do WinGet...", flush=True)
            db_path = icon_probe.fetch_index(f"{icon_probe.DEFAULT_CDN}/source2.msix", out)
            try:
                packages = icon_probe.read_packages(db_path, out / "schema.txt")
            finally:
                db_path.unlink(missing_ok=True)
            print(f"Ícones: índice carregado; {len(packages):,} pacotes disponíveis na CDN.", flush=True)
        else:
            packages = []
        package_map = {icon_sync.norm_id(item["id"]): item for item in packages}
        processed = 0
        for offset in range(0, len(pending), args.publish_batch_size):
            batch_ids = pending[offset : offset + args.publish_batch_size]
            batch_jobs = []
            for package_id in batch_ids:
                processed += 1
                package = package_map.get(icon_sync.norm_id(package_id))
                if not package:
                    results.append({"id": package_id, "type": args.type, "status": "not-found", "error": "ID não encontrado no índice da CDN de ícones."})
                else:
                    try:
                        result = icon_sync.resolve_icon(package, icon_probe.DEFAULT_CDN)
                        if result.get("status") == "ok":
                            batch_jobs.append((package_id, result["data"], result["ext"], result["url"], "cdn"))
                        else:
                            results.append({"id": package_id, "type": args.type, "status": "not-found", "error": str(result.get("status", "ícone indisponível na CDN"))})
                    except Exception as exc:
                        results.append({"id": package_id, "type": args.type, "status": "error", "error": f"{type(exc).__name__}: {exc}"})

                if processed % 25 == 0 or processed == selected_count:
                    prefix_label = "0-9" if package_id[:1].isdigit() else package_id[:1].casefold()
                    print(f"Ícones: prefixo {prefix_label}; {processed:,}/{selected_count:,} apps analisados; último ID: {package_id}", flush=True)

            for position, (package_id, body, extension, source, label) in enumerate(batch_jobs, start=1):
                try:
                    if args.dry_run:
                        results.append({"id": package_id, "type": args.type, "status": "dry-run", "sourceUrl": source, "sha256": hashlib.sha256(body).hexdigest()})
                    else:
                        results.append(publish_one(client, manifest, index, media_index, package_id, args.type, body, extension, source, label))
                except Exception as exc:
                    results.append({"id": package_id, "type": args.type, "status": "error", "error": f"{type(exc).__name__}: {exc}"})
                if position % 25 == 0 or position == len(batch_jobs):
                    print(f"Ícones: lote {offset // args.publish_batch_size + 1}; {position:,}/{len(batch_jobs):,} imagens publicadas", flush=True)

            if not args.dry_run:
                store_scan_cursor(media_index, args.type, args.scan_prefix, batch_ids[-1])
                batch_published = any(
                    result.get("id") in batch_ids and result.get("status") == "published"
                    for result in results
                )
                publish_catalog_checkpoint(client, manifest, index, media_index, update_search_index=batch_published)
                prefix_label = "0-9" if batch_ids[-1][:1].isdigit() else batch_ids[-1][:1].casefold()
                print(f"Ícones: lote {offset // args.publish_batch_size + 1} confirmado; prefixo {prefix_label}, cursor {batch_ids[-1]}", flush=True)
        auto_icons_checkpointed = not args.dry_run

    for package_id, body, extension, source, label in jobs:
        try:
            if args.dry_run:
                results.append({"id": package_id, "type": args.type, "status": "dry-run", "sourceUrl": source, "sha256": hashlib.sha256(body).hexdigest()})
            else:
                results.append(publish_one(client, manifest, index, media_index, package_id, args.type, body, extension, source, label))
        except Exception as exc:
            results.append({"id": package_id, "type": args.type, "status": "error", "error": f"{type(exc).__name__}: {exc}"})
    media_changed = any(item.get("status") in {"published", "queued-until-catalog-sync"} for item in results)
    index_changed = any(item.get("status") == "published" for item in results)
    if auto_icons_checkpointed:
        media_changed = False
        index_changed = False
    for item in results:
        if item.get("status") == "error":
            print(json.dumps(item, ensure_ascii=False), file=sys.stderr)
    if not args.dry_run:
        if args.mode == "auto" and selected_count and not auto_icons_checkpointed:
            store_scan_cursor(media_index, args.type, args.scan_prefix, next_cursor_id)
            media_changed = True
        if media_changed:
            media_index["generatedUtc"] = datetime.now(UTC).isoformat().replace("+00:00", "Z")
            media_index_bytes = put_json(client, MEDIA_INDEX_KEY, media_index)
            manifest["mediaIndexSha256"] = hashlib.sha256(media_index_bytes).hexdigest()
    if index_changed and not args.dry_run:
        index_bytes = json.dumps(index, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        # catalogSha256 é reconstituível pelos hashes dos detalhes no índice, sem baixar 15 mil arquivos.
        digest_builder = hashlib.sha256()
        for entry in index:
            raw_path = unquote(str(entry["detailPath"]))
            digest_builder.update(raw_path.encode("utf-8"))
            digest_builder.update(b"\0")
            digest_builder.update(bytes.fromhex(entry["recordSha256"]))
        digest_builder.update(hashlib.sha256(index_bytes).digest())
        manifest["indexSha256"] = hashlib.sha256(index_bytes).hexdigest()
        manifest["catalogSha256"] = digest_builder.hexdigest()
        manifest["generatedUtc"] = datetime.now(UTC).isoformat().replace("+00:00", "Z")
        client.put_object(Bucket=BUCKET, Key=SEARCH_KEY, Body=index_bytes, ContentType="application/json", CacheControl="public, max-age=300, must-revalidate", Metadata={"sha256": manifest["indexSha256"]})
    if media_changed and not args.dry_run:
        put_json(client, MANIFEST_KEY, manifest)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps({"mode": args.mode, "type": args.type, "processed": len(results), "results": results}, ensure_ascii=False, indent=2), encoding="utf-8")
    print(
        f"Mídia: {len(results)} processada(s); "
        f"{sum(item['status'] == 'published' for item in results)} publicada(s); "
        f"{sum(item['status'] == 'queued-until-catalog-sync' for item in results)} pendente(s); "
        f"{sum(item['status'] == 'unchanged' for item in results)} sem alteração; "
        f"{sum(item['status'] == 'not-found' for item in results)} sem correspondência."
    )
    return 1 if any(item["status"] == "error" for item in results) else 0


if __name__ == "__main__":
    raise SystemExit(main())
