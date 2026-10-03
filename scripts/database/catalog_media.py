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
    screenshots = media.get("screenshots", [])
    if not isinstance(screenshots, list):
        screenshots = []
    screenshots = list(dict.fromkeys(path for path in screenshots if isinstance(path, str)))
    return {"icon": icon, "screenshots": screenshots}


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
    now = datetime.now(UTC).isoformat().replace("+00:00", "Z")
    label = slug(name or Path(urlparse(source_url).path).stem or kind)
    if kind == "icon":
        filename = f"icon.{extension}"
        relative = f"media/{filename}"
        media["icon"] = relative
    else:
        old_paths = media["screenshots"]
        next_number = max((int(match.group(1)) for path in old_paths if (match := re.match(r"media/screenshots/(\d+)-", path))), default=0) + 1
        filename = f"{next_number:02d}-{label}.{extension}"
        relative = f"media/screenshots/{filename}"
        if relative not in old_paths:
            old_paths.append(relative)
        media["screenshots"] = old_paths

    object_key = media_key(package_id, kind, filename)
    digest = hashlib.sha256(data).hexdigest()
    if kind == "icon":
        with Image.open(io.BytesIO(data)) as image:
            converted = io.BytesIO()
            image.convert("RGBA").save(converted, format="PNG", optimize=True)
        data = converted.getvalue()
        extension = "png"
        digest = hashlib.sha256(data).hexdigest()
    # Publish bytes first. They are immutable by normalized name unless explicitly replaced.
    client.put_object(Bucket=BUCKET, Key=object_key, Body=data, ContentType=CONTENT_TYPES[extension], CacheControl="public, max-age=31536000, immutable", Metadata={"sha256": digest})
    detail_bytes = None
    if detail is not None:
        detail["media"] = media
        if kind == "screenshot":
            detail["screenshotUrls"] = [f"{PUBLIC_BASE}/{quote('apps/' + unquote(detail_rel).removeprefix('apps/').rsplit('/', 1)[0] + '/' + path, safe='/')}" for path in media["screenshots"]]
        detail_bytes = json.dumps(detail, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        client.put_object(Bucket=BUCKET, Key=detail_key, Body=detail_bytes, ContentType="application/json", CacheControl="public, max-age=300, must-revalidate", Metadata={"sha256": hashlib.sha256(detail_bytes).hexdigest()})

    media_index["generatedUtc"] = now
    media_index["apps"][normalized_id] = media
    if row is not None:
        row["media"] = media
        if kind == "screenshot":
            row["screenshotUrls"] = detail["screenshotUrls"]
        row["recordSha256"] = hashlib.sha256(detail_bytes or b"").hexdigest()
    return {"id": package_id, "type": kind, "path": object_key.removeprefix("Store/Catalog/"), "sha256": digest, "status": "published" if row else "queued-until-catalog-sync"}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("manual", "auto"), required=True)
    parser.add_argument("--type", choices=("icon", "screenshot"), required=True)
    parser.add_argument("--package-id", default="")
    parser.add_argument("--url", default="", help="URL direta da imagem (modo manual)")
    parser.add_argument("--urls-text", default="", help="URLs diretas adicionais por linha ou em array JSON (modo manual)")
    parser.add_argument("--homepage-url", default="", help="homepage a percorrer (modo automático screenshot)")
    parser.add_argument("--batch-size", type=int, default=100)
    parser.add_argument("--out", type=Path, default=Path("catalog-media-report.json"))
    parser.add_argument("--dry-run", action="store_true", help="resolve and validate assets without publishing")
    args = parser.parse_args()
    urls_text = args.urls_text.strip()
    if urls_text.startswith("["):
        try:
            parsed_urls = json.loads(urls_text)
        except json.JSONDecodeError as exc:
            parser.error(f"media_urls precisa ser uma lista JSON válida ou URLs separadas por linha: {exc}")
        if not isinstance(parsed_urls, list) or any(not isinstance(url, str) for url in parsed_urls):
            parser.error("a lista JSON de media_urls deve conter somente URLs em texto")
        urls_from_text = parsed_urls
    else:
        urls_from_text = urls_text.splitlines()
    manual_urls = list(dict.fromkeys(
        url.strip()
        for url in ([args.url] + urls_from_text)
        if url.strip()
    ))
    if args.mode == "manual" and (not args.package_id.strip() or not manual_urls):
        parser.error("modo manual exige --package-id e pelo menos uma URL em --url ou --urls-text")
    if args.mode == "manual" and args.type == "icon" and len(manual_urls) > 1:
        parser.error("modo manual de ícone aceita uma URL; use a captura automática para processar vários ícones")
    if args.mode == "auto" and args.type == "screenshot" and not args.homepage_url and args.batch_size < 1:
        parser.error("batch-size precisa ser positivo")
    if args.mode == "auto" and args.type == "icon" and args.package_id:
        parser.error("modo automático de ícones processa a cobertura; não aceite um ID manual")

    client = r2_client()
    manifest, index, media_index = load_catalog(client)
    jobs = []
    scan_cursor_key = "iconCursor" if args.type == "icon" else "screenshotCursor"
    selected_count = 0
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
        candidates = [
            row
            for row in index
            if isinstance(row, dict)
            and row.get("id")
            and (row.get("homepage") or requested_homepage)
            and (args.package_id or requested_homepage or not normalize_media(row.get("media")).get("screenshots"))
        ]
        if args.package_id:
            candidates = [row for row in candidates if str(row["id"]).casefold() == args.package_id.casefold()]
        elif requested_homepage:
            candidates = [row for row in candidates if str(row.get("homepage", "")).casefold() == requested_homepage.casefold()]
        else:
            cursor = int(media_index.get(scan_cursor_key, 0)) % max(1, len(candidates))
            candidates = candidates[cursor:] + candidates[:cursor]
            selected_count = min(args.batch_size, len(candidates))
            candidates = candidates[:selected_count]
        for row in candidates:
            try:
                page_url = requested_homepage or str(row.get("homepage") or "")
                body, extension, source = download_image(page_url, homepage=True)
                jobs.append((str(row["id"]), body, extension, source, str(row.get("name") or "screenshot")))
            except Exception as exc:  # Per-app automatic failures are isolated.
                print(f"{row.get('id')}: {type(exc).__name__}: {exc}", file=sys.stderr)
    else:
        pending = [str(row["id"]) for row in index if isinstance(row, dict) and row.get("id") and not normalize_media(row.get("media")).get("icon")]
        out = Path("catalog-icon-cdn-cache")
        out.mkdir(parents=True, exist_ok=True)
        db_path = icon_probe.fetch_index(f"{icon_probe.DEFAULT_CDN}/source2.msix", out)
        try:
            packages = icon_probe.read_packages(db_path, out / "schema.txt")
        finally:
            db_path.unlink(missing_ok=True)
        package_map = {icon_sync.norm_id(item["id"]): item for item in packages}
        cursor = int(media_index.get(scan_cursor_key, 0)) % max(1, len(pending))
        pending = pending[cursor:] + pending[:cursor]
        selected_count = min(args.batch_size, len(pending))
        pending = pending[:selected_count]
        for package_id in pending:
            package = package_map.get(icon_sync.norm_id(package_id))
            if not package:
                continue
            result = icon_sync.resolve_icon(package, icon_probe.DEFAULT_CDN)
            if result.get("status") == "ok":
                jobs.append((package_id, result["data"], result["ext"], result["url"], "cdn"))

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
    for item in results:
        if item.get("status") == "error":
            print(json.dumps(item, ensure_ascii=False), file=sys.stderr)
    if not args.dry_run:
        if args.mode == "auto" and selected_count:
            media_index[scan_cursor_key] = int(media_index.get(scan_cursor_key, 0)) + selected_count
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
    print(f"Mídia: {len(results)} processada(s); {sum(item['status'] == 'published' for item in results)} publicada(s); {sum(item['status'] == 'queued-until-catalog-sync' for item in results)} pendente(s).")
    return 1 if any(item["status"] == "error" for item in results) else 0


if __name__ == "__main__":
    raise SystemExit(main())
