"""Resolve Microsoft 365 offer media and publish verified image objects to R2.

The Office catalog must contain public R2 URLs, never temporary Microsoft Store
URLs. Asset object keys are content-addressed and each published URL is checked
through the public endpoint before the catalog is allowed to be uploaded.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
from datetime import datetime, timezone
from io import BytesIO
from pathlib import Path
from urllib.parse import parse_qsl, urlencode, urlparse, urlsplit, urlunsplit

import boto3
import requests
from botocore.config import Config
from botocore.exceptions import BotoCoreError, ClientError
from PIL import Image
from requests.adapters import HTTPAdapter
from urllib3.util.retry import Retry

DISPLAY_CATALOG_URL = "https://displaycatalog.mp.microsoft.com/v7.0/products"
MAX_IMAGE_BYTES = 20 * 1024 * 1024
SAFE_ID = re.compile(r"^[A-Za-z0-9._-]{2,100}$")
PURPOSE_RANK = {"logo": 0, "tile": 1, "boxart": 2}
FORMAT_EXTENSION = {"PNG": "png", "JPEG": "jpg", "WEBP": "webp", "GIF": "gif", "BMP": "bmp", "TIFF": "tif", "ICO": "ico"}


def safe_source_url(value: str | None) -> str | None:
    if not isinstance(value, str) or not value.strip():
        return None
    url = "https:" + value if value.startswith("//") else value
    parsed = urlparse(url)
    host = (parsed.hostname or "").lower().rstrip(".")
    allowed = (
        host == "microsoft.com" or host.endswith(".microsoft.com")
        or host == "s-microsoft.com" or host.endswith(".s-microsoft.com")
        or host == "akamaized.net" or host.endswith(".akamaized.net")
        or host == "xboxlive.com" or host.endswith(".xboxlive.com")
    )
    return url if parsed.scheme == "https" and allowed else None


def high_resolution_screenshot_url(value: str | None) -> str | None:
    """Ask the Microsoft image CDN for its high-quality 1080p screenshot rendition."""
    url = safe_source_url(value)
    if not url:
        return None
    parts = urlsplit(url)
    query = [(key, item) for key, item in parse_qsl(parts.query, keep_blank_values=True)
             if key.casefold() not in {"q", "w", "h"}]
    query.extend((("q", "97"), ("w", "1920"), ("h", "1080")))
    return urlunsplit((parts.scheme, parts.netloc, parts.path, urlencode(query), parts.fragment))


def load_image_map(path: Path) -> dict[str, dict[str, str]]:
    if not path.is_file():
        return {}
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
        assets = payload.get("assets", {}) if isinstance(payload, dict) else {}
        return assets if isinstance(assets, dict) else {}
    except (OSError, json.JSONDecodeError):
        print("Aviso: mapa de mídia do Office inválido; será recriado.", file=sys.stderr)
        return {}


def extract_media(product: dict) -> dict[str, object]:
    localized = next((item for item in product.get("LocalizedProperties", []) if isinstance(item, dict)), {})
    images = [item for item in localized.get("Images", []) if isinstance(item, dict)]
    images = [item for item in images if safe_source_url(item.get("Uri"))]

    logos = [image for image in images if (image.get("ImagePurpose") or "").lower() in PURPOSE_RANK]
    logos.sort(key=lambda image: (
        PURPOSE_RANK.get((image.get("ImagePurpose") or "").lower(), 10),
        abs(1 - (image.get("Width", 0) / image.get("Height", 1))) if image.get("Width") and image.get("Height") else 9,
        -(image.get("Width", 0) * image.get("Height", 0)),
    ))

    banners = [image for image in images if (image.get("ImagePurpose") or "").lower() != "screenshot"]
    banners.sort(key=lambda image: (
        0 if (image.get("ImagePurpose") or "").lower() == "superheroart" else 1,
        0 if image.get("Width", 0) > image.get("Height", 0) else 1,
        -(image.get("Width", 0) / max(image.get("Height", 1), 1)),
        -(image.get("Width", 0) * image.get("Height", 0)),
    ))

    screenshot_images = [
        image for image in images
        if (image.get("ImagePurpose") or "").casefold() == "screenshot"
    ]
    screenshot_images.sort(
        key=lambda image: (
            -(int(image.get("Width") or 0) * int(image.get("Height") or 0)),
            -int(image.get("Width") or 0),
        )
    )

    screenshots: list[str] = []
    seen: set[str] = set()
    for image in screenshot_images:
        uri = high_resolution_screenshot_url(image.get("Uri"))
        if uri and uri not in seen:
            screenshots.append(uri)
            seen.add(uri)
    return {
        "icon": safe_source_url(logos[0].get("Uri")) if logos else None,
        "banner": safe_source_url(banners[0].get("Uri")) if banners else None,
        "screenshots": screenshots[:12],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("catalog", type=Path)
    parser.add_argument("--map", dest="map_path", type=Path, required=True)
    parser.add_argument("--market", default="BR")
    parser.add_argument("--language", default="pt-BR")
    parser.add_argument("--public-base", default=os.environ.get("R2_PUBLIC_BASE", "").rstrip("/"))
    args = parser.parse_args()

    if not args.catalog.is_file():
        parser.error(f"catálogo não encontrado: {args.catalog}")
    if not args.public_base.startswith("https://"):
        parser.error("defina --public-base ou R2_PUBLIC_BASE com a URL pública HTTPS do bucket")

    account_id = os.environ["R2_ACCOUNT_ID"]
    access_key = os.environ["R2_ACCESS_KEY_ID"]
    secret_key = os.environ["R2_SECRET_ACCESS_KEY"]
    bucket = os.environ.get("R2_BUCKET") or "winprovision"
    r2 = boto3.client(
        "s3", endpoint_url=f"https://{account_id}.r2.cloudflarestorage.com",
        aws_access_key_id=access_key, aws_secret_access_key=secret_key,
        config=Config(signature_version="s3v4", retries={"max_attempts": 5, "mode": "standard"}),
        region_name="auto",
    )

    retry = Retry(total=3, connect=3, read=3, status=3, backoff_factor=0.5,
                  status_forcelist=(429, 500, 502, 503, 504), allowed_methods=frozenset({"GET", "HEAD"}))
    session = requests.Session()
    session.headers.update({"User-Agent": "WinProvision-Office-Asset-Sync/1.0"})
    session.mount("https://", HTTPAdapter(max_retries=retry))

    catalog = json.loads(args.catalog.read_text(encoding="utf-8"))
    offers = catalog.get("storeOffers") if isinstance(catalog, dict) else None
    if not isinstance(offers, list):
        parser.error("catálogo sem a lista storeOffers")
    ids = list(dict.fromkeys(
        offer.get("storeProductId", "") for offer in offers
        if isinstance(offer, dict) and SAFE_ID.fullmatch(offer.get("storeProductId", ""))
    ))
    if not ids:
        parser.error("nenhum Store Product ID válido em storeOffers")

    response = session.get(DISPLAY_CATALOG_URL, params={
        "bigIds": ",".join(ids), "market": args.market, "languages": args.language,
        "fieldsTemplate": "Details",
    }, timeout=(10, 30))
    response.raise_for_status()
    products = response.json().get("Products", [])
    product_by_id = {
        str(product.get("ProductId", "")).casefold(): product
        for product in products if isinstance(product, dict)
    }

    image_map = load_image_map(args.map_path)
    stats = {"downloaded": 0, "reused": 0, "missing_products": [], "missing_screenshots": []}

    def upload_image(source_url: str | None) -> str | None:
        source_url = safe_source_url(source_url)
        if not source_url:
            return None
        source_hash = hashlib.sha256(source_url.encode("utf-8")).hexdigest()
        cached = image_map.get(source_hash)
        if isinstance(cached, dict) and cached.get("sourceUrl") == source_url and cached.get("publicUrl"):
            cached_url = cached["publicUrl"]
            try:
                head = session.head(cached_url, timeout=(8, 15), allow_redirects=True)
                if head.status_code == 200:
                    stats["reused"] += 1
                    return cached_url
            except requests.RequestException:
                pass

        with session.get(source_url, timeout=(10, 30), stream=True) as image_response:
            image_response.raise_for_status()
            if safe_source_url(image_response.url) is None:
                raise ValueError("a URL da imagem redirecionou para um domínio não permitido")
            content_type = image_response.headers.get("Content-Type", "").split(";", 1)[0].lower()
            if not content_type.startswith("image/"):
                raise ValueError(f"content-type inesperado: {content_type or 'ausente'}")
            size = image_response.headers.get("Content-Length")
            if size and int(size) > MAX_IMAGE_BYTES:
                raise ValueError("imagem excede 20 MB")
            data = bytearray()
            for chunk in image_response.iter_content(64 * 1024):
                data.extend(chunk)
                if len(data) > MAX_IMAGE_BYTES:
                    raise ValueError("imagem excede 20 MB")
        if not data:
            raise ValueError("imagem vazia")

        try:
            with Image.open(BytesIO(data)) as image:
                image.verify()
                image_format = image.format
        except (OSError, Image.DecompressionBombError) as exc:
            raise ValueError(f"imagem inválida ou insegura: {exc}") from exc
        extension = FORMAT_EXTENSION.get(image_format or "")
        if not extension:
            raise ValueError(f"formato de imagem não suportado: {image_format}")

        content_hash = hashlib.sha256(data).hexdigest()
        object_key = f"Store/MsStore/Assets/{content_hash}.{extension}"
        r2.put_object(
            Bucket=bucket, Key=object_key, Body=bytes(data),
            ContentType={"jpg": "image/jpeg", "tif": "image/tiff", "ico": "image/x-icon"}.get(extension, f"image/{extension}"),
            CacheControl="public, max-age=31536000, immutable",
            Metadata={"sha256": content_hash, "source-url-sha256": source_hash},
        )
        public_url = f"{args.public_base}/{object_key}"
        verification = session.head(public_url, timeout=(8, 15), allow_redirects=True)
        if verification.status_code != 200:
            raise RuntimeError(f"asset enviado, mas a URL pública respondeu HTTP {verification.status_code}: {public_url}")
        image_map[source_hash] = {
            "sourceUrl": source_url, "publicUrl": public_url,
            "contentSha256": content_hash, "updatedUtc": datetime.now(timezone.utc).isoformat(),
        }
        stats["downloaded"] += 1
        return public_url

    resolved_by_id: dict[str, dict[str, object]] = {}
    for offer in offers:
        if not isinstance(offer, dict):
            continue
        product_id = offer.get("storeProductId", "")
        product = product_by_id.get(product_id.casefold())
        if product is None:
            stats["missing_products"].append(product_id)
            offer["iconUrl"] = None
            offer["bannerUrl"] = None
            offer["screenshots"] = []
            continue

        media = extract_media(product)
        screenshots = [upload_image(url) for url in media["screenshots"]]
        offer["iconUrl"] = upload_image(media["icon"])
        offer["bannerUrl"] = upload_image(media["banner"])
        offer["screenshots"] = [url for url in screenshots if url]
        if not offer["screenshots"]:
            stats["missing_screenshots"].append(product_id)
        resolved_by_id[product_id.casefold()] = offer

    # Os planos ODT relacionados reutilizam a mídia da oferta correspondente.
    # Para Home/Personal, Family é a imagem representativa do plano compartilhado.
    for plan in catalog.get("products", []):
        if not isinstance(plan, dict):
            continue
        linked = [offer for offer in offers if isinstance(offer, dict) and offer.get("odtProductId", "").casefold() == plan.get("productId", "").casefold()]
        preferred = next((offer for offer in linked if offer.get("storeProductId", "").casefold() == "cfq7ttc0k5dm"), None)
        source_offer = preferred or next((offer for offer in linked if offer.get("iconUrl") or offer.get("screenshots")), None)
        if source_offer:
            plan["iconUrl"] = source_offer.get("iconUrl")
            plan["bannerUrl"] = source_offer.get("bannerUrl")
            plan["screenshots"] = source_offer.get("screenshots", [])

    if stats["missing_products"]:
        print("Ofertas sem resposta da Display Catalog: " + ", ".join(stats["missing_products"]), file=sys.stderr)
    if stats["missing_screenshots"]:
        print("Ofertas sem screenshots na Display Catalog: " + ", ".join(stats["missing_screenshots"]), file=sys.stderr)

    # Family e Personal precisam validar o fluxo visual antes de o catálogo ser publicado.
    for required_id in ("CFQ7TTC0K5DM", "CFQ7TTC0K5BF"):
        offer = next((item for item in offers if isinstance(item, dict) and item.get("storeProductId", "").casefold() == required_id.casefold()), None)
        if offer is None or not offer.get("screenshots"):
            raise RuntimeError(f"Sem screenshots públicos verificados para a oferta obrigatória {required_id}; catálogo não publicado.")

    args.catalog.write_text(json.dumps(catalog, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    args.map_path.parent.mkdir(parents=True, exist_ok=True)
    args.map_path.write_text(json.dumps({"schemaVersion": 1, "assets": image_map}, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    print(
        f"Mídia Office: {stats['downloaded']} enviada(s), {stats['reused']} reutilizada(s), "
        f"{len(stats['missing_products'])} oferta(s) sem produto e {len(stats['missing_screenshots'])} sem screenshot."
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (requests.RequestException, BotoCoreError, ClientError, OSError, ValueError, RuntimeError, KeyError) as exc:
        print(f"Falha ao sincronizar mídia Office: {exc}", file=sys.stderr)
        raise SystemExit(1)
