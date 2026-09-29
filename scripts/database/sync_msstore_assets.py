"""Copy Microsoft Store catalog images into R2 and rewrite the catalog URLs.

The source URL hash is the cache key, so unchanged images are not fetched again.
Objects are content-addressed and immutable; the app only receives R2 URLs for
assets that were successfully downloaded and validated as image files.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import threading
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlparse

import boto3
import requests
from botocore.config import Config
from botocore.exceptions import BotoCoreError
from PIL import Image
from requests.adapters import HTTPAdapter
from urllib3.util.retry import Retry

MAX_IMAGE_BYTES = 20 * 1024 * 1024
IMAGE_FIELDS = ("storeIconUrl", "storeBannerUrl")


def source_is_allowed(url: str) -> bool:
    parsed = urlparse(url)
    host = (parsed.hostname or "").lower().rstrip(".")
    allowed = (
        host == "microsoft.com"
        or host.endswith(".microsoft.com")
        or host == "s-microsoft.com"
        or host.endswith(".s-microsoft.com")
        or host == "akamaized.net"
        or host.endswith(".akamaized.net")
    )
    return parsed.scheme == "https" and allowed


def load_map(path: Path) -> dict[str, dict[str, str]]:
    if not path.is_file():
        return {}
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        assets = data.get("assets", {}) if isinstance(data, dict) else {}
        return assets if isinstance(assets, dict) else {}
    except (OSError, json.JSONDecodeError):
        print("Aviso: mapa de assets inválido; será recriado.", file=sys.stderr)
        return {}


def read_image(response: requests.Response) -> tuple[bytes, str]:
    content_type = response.headers.get("Content-Type", "").split(";", 1)[0].lower()
    if not content_type.startswith("image/"):
        raise ValueError(f"content-type inesperado: {content_type or 'ausente'}")
    length = response.headers.get("Content-Length")
    if length and int(length) > MAX_IMAGE_BYTES:
        raise ValueError("imagem excede 20 MB")
    body = bytearray()
    for chunk in response.iter_content(64 * 1024):
        body.extend(chunk)
        if len(body) > MAX_IMAGE_BYTES:
            raise ValueError("imagem excede 20 MB")
    if not body:
        raise ValueError("imagem vazia")
    from io import BytesIO

    try:
        with Image.open(BytesIO(body)) as image:
            image.verify()
            image_format = image.format
    except (OSError, Image.DecompressionBombError) as exc:
        raise ValueError(f"imagem inválida ou insegura: {exc}") from exc
    extensions = {
        "PNG": "png",
        "JPEG": "jpg",
        "WEBP": "webp",
        "GIF": "gif",
        "BMP": "bmp",
        "TIFF": "tif",
        "ICO": "ico",
    }
    if image_format not in extensions:
        raise ValueError(f"formato de imagem não suportado: {image_format}")
    return bytes(body), extensions[image_format]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("catalog", type=Path)
    parser.add_argument("--map", dest="map_path", type=Path, required=True)
    parser.add_argument("--previous", type=Path, help="catálogo anterior para conservar imagens se uma URL falhar")
    parser.add_argument("--workers", type=int, default=8)
    parser.add_argument("--max-failure-rate", type=float, default=0.25)
    args = parser.parse_args()

    if not args.catalog.is_file():
        parser.error(f"catálogo não encontrado: {args.catalog}")
    if args.workers < 1 or not 0 <= args.max_failure_rate <= 1:
        parser.error("--workers precisa ser positivo e --max-failure-rate deve ficar entre 0 e 1")
    public_base = os.environ.get("R2_PUBLIC_BASE", "").rstrip("/")
    if not public_base.startswith("https://"):
        parser.error("defina R2_PUBLIC_BASE com a URL HTTPS pública do bucket")

    account_id = os.environ["R2_ACCOUNT_ID"]
    access_key = os.environ["R2_ACCESS_KEY_ID"]
    secret_key = os.environ["R2_SECRET_ACCESS_KEY"]
    bucket = os.environ.get("R2_BUCKET") or "winprovision"
    client = boto3.client(
        "s3",
        endpoint_url=f"https://{account_id}.r2.cloudflarestorage.com",
        aws_access_key_id=access_key,
        aws_secret_access_key=secret_key,
        config=Config(signature_version="s3v4", retries={"max_attempts": 4, "mode": "standard"}),
        region_name="auto",
    )

    catalog = json.loads(args.catalog.read_text(encoding="utf-8"))
    if not isinstance(catalog, list):
        parser.error("a raiz do catálogo precisa ser uma lista JSON")
    assets = load_map(args.map_path)
    downloaded = reused = failed = 0
    url_results: dict[str, str | None] = {}
    counter_lock = threading.Lock()
    session_local = threading.local()

    previous_by_id: dict[str, dict] = {}
    if args.previous and args.previous.is_file():
        try:
            previous_catalog = json.loads(args.previous.read_text(encoding="utf-8"))
            if isinstance(previous_catalog, list):
                previous_by_id = {
                    str(item.get("id", "")).casefold(): item
                    for item in previous_catalog
                    if isinstance(item, dict) and item.get("id")
                }
        except (OSError, json.JSONDecodeError) as exc:
            print(f"Aviso: catálogo anterior de imagens inválido: {exc}", file=sys.stderr)

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

    def get_session() -> requests.Session:
        session = getattr(session_local, "session", None)
        if session is None:
            session = requests.Session()
            session.headers.update({"User-Agent": "WinProvision-Store-Asset-Sync/1.0"})
            adapter = HTTPAdapter(max_retries=retry)
            session.mount("https://", adapter)
            session_local.session = session
        return session

    def resolve(source_url: str | None) -> str | None:
        nonlocal downloaded, reused, failed
        if not source_url:
            return None
        if source_url in url_results:
            return url_results[source_url]
        if not source_is_allowed(source_url):
            print(f"Aviso: URL de imagem fora dos domínios permitidos: {source_url}", file=sys.stderr)
            url_results[source_url] = None
            with counter_lock:
                failed += 1
            return None
        source_key = hashlib.sha256(source_url.encode("utf-8")).hexdigest()
        cached = assets.get(source_key)
        if isinstance(cached, dict) and cached.get("sourceUrl") == source_url and cached.get("publicUrl"):
            url_results[source_url] = cached["publicUrl"]
            with counter_lock:
                reused += 1
            return cached["publicUrl"]

        try:
            with get_session().get(source_url, timeout=(10, 30), stream=True) as response:
                response.raise_for_status()
                if not source_is_allowed(response.url):
                    raise ValueError("redirecionamento para domínio não permitido")
                body, extension = read_image(response)
            content_hash = hashlib.sha256(body).hexdigest()
            object_key = f"Store/MsStore/Assets/{content_hash}.{extension}"
            client.put_object(
                Bucket=bucket,
                Key=object_key,
                Body=body,
                ContentType={
                    "ico": "image/x-icon",
                    "jpg": "image/jpeg",
                    "tif": "image/tiff",
                }.get(extension, f"image/{extension}"),
                CacheControl="public, max-age=31536000, immutable",
                Metadata={"sha256": content_hash, "source-url-sha256": source_key},
            )
            public_url = f"{public_base}/{object_key}"
            assets[source_key] = {
                "sourceUrl": source_url,
                "publicUrl": public_url,
                "contentSha256": content_hash,
                "updatedUtc": datetime.now(timezone.utc).isoformat(),
            }
            url_results[source_url] = public_url
            with counter_lock:
                downloaded += 1
            return public_url
        except (requests.RequestException, BotoCoreError, ValueError, OSError) as exc:
            print(f"Aviso: não foi possível armazenar asset {source_url}: {exc}", file=sys.stderr)
            url_results[source_url] = None
            with counter_lock:
                failed += 1
            return None

    # Cada URL distinta é resolvida uma vez; downloads e uploads independentes
    # avançam em paralelo, limitados para não sobrecarregar a CDN nem o R2.
    source_urls = {
        url
        for app in catalog
        if isinstance(app, dict)
        for url in (
            [app.get(field) for field in IMAGE_FIELDS]
            + (app.get("storeScreenshotUrls") if isinstance(app.get("storeScreenshotUrls"), list) else [])
        )
        if isinstance(url, str) and url
    }
    with ThreadPoolExecutor(max_workers=args.workers) as executor:
        list(executor.map(resolve, sorted(source_urls)))

    def keep_previous(old_url: str | None) -> str | None:
        if not isinstance(old_url, str) or not old_url.startswith(f"{public_base}/Store/MsStore/Assets/"):
            return None
        return old_url

    for app in catalog:
        if not isinstance(app, dict):
            continue
        previous = previous_by_id.get(str(app.get("id", "")).casefold(), {})
        for field in IMAGE_FIELDS:
            source_url = app.get(field)
            resolved = url_results.get(source_url) if isinstance(source_url, str) else None
            app[field] = resolved or keep_previous(previous.get(field))
        screenshots = app.get("storeScreenshotUrls")
        previous_screenshots = previous.get("storeScreenshotUrls")
        if isinstance(screenshots, list):
            prior = previous_screenshots if isinstance(previous_screenshots, list) else []
            if not screenshots and prior:
                app["storeScreenshotUrls"] = [url for prior_url in prior if (url := keep_previous(prior_url))]
                continue
            app["storeScreenshotUrls"] = [
                resolved or keep_previous(prior[index] if index < len(prior) else None)
                for index, url in enumerate(screenshots)
                if (resolved := url_results.get(url) if isinstance(url, str) else None)
                or (index < len(prior) and keep_previous(prior[index]))
            ]
        elif isinstance(previous_screenshots, list):
            app["storeScreenshotUrls"] = [url for prior_url in previous_screenshots if (url := keep_previous(prior_url))]

    attempted = len(source_urls) - reused
    failure_rate = failed / attempted if attempted else 0.0
    print(
        f"Assets Store: {downloaded} novos, {reused} reutilizados, {failed} sem cópia "
        f"({failure_rate:.1%} das URLs consultadas); {len(assets)} mapeamentos no cache."
    )
    if failure_rate > args.max_failure_rate:
        print(
            f"Falhas acima do limite de {args.max_failure_rate:.0%}; catálogo e mapa não serão atualizados.",
            file=sys.stderr,
        )
        return 1

    args.catalog.write_text(json.dumps(catalog, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    args.map_path.parent.mkdir(parents=True, exist_ok=True)
    args.map_path.write_text(
        json.dumps({"schemaVersion": 1, "assets": assets}, ensure_ascii=False, separators=(",", ":")),
        encoding="utf-8",
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
