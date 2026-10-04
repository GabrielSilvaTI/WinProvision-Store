#!/usr/bin/env python3
"""Store validated Microsoft Store media beside each app in the R2 catalog."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import re
import sys
import threading
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import UTC, datetime
from pathlib import Path
from urllib.parse import urlparse

import boto3
import requests
from botocore.config import Config
from botocore.exceptions import BotoCoreError
from catalog_media_naming import banner_filename, icon_filename, screenshot_filename
from PIL import Image, UnidentifiedImageError
from requests.adapters import HTTPAdapter
from urllib3.util.retry import Retry

MAX_IMAGE_BYTES = 20 * 1024 * 1024
BASE_KEY = "Store/Catalog/msstore/apps"
CONTENT_TYPES = {
    "png": "image/png",
    "jpg": "image/jpeg",
    "webp": "image/webp",
    "gif": "image/gif",
    "bmp": "image/bmp",
    "tif": "image/tiff",
    "ico": "image/x-icon",
}


def allowed(url: str) -> bool:
    parsed = urlparse(url)
    host = (parsed.hostname or "").casefold().rstrip(".")
    return parsed.scheme == "https" and (
        host == "microsoft.com"
        or host.endswith(".microsoft.com")
        or host == "s-microsoft.com"
        or host.endswith(".s-microsoft.com")
        or host == "akamaized.net"
        or host.endswith(".akamaized.net")
    )


def app_folder(package_id: str) -> tuple[str, str]:
    folder = re.sub(r"[^a-z0-9._-]+", "-", package_id.casefold()).strip(".-_")
    if not folder or folder in {".", ".."} or "/" in folder or "\\" in folder:
        raise ValueError(f"ID MS Store inválido: {package_id!r}")
    first = folder[0]
    return ("0-9" if first.isdigit() else first if "a" <= first <= "z" else "_", folder)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("catalog", type=Path)
    parser.add_argument("--map", dest="map_path", type=Path, required=True)
    parser.add_argument("--previous", type=Path)
    parser.add_argument("--workers", type=int, default=8)
    parser.add_argument("--max-failure-rate", type=float, default=0.25)
    args = parser.parse_args()
    if args.workers < 1 or not 0 <= args.max_failure_rate <= 1:
        parser.error("workers precisa ser positivo e max-failure-rate deve ficar entre 0 e 1")
    public_base = os.environ.get("R2_PUBLIC_BASE", "").rstrip("/")
    if not public_base.startswith("https://"):
        parser.error("defina R2_PUBLIC_BASE com a URL HTTPS pública do bucket")
    catalog = json.loads(args.catalog.read_text(encoding="utf-8-sig"))
    if not isinstance(catalog, list) or not catalog:
        parser.error("catálogo precisa ser uma lista não vazia")
    previous = {}
    if args.previous and args.previous.is_file():
        try:
            previous = {
                str(row["id"]).casefold(): row
                for row in json.loads(args.previous.read_text(encoding="utf-8-sig"))
                if isinstance(row, dict) and row.get("id")
            }
        except (OSError, json.JSONDecodeError, TypeError):
            print(
                "Aviso: catálogo anterior não pôde ser lido; mídia será reconstruída pelas URLs de origem.",
                file=sys.stderr,
            )
    try:
        map_doc = json.loads(args.map_path.read_text(encoding="utf-8-sig")) if args.map_path.is_file() else {}
    except (OSError, json.JSONDecodeError):
        map_doc = {}
    cache = map_doc.get("assets", {}) if isinstance(map_doc, dict) else {}
    if not isinstance(cache, dict):
        cache = {}

    s3 = boto3.client(
        "s3",
        endpoint_url=f"https://{os.environ['R2_ACCOUNT_ID']}.r2.cloudflarestorage.com",
        aws_access_key_id=os.environ["R2_ACCESS_KEY_ID"],
        aws_secret_access_key=os.environ["R2_SECRET_ACCESS_KEY"],
        config=Config(signature_version="s3v4", retries={"max_attempts": 4, "mode": "standard"}),
        region_name="auto",
    )
    bucket = os.environ.get("R2_BUCKET") or "winprovision"
    retry = Retry(
        total=3,
        connect=3,
        read=3,
        status=3,
        backoff_factor=0.5,
        status_forcelist=(429, 500, 502, 503, 504),
        allowed_methods=frozenset({"GET"}),
        respect_retry_after_header=True,
    )
    local = threading.local()

    def session() -> requests.Session:
        value = getattr(local, "session", None)
        if value is None:
            value = requests.Session()
            value.headers["User-Agent"] = "WinProvision-MSStore-Media/1.0"
            value.mount("https://", HTTPAdapter(max_retries=retry))
            local.session = value
        return value

    jobs = []
    for app in catalog:
        if not isinstance(app, dict) or not isinstance(app.get("id"), str):
            parser.error("cada app deve ter id")
        app_folder(app["id"])
        for field, slot, index, url in [
            ("storeIconUrl", "icon", 0, app.get("storeIconUrl")),
            ("storeBannerUrl", "banner", 0, app.get("storeBannerUrl")),
        ] + [
            ("storeScreenshotUrls", "screenshot", i, url)
            for i, url in enumerate(
                app.get("storeScreenshotUrls", []) if isinstance(app.get("storeScreenshotUrls"), list) else []
            )
        ]:
            if isinstance(url, str) and url:
                jobs.append((app["id"], field, slot, index, url))

    results = {}
    failures = []

    def resolve(job):
        package_id, field, slot, index, url = job
        contextual = f"{package_id.casefold()}\0{slot}\0{index}\0{url}"
        cache_key = hashlib.sha256(contextual.encode("utf-8")).hexdigest()
        old = cache.get(cache_key)
        expected_path = None
        if isinstance(old, dict) and isinstance(old.get("path"), str):
            extension = old["path"].rsplit(".", 1)[-1]
            expected_name = (
                icon_filename(package_id, extension)
                if slot == "icon"
                else banner_filename(package_id, extension)
                if slot == "banner"
                else screenshot_filename(package_id, index + 1, extension)
            )
            expected_path = f"media/{expected_name}" if slot != "screenshot" else f"media/screenshots/{expected_name}"
        if isinstance(old, dict) and old.get("sourceUrl") == url and old.get("path") == expected_path:
            return job, old, False
        if not allowed(url):
            raise ValueError(f"URL fora dos domínios permitidos: {url}")
        response = session().get(url, timeout=(10, 30), stream=True)
        try:
            response.raise_for_status()
            if not allowed(response.url):
                raise ValueError("redirecionamento para domínio não permitido")
            content_type = response.headers.get("Content-Type", "").split(";", 1)[0].casefold()
            if not content_type.startswith("image/"):
                raise ValueError(f"Content-Type inválido: {content_type}")
            data = bytearray()
            for chunk in response.iter_content(64 * 1024):
                data.extend(chunk)
                if len(data) > MAX_IMAGE_BYTES:
                    raise ValueError("imagem excede 20 MiB")
        finally:
            response.close()
        try:
            with Image.open(io.BytesIO(data)) as image:
                image.verify()
                fmt, size = image.format, image.size
            if min(size) < 16:
                raise ValueError(f"dimensões inválidas: {size}")
            ext = {
                "PNG": "png",
                "JPEG": "jpg",
                "WEBP": "webp",
                "GIF": "gif",
                "BMP": "bmp",
                "TIFF": "tif",
                "ICO": "ico",
            }.get(fmt)
            if not ext:
                raise ValueError(f"formato inválido: {fmt}")
            if slot in {"icon", "banner"}:
                with Image.open(io.BytesIO(data)) as image:
                    converted = io.BytesIO()
                    image.convert("RGBA" if slot == "icon" else "RGB").save(converted, format="PNG", optimize=True)
                data, ext = bytearray(converted.getvalue()), "png"
        except (OSError, UnidentifiedImageError, Image.DecompressionBombError) as exc:
            raise ValueError(f"imagem inválida: {exc}") from exc
        prefix, folder = app_folder(package_id)
        if slot == "icon":
            relative = f"media/{icon_filename(package_id, ext)}"
        elif slot == "banner":
            relative = f"media/{banner_filename(package_id, ext)}"
        else:
            relative = f"media/screenshots/{screenshot_filename(package_id, index + 1, ext)}"
        object_key = f"{BASE_KEY}/{prefix}/{folder}/{relative}"
        digest = hashlib.sha256(data).hexdigest()
        s3.put_object(
            Bucket=bucket,
            Key=object_key,
            Body=bytes(data),
            ContentType=CONTENT_TYPES[ext],
            CacheControl="public, max-age=31536000, immutable",
            Metadata={"sha256": digest},
        )
        value = {
            "sourceUrl": url,
            "publicUrl": f"{public_base}/{object_key}?v={digest[:16]}",
            "path": relative,
            "contentSha256": digest,
            "updatedUtc": datetime.now(UTC).isoformat(),
        }
        return job, value, True

    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        future_map = {pool.submit(resolve, job): job for job in jobs}
        for future in as_completed(future_map):
            job = future_map[future]
            try:
                key, value, _ = future.result()
                contextual = f"{key[0].casefold()}\0{key[2]}\0{key[3]}\0{key[4]}"
                cache[hashlib.sha256(contextual.encode("utf-8")).hexdigest()] = value
                results[(key[0].casefold(), key[2], key[3])] = value
            except (requests.RequestException, BotoCoreError, ValueError, OSError) as exc:
                failures.append({"id": job[0], "slot": job[2], "url": job[4], "error": f"{type(exc).__name__}: {exc}"})
                print(f"Aviso: {job[0]} {job[2]} não sincronizado: {exc}", file=sys.stderr)

    failure_rate = len(failures) / len(jobs) if jobs else 0.0
    print(f"Assets MS Store: {len(results)} sincronizados; {len(failures)} falharam ({failure_rate:.1%}).")
    if failure_rate > args.max_failure_rate:
        print("Falhas acima do limite; catálogo não será gravado.", file=sys.stderr)
        return 1

    for app in catalog:
        package_key = app["id"].casefold()
        old_app = previous.get(package_key, {})
        old_media = old_app.get("media", {})
        media = {
            "icon": None,
            "iconSha256": None,
            "banner": None,
            "bannerSha256": None,
            "screenshots": [],
            "screenshotSha256": {},
        }
        for field, slot in (("storeIconUrl", "icon"), ("storeBannerUrl", "banner")):
            item = results.get((package_key, slot, 0))
            if item:
                app[field] = item["publicUrl"]
                media[slot] = item["path"]
                if slot == "icon":
                    media["iconSha256"] = item["contentSha256"]
                else:
                    media["bannerSha256"] = item["contentSha256"]
            elif isinstance(old_media, dict) and isinstance(old_media.get(slot), str):
                media[slot] = old_media[slot]
                if slot == "icon":
                    media["iconSha256"] = old_media.get("iconSha256")
                else:
                    media["bannerSha256"] = old_media.get("bannerSha256")
        screenshots = []
        original = app.get("storeScreenshotUrls", [])
        old_paths = old_media.get("screenshots", []) if isinstance(old_media, dict) else []
        old_hashes = old_media.get("screenshotSha256", {}) if isinstance(old_media, dict) else {}
        if not original and isinstance(old_paths, list):
            for old_path in old_paths:
                if isinstance(old_path, str) and old_path.startswith("media/screenshots/"):
                    screenshots.append(old_path)
                    if isinstance(old_hashes, dict) and isinstance(old_hashes.get(old_path), str):
                        media["screenshotSha256"][old_path] = old_hashes[old_path]
        for index, _source in enumerate(original if isinstance(original, list) else []):
            item = results.get((package_key, "screenshot", index))
            if item:
                screenshots.append(item["path"])
                media["screenshotSha256"][item["path"]] = item["contentSha256"]
            else:
                if index < len(old_paths) and isinstance(old_paths[index], str):
                    old_path = old_paths[index]
                    if old_path.startswith("media/screenshots/"):
                        screenshots.append(old_path)
                        if isinstance(old_hashes, dict) and isinstance(old_hashes.get(old_path), str):
                            media["screenshotSha256"][old_path] = old_hashes[old_path]
        app.pop("storeIconUrl", None)
        app.pop("storeBannerUrl", None)
        app.pop("storeScreenshotUrls", None)
        media["screenshots"] = screenshots
        app["media"] = media

    args.catalog.write_text(json.dumps(catalog, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    args.map_path.parent.mkdir(parents=True, exist_ok=True)
    args.map_path.write_text(
        json.dumps({"schemaVersion": 2, "assets": cache}, ensure_ascii=False, separators=(",", ":")), encoding="utf-8"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
