#!/usr/bin/env python3
"""Match the curated UniGetUI screenshot database to WinGet IDs and publish to R2.

The source database uses normalized package slugs rather than WinGet identifiers.
Only unique, exact matches are attached automatically. Ambiguous and unmatched
records are written to a report and left out of apps.json.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import threading
import unicodedata
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import UTC, datetime, timedelta
from io import BytesIO
from pathlib import Path
from urllib.parse import quote, urlparse

import boto3
import requests
from botocore.config import Config
from botocore.exceptions import BotoCoreError, ClientError
from PIL import Image
from requests.adapters import HTTPAdapter
from urllib3.util.retry import Retry

DEFAULT_PUBLIC_BASE = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev"
R2_PREFIX = "Store/Screenshot_Database"
MAX_IMAGE_BYTES = 20 * 1024 * 1024
MAX_SCREENSHOTS_PER_APP = 12
MAX_WORKERS = 8
EXTENSIONS = {"PNG": "png", "JPEG": "jpg", "WEBP": "webp", "GIF": "gif", "BMP": "bmp"}
_thread_local = threading.local()


def slug(value: str) -> str:
    normalized = unicodedata.normalize("NFKD", value.casefold())
    return "".join(char for char in normalized if char.isalnum() and not unicodedata.combining(char))


def normalized_id(value: str) -> str:
    return value.strip().casefold()


def build_match_indexes(apps: list[dict]) -> dict[int, dict[str, list[dict]]]:
    indexes: dict[int, dict[str, list[dict]]] = {3: {}, 2: {}, 1: {}}
    for app in apps:
        if str(app.get("source", "winget")).casefold() != "winget":
            continue
        package_id = app.get("id")
        name = app.get("name")
        if not isinstance(package_id, str) or not isinstance(name, str):
            continue
        parts = package_id.split(".")
        for rank, value in ((3, slug(package_id)), (2, slug(parts[-1]) if parts else ""), (1, slug(name))):
            if value:
                indexes[rank].setdefault(value, []).append(app)
    return indexes


def match_key(key: str, indexes: dict[int, dict[str, list[dict]]]) -> tuple[dict | None, str]:
    """Return a unique exact package/name match; never guess between ties."""
    wanted = slug(key)
    for rank in (3, 2, 1):
        matches = indexes[rank].get(wanted, [])
        if len(matches) == 1:
            return matches[0], f"exact-{rank}"
        if len(matches) > 1:
            return None, "ambiguous"
    return None, "unmatched"


def load_json(path: Path):
    with path.open(encoding="utf-8-sig") as stream:
        return json.load(stream)


def load_state(path: Path) -> dict:
    if not path.is_file():
        return {"schemaVersion": 1, "assets": {}}
    try:
        payload = load_json(path)
        assets = payload.get("assets", {}) if isinstance(payload, dict) else {}
        if isinstance(assets, dict):
            return {"schemaVersion": 1, "assets": assets}
    except (OSError, json.JSONDecodeError):
        pass
    print("Aviso: cache de screenshots inválido; será recriado.", file=sys.stderr)
    return {"schemaVersion": 1, "assets": {}}


def public_key_for(package_id: str, digest: str, extension: str) -> str:
    lowered_id = normalized_id(package_id)
    if not re.fullmatch(r"[a-z0-9][a-z0-9._-]{0,254}", lowered_id):
        raise ValueError(f"ID WinGet inválido para objeto R2: {package_id!r}")
    return f"{R2_PREFIX}/{quote(lowered_id, safe='._-')}/{digest}.{extension}"


def _session() -> requests.Session:
    session = getattr(_thread_local, "session", None)
    if session is None:
        session = requests.Session()
        retry = Retry(
            total=3,
            connect=3,
            read=2,
            backoff_factor=0.4,
            status_forcelist=(429, 500, 502, 503, 504),
            allowed_methods=frozenset({"GET"}),
        )
        adapter = HTTPAdapter(max_retries=retry, pool_connections=MAX_WORKERS, pool_maxsize=MAX_WORKERS)
        session.mount("https://", adapter)
        session.headers.update({"User-Agent": "WinProvisionStore-ScreenshotSync/1.0"})
        _thread_local.session = session
    return session


def download_image(url: str) -> tuple[bytes, str]:
    parsed = urlparse(url)
    if parsed.scheme != "https" or not parsed.hostname:
        raise ValueError("URL não HTTPS")
    with _session().get(url, timeout=(10, 35), stream=True, allow_redirects=True) as response:
        response.raise_for_status()
        if urlparse(response.url).scheme != "https":
            raise ValueError("redirecionamento para URL sem HTTPS")
        content_type = response.headers.get("Content-Type", "").split(";", 1)[0].lower()
        if content_type and not content_type.startswith("image/"):
            raise ValueError(f"content-type não é imagem: {content_type}")
        length = response.headers.get("Content-Length")
        if length and int(length) > MAX_IMAGE_BYTES:
            raise ValueError("imagem excede 20 MiB")
        body = bytearray()
        for chunk in response.iter_content(64 * 1024):
            body.extend(chunk)
            if len(body) > MAX_IMAGE_BYTES:
                raise ValueError("imagem excede 20 MiB")
    if not body:
        raise ValueError("imagem vazia")
    try:
        with Image.open(BytesIO(body)) as image:
            image.verify()
            image_format = image.format
    except (OSError, Image.DecompressionBombError) as exc:
        raise ValueError(f"imagem inválida: {exc}") from exc
    extension = EXTENSIONS.get(image_format or "")
    if not extension:
        raise ValueError(f"formato de imagem não suportado: {image_format}")
    return bytes(body), extension


def sync_one(
    client, public_base: str, package_id: str, url: str, previous: dict, recheck_days: int
) -> tuple[str, dict | None, str | None]:
    now = datetime.now(UTC)
    old = previous.get(url)
    if isinstance(old, dict) and old.get("publicUrl") and old.get("sha256"):
        try:
            checked = datetime.fromisoformat(str(old.get("checkedAt", "")).replace("Z", "+00:00"))
            if now - checked < timedelta(days=recheck_days):
                return url, old, None
        except ValueError:
            pass

    try:
        body, extension = download_image(url)
        digest = hashlib.sha256(body).hexdigest()
        if isinstance(old, dict) and old.get("sha256") == digest and old.get("publicUrl"):
            refreshed = {**old, "checkedAt": now.isoformat()}
            return url, refreshed, None
        object_key = public_key_for(package_id, digest, extension)
        content_type = {
            "png": "image/png",
            "jpg": "image/jpeg",
            "webp": "image/webp",
            "gif": "image/gif",
            "bmp": "image/bmp",
        }[extension]
        client.put_object(
            Bucket=os.environ.get("R2_BUCKET") or "winprovision",
            Key=object_key,
            Body=body,
            ContentType=content_type,
            CacheControl="public, max-age=31536000, immutable",
            Metadata={"sha256": digest, "source-url-sha256": hashlib.sha256(url.encode()).hexdigest()},
        )
        asset = {
            "publicUrl": f"{public_base}/{object_key}",
            "sha256": digest,
            "checkedAt": now.isoformat(),
            "packageId": normalized_id(package_id),
        }
        return url, asset, None
    except (requests.RequestException, BotoCoreError, ClientError, OSError, ValueError) as exc:
        if isinstance(old, dict) and old.get("publicUrl"):
            return url, old, f"{url}: {exc} (mantido cache anterior)"
        return url, None, f"{url}: {exc}"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="JSON limpo do UniGetUI")
    parser.add_argument("catalog", type=Path, help="apps.json gerado pelo Indexer")
    parser.add_argument("state", type=Path, help="cache local de assets R2")
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--public-base", default=os.environ.get("R2_PUBLIC_BASE", DEFAULT_PUBLIC_BASE).rstrip("/"))
    parser.add_argument("--workers", type=int, default=MAX_WORKERS)
    parser.add_argument("--recheck-days", type=int, default=30)
    parser.add_argument("--max-failure-rate", type=float, default=0.25)
    args = parser.parse_args()
    if args.workers < 1 or args.recheck_days < 1 or not 0 <= args.max_failure_rate <= 1:
        parser.error("--workers e --recheck-days precisam ser positivos; --max-failure-rate deve ficar entre 0 e 1")
    if not args.public_base.startswith("https://"):
        parser.error("--public-base precisa ser HTTPS")

    try:
        source = load_json(args.source)
        catalog = load_json(args.catalog)
        if not isinstance(source, dict) or not isinstance(source.get("icons_and_screenshots"), dict):
            raise ValueError("formato do JSON do UniGetUI inválido")
        if not isinstance(catalog, list):
            raise ValueError("apps.json precisa conter uma lista")
    except (OSError, json.JSONDecodeError, ValueError) as exc:
        print(f"Entrada inválida; nada foi publicado: {exc}", file=sys.stderr)
        return 1

    asset_state = load_state(args.state)
    prior_assets = asset_state["assets"]
    indexes = build_match_indexes(catalog)
    match_results = []
    matched: dict[str, list[str]] = {}
    failures: list[str] = []
    for source_key, record in source["icons_and_screenshots"].items():
        if source_key.startswith("__") or not isinstance(record, dict):
            match_results.append({"sourceKey": source_key, "status": "ignored"})
            continue
        images = record.get("images")
        urls = (
            list(
                dict.fromkeys(
                    value.strip()
                    for value in images
                    if isinstance(images, list) and isinstance(value, str) and value.strip().startswith("https://")
                )
            )
            if isinstance(images, list)
            else []
        )
        if not urls:
            match_results.append({"sourceKey": source_key, "status": "no-valid-images"})
            continue
        app, match_status = match_key(source_key, indexes)
        if app is None:
            match_results.append({"sourceKey": source_key, "status": match_status})
            continue
        package_id = app["id"]
        package_key = normalized_id(package_id)
        package_urls = matched.setdefault(package_key, [])
        for url in urls:
            if url not in package_urls and len(package_urls) < MAX_SCREENSHOTS_PER_APP:
                package_urls.append(url)
        match_results.append(
            {
                "sourceKey": source_key,
                "packageId": normalized_id(package_id),
                "status": "matched",
                "match": match_status,
                "images": len(urls[:MAX_SCREENSHOTS_PER_APP]),
            }
        )

    unique_pairs = sorted((package_id, url) for package_id, urls in matched.items() for url in urls)
    cache_keys = [(package_id, url, f"{package_id}|{url}") for package_id, url in unique_pairs]
    if unique_pairs and all(
        os.environ.get(name) for name in ("R2_ACCOUNT_ID", "R2_ACCESS_KEY_ID", "R2_SECRET_ACCESS_KEY")
    ):
        client = boto3.client(
            "s3",
            endpoint_url=f"https://{os.environ['R2_ACCOUNT_ID']}.r2.cloudflarestorage.com",
            aws_access_key_id=os.environ["R2_ACCESS_KEY_ID"],
            aws_secret_access_key=os.environ["R2_SECRET_ACCESS_KEY"],
            config=Config(
                signature_version="s3v4",
                max_pool_connections=args.workers,
                retries={"max_attempts": 4, "mode": "standard"},
            ),
            region_name="auto",
        )
        resolved: dict[str, dict] = {}
        with ThreadPoolExecutor(max_workers=args.workers) as executor:
            futures = {
                executor.submit(
                    sync_one,
                    client,
                    args.public_base,
                    package_id,
                    url,
                    {url: prior_assets.get(cache_key)},
                    args.recheck_days,
                ): cache_key
                for package_id, url, cache_key in cache_keys
            }
            for future in as_completed(futures):
                url, asset, error = future.result()
                if asset:
                    resolved[futures[future]] = asset
                if error:
                    failures.append(error)
    else:
        if unique_pairs:
            print("R2 credentials ausentes: execução de análise, sem baixar nem publicar imagens.", file=sys.stderr)
        resolved = {
            cache_key: prior_assets[cache_key]
            for _, _, cache_key in cache_keys
            if isinstance(prior_assets.get(cache_key), dict) and prior_assets[cache_key].get("publicUrl")
        }

    for app in catalog:
        if not isinstance(app, dict) or not isinstance(app.get("id"), str):
            continue
        urls = matched.get(normalized_id(app["id"]), [])
        public_urls = [
            resolved[f"{normalized_id(app['id'])}|{url}"]["publicUrl"]
            for url in urls
            if f"{normalized_id(app['id'])}|{url}" in resolved
        ]
        if public_urls:
            app["screenshotUrls"] = public_urls
        else:
            app.pop("screenshotUrls", None)

    report = {
        "schemaVersion": 1,
        "generatedAt": datetime.now(UTC).isoformat(),
        "sourceRecords": len(source["icons_and_screenshots"]),
        "matchedPackages": len(matched),
        "uniqueSourceImages": len(unique_pairs),
        "imagesAvailableInR2": len(resolved),
        "ambiguous": sum(item["status"] == "ambiguous" for item in match_results),
        "unmatched": sum(item["status"] == "unmatched" for item in match_results),
        "failures": failures,
        "matches": match_results,
    }
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    if source["icons_and_screenshots"] and not matched:
        print(
            "Nenhuma chave do banco de screenshots correspondeu a um pacote WinGet; catálogo não atualizado.",
            file=sys.stderr,
        )
        return 1
    failure_rate = (len(unique_pairs) - len(resolved)) / len(unique_pairs) if unique_pairs else 0.0
    if failure_rate > args.max_failure_rate:
        print(
            f"Sincronização abortada: {failure_rate:.1%} das imagens associadas não estão disponíveis "
            f"(limite {args.max_failure_rate:.0%}); apps.json e cache não foram atualizados.",
            file=sys.stderr,
        )
        return 1

    args.catalog.write_text(json.dumps(catalog, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    asset_state["assets"] = resolved
    args.state.parent.mkdir(parents=True, exist_ok=True)
    args.state.write_text(json.dumps(asset_state, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")

    print(
        f"Screenshots: {len(matched)} pacote(s) associados, {len(unique_pairs)} imagem(ns) por pacote, "
        f"{len(resolved)} disponível(is) no R2, {report['ambiguous']} ambíguo(s), {report['unmatched']} sem associação."
    )
    if failures:
        print(
            f"{len(failures)} imagem(ns) falharam; consulte o relatório. Cache anterior foi preservado quando disponível.",
            file=sys.stderr,
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
