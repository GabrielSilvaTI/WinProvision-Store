#!/usr/bin/env python3
"""Find explicitly identified screenshots on WinGet homepages and publish to R2.

This is a conservative, incremental crawler: it visits only a bounded number of
public HTTPS homepages per run, respects robots.txt, accepts images whose page
metadata/attributes identify them as screenshots, and never replaces existing
catalog screenshots.
"""

from __future__ import annotations

import argparse
import hashlib
import ipaddress
import json
import os
import re
import socket
import sys
import time
import unicodedata
import warnings
from datetime import datetime, timedelta, timezone
from html.parser import HTMLParser
from io import BytesIO
from pathlib import Path
from urllib.robotparser import RobotFileParser
from urllib.parse import quote, urljoin, urlparse

import boto3
import requests
from botocore.config import Config
from botocore.exceptions import BotoCoreError, ClientError
from PIL import Image, UnidentifiedImageError

DEFAULT_PUBLIC_BASE = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev"
R2_PREFIX = "Store/Screenshot_Database/Homepage"
USER_AGENT = "WinProvisionStore-ScreenshotIndexer/1.0 (+https://github.com/GabrielSilvaTI/WinProvision-Store)"
MAX_HTML_BYTES = 2 * 1024 * 1024
MAX_ROBOTS_BYTES = 512 * 1024
MAX_IMAGE_BYTES = 20 * 1024 * 1024
MAX_CANDIDATES = 3
SCREENSHOT_WORDS = ("screenshot", "screen-shot", "screenshots", "screen shot", "captura", "capturas",
                    "app-preview", "app preview", "preview-screenshot", "product-screenshot", "product screenshot")
NEGATIVE_WORDS = ("logo", "icon", "favicon", "avatar", "profile", "badge", "sprite", "banner")
EXTENSIONS = {"PNG": "png", "JPEG": "jpg", "WEBP": "webp", "GIF": "gif"}
Image.MAX_IMAGE_PIXELS = 40_000_000
warnings.simplefilter("error", Image.DecompressionBombWarning)


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def normalize_id(value: str) -> str:
    normalized = unicodedata.normalize("NFKD", value.casefold())
    normalized = "".join(char for char in normalized if not unicodedata.combining(char))
    normalized = re.sub(r"[^a-z0-9._-]+", "-", normalized).strip(".-_")
    if not normalized or len(normalized) > 180:
        raise ValueError(f"ID de pacote inválido para nome de objeto: {value!r}")
    return normalized


def load_json(path: Path, default):
    if not path.is_file():
        return default
    with path.open(encoding="utf-8-sig") as stream:
        return json.load(stream)


def is_public_https(url: str) -> bool:
    try:
        parsed = urlparse(url)
        if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password:
            return False
        if parsed.port not in (None, 443):
            return False
        addresses = socket.getaddrinfo(parsed.hostname, parsed.port or 443, type=socket.SOCK_STREAM)
        return bool(addresses) and all(ipaddress.ip_address(item[4][0].split("%", 1)[0]).is_global
                                       for item in addresses)
    except (OSError, ValueError):
        return False


def is_https_url(url: str) -> bool:
    parsed = urlparse(url)
    return parsed.scheme == "https" and bool(parsed.hostname) and not parsed.username and not parsed.password


def normalize_homepage(url: str) -> str:
    value = url.strip()
    if urlparse(value).scheme == "http":
        value = "https:" + value[5:]
    return value


def homepage_match_key(url: str) -> tuple[str, int, str] | None:
    value = normalize_homepage(url)
    parsed = urlparse(value)
    if not is_https_url(value):
        return None
    return (parsed.hostname.casefold(), parsed.port or 443, parsed.path.rstrip("/") or "/")


class PageImages(HTMLParser):
    def __init__(self, base_url: str):
        super().__init__(convert_charrefs=True)
        self.base_url = base_url
        self.images: list[tuple[int, str]] = []
        self.meta: list[tuple[int, str]] = []

    @staticmethod
    def _score(url: str, description: str, *, metadata: bool = False) -> int:
        text = f"{url} {description}".casefold().replace("_", "-")
        score = 0
        if any(word in text for word in SCREENSHOT_WORDS):
            score += 6
        if any(word in text for word in NEGATIVE_WORDS):
            score -= 8
        if metadata and score == 0:
            score = 1  # weak fallback: metadata images are considered only after a screenshot was identified
        return score

    def handle_starttag(self, tag: str, attrs) -> None:
        values = {str(k).casefold(): str(v or "") for k, v in attrs}
        if tag.casefold() == "meta":
            key = (values.get("property") or values.get("name") or "").casefold()
            value = values.get("content", "").strip()
            if value and key in ("og:image", "og:image:url", "twitter:image", "twitter:image:src"):
                self.meta.append((self._score(value, " ".join(values.values()), metadata=True),
                                  urljoin(self.base_url, value)))
        elif tag.casefold() in ("img", "source"):
            description = " ".join(values.get(k, "") for k in (
                "alt", "title", "aria-label", "class", "id", "data-testid", "itemprop", "src", "data-src"))
            sources = [values.get("src", ""), values.get("data-src", ""), values.get("data-original", "")]
            srcset = values.get("srcset", "")
            if srcset:
                sources.extend(part.strip().split()[0] for part in srcset.split(",") if part.strip())
            width = self._number(values.get("width"))
            height = self._number(values.get("height"))
            base_score = self._score(" ".join(sources), description)
            if width >= 320 and height >= 180:
                base_score += 2
            if width and width < 240 or height and height < 140:
                base_score -= 4
            for source in sources:
                if source.strip():
                    self.images.append((base_score, urljoin(self.base_url, source.strip())))
        elif tag.casefold() == "a":
            href = values.get("href", "")
            score = self._score(href, values.get("title", "") + " " + values.get("aria-label", ""))
            if score >= 6:
                self.images.append((score, urljoin(self.base_url, href)))

    @staticmethod
    def _number(value: str) -> int:
        try:
            return int(value)
        except (ValueError, TypeError):
            return 0


class SafeHttp:
    def __init__(self):
        self.session = requests.Session()
        self.session.headers.update({"User-Agent": USER_AGENT, "Accept": "text/html,image/avif,image/webp,image/*,*/*;q=0.8"})
        self.robots: dict[str, tuple[float, RobotFileParser]] = {}

    def get(self, url: str, limit: int):
        current = url
        for _ in range(5):
            if not is_public_https(current):
                raise ValueError("URL não HTTPS público ou endereço privado")
            response = self.session.get(current, timeout=(6, 12), stream=True, allow_redirects=False)
            if response.is_redirect or response.is_permanent_redirect:
                location = response.headers.get("Location")
                response.close()
                if not location:
                    raise ValueError("redirecionamento sem destino")
                current = urljoin(current, location)
                continue
            response.raise_for_status()
            content_length = response.headers.get("Content-Length")
            if content_length and int(content_length) > limit:
                response.close()
                raise ValueError("resposta excede o limite permitido")
            body = bytearray()
            for chunk in response.iter_content(64 * 1024):
                body.extend(chunk)
                if len(body) > limit:
                    response.close()
                    raise ValueError("resposta excede o limite permitido")
            content_type = response.headers.get("Content-Type", "").split(";", 1)[0].lower()
            final_url = response.url
            response.close()
            return bytes(body), content_type, final_url
        raise ValueError("redirecionamentos em excesso")

    def allowed_by_robots(self, url: str) -> bool:
        parsed = urlparse(url)
        host_key = f"{parsed.scheme}://{parsed.netloc}"
        cached = self.robots.get(host_key)
        if cached is None or cached[0] < time.monotonic():
            try:
                body, content_type, _ = self.get(f"{host_key}/robots.txt", MAX_ROBOTS_BYTES)
                lines = body.decode("utf-8", errors="replace").splitlines() if content_type in (
                    "text/plain", "application/octet-stream", "") else []
            except requests.HTTPError as exc:
                lines = [] if exc.response is not None and exc.response.status_code in (404, 410) else ["User-agent: *", "Disallow: /"]
            except (requests.RequestException, OSError, ValueError):
                lines = ["User-agent: *", "Disallow: /"]
            robot_parser = RobotFileParser()
            robot_parser.parse(lines)
            cached = (time.monotonic() + 3600, robot_parser)
            self.robots[host_key] = cached
        agent = USER_AGENT.split("/", 1)[0]
        return cached[1].can_fetch(agent, url)


def image_candidates(html: bytes, page_url: str) -> list[str]:
    parser = PageImages(page_url)
    parser.feed(html.decode("utf-8", errors="replace"))
    candidates = parser.images + parser.meta
    unique: dict[str, int] = {}
    for score, url in candidates:
        parsed = urlparse(url)
        if not is_https_url(url):
            continue
        if parsed.path.casefold().endswith((".svg", ".ico")):
            continue
        unique[url] = max(score, unique.get(url, -100))
    return [url for url, score in sorted(unique.items(), key=lambda item: (-item[1], item[0]))
            if score >= 6][:MAX_CANDIDATES]


def fetch_screenshot(http: SafeHttp, homepage: str) -> tuple[bytes, str, str] | None:
    if not http.allowed_by_robots(homepage):
        return None
    html, content_type, final_url = http.get(homepage, MAX_HTML_BYTES)
    if urlparse(final_url).netloc != urlparse(homepage).netloc and not http.allowed_by_robots(final_url):
        return None
    if content_type not in ("text/html", "application/xhtml+xml", "", "application/octet-stream"):
        return None
    for image_url in image_candidates(html, final_url):
        if not http.allowed_by_robots(image_url):
            continue
        try:
            body, image_type, resolved_url = http.get(image_url, MAX_IMAGE_BYTES)
            if urlparse(resolved_url).netloc != urlparse(image_url).netloc and not http.allowed_by_robots(resolved_url):
                continue
            if image_type and not image_type.startswith("image/"):
                continue
            with Image.open(BytesIO(body)) as image:
                if image.width < 320 or image.height < 180:
                    continue
                image.verify()
                image_format = image.format
            extension = EXTENSIONS.get(image_format or "")
            if extension:
                return body, extension, resolved_url
        except (requests.RequestException, OSError, ValueError, UnidentifiedImageError,
                Image.DecompressionBombError, Image.DecompressionBombWarning):
            continue
    return None


def load_state(path: Path) -> dict:
    try:
        value = load_json(path, {"schemaVersion": 1, "nextIndex": 0, "packages": {}})
        if not isinstance(value, dict) or not isinstance(value.get("packages"), dict):
            raise ValueError("estado inválido")
        return {"schemaVersion": 1, "nextIndex": max(0, int(value.get("nextIndex", 0))), "packages": value["packages"]}
    except (OSError, ValueError, json.JSONDecodeError, TypeError):
        return {"schemaVersion": 1, "nextIndex": 0, "packages": {}}


def make_r2_client():
    return boto3.client("s3", endpoint_url=f"https://{os.environ['R2_ACCOUNT_ID']}.r2.cloudflarestorage.com",
                        aws_access_key_id=os.environ["R2_ACCESS_KEY_ID"],
                        aws_secret_access_key=os.environ["R2_SECRET_ACCESS_KEY"],
                        config=Config(signature_version="s3v4", retries={"max_attempts": 4, "mode": "standard"}),
                        region_name="auto")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("catalog", type=Path)
    parser.add_argument("state", type=Path)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--batch-size", type=int, default=100)
    parser.add_argument("--recheck-days", type=int, default=30)
    parser.add_argument("--package-id", default="", help="processa um ID do catálogo em vez do lote normal")
    parser.add_argument("--homepage-url", default="", help="processa uma URL; sem ID, precisa corresponder a um único app")
    parser.add_argument("--public-base", default=os.environ.get("R2_PUBLIC_BASE", DEFAULT_PUBLIC_BASE).rstrip("/"))
    args = parser.parse_args()
    if args.batch_size < 1 or args.recheck_days < 1 or not args.public_base.startswith("https://"):
        parser.error("batch-size e recheck-days devem ser positivos; public-base deve usar HTTPS")
    try:
        catalog = load_json(args.catalog, None)
        if not isinstance(catalog, list):
            raise ValueError("o catálogo precisa ser uma lista JSON")
        state = load_state(args.state)
    except (OSError, json.JSONDecodeError, ValueError) as exc:
        print(f"Entrada inválida: {exc}", file=sys.stderr)
        return 1

    package_target = args.package_id.strip().casefold()
    url_target = normalize_homepage(args.homepage_url) if args.homepage_url.strip() else ""
    if url_target and not is_https_url(url_target):
        parser.error("--homepage-url precisa ser uma URL HTTPS válida")

    eligible = []
    catalog_apps_by_id = {}
    for index, app in enumerate(catalog):
        if not isinstance(app, dict) or not isinstance(app.get("id"), str):
            continue
        catalog_apps_by_id.setdefault(app["id"].casefold(), []).append((index, app))
        if (str(app.get("source", "winget")).casefold() not in ("winget", "msstore")
                or not isinstance(app.get("homepage"), str)):
            continue
        screenshot_field = "storeScreenshotUrls" if str(app.get("source", "winget")).casefold() == "msstore" else "screenshotUrls"
        if app.get(screenshot_field):
            continue
        homepage = normalize_homepage(app["homepage"])
        if is_https_url(homepage):
            eligible.append((index, app, homepage))

    reports = []
    now = utc_now()
    targeted = bool(package_target or url_target)
    if targeted:
        target_matches = catalog_apps_by_id.get(package_target, []) if package_target else []
        if package_target and len(target_matches) != 1:
            parser.error(f"ID '{args.package_id}' não encontrado ou duplicado no catálogo")
        if package_target:
            target_app = target_matches[0][1]
            target_index = target_matches[0][0]
            if str(target_app.get("source", "winget")).casefold() not in ("winget", "msstore"):
                parser.error(f"O pacote '{args.package_id}' tem uma origem não suportada")
            homepage = url_target or (normalize_homepage(target_app.get("homepage", "")))
            if not is_https_url(homepage):
                parser.error(f"O pacote '{args.package_id}' não tem uma homepage HTTPS válida")
        else:
            target_key = homepage_match_key(url_target)
            exact = [(index, app) for index, app in enumerate(catalog)
                     if isinstance(app, dict) and isinstance(app.get("homepage"), str)
                     and homepage_match_key(app["homepage"]) == target_key]
            if not exact:
                exact = [(index, app) for index, app in enumerate(catalog)
                         if isinstance(app, dict) and isinstance(app.get("homepage"), str)
                         and homepage_match_key(app["homepage"]) is not None
                         and homepage_match_key(app["homepage"])[0] == target_key[0]]
            if len(exact) != 1:
                parser.error("A URL não identifica um único app. Informe também o ID do pacote para vinculá-la sem ambiguidade.")
            target_index, target_app = exact[0]
            if str(target_app.get("source", "winget")).casefold() not in ("winget", "msstore"):
                parser.error("O app correspondente tem uma origem não suportada")
            homepage = url_target
        selected = [(target_index, target_app, homepage)]
        next_index = state["nextIndex"]
    elif not eligible:
        selected = []
        next_index = 0
    else:
        start = state["nextIndex"] % len(catalog)
        rotated = [item for item in eligible if item[0] >= start] + [item for item in eligible if item[0] < start]
        selected = rotated[:args.batch_size]
        next_index = (selected[-1][0] + 1) % len(catalog) if selected else start

    try:
        client = make_r2_client()
    except (KeyError, BotoCoreError, ClientError) as exc:
        print(f"Credenciais ou acesso R2 inválidos: {exc}", file=sys.stderr)
        return 1

    http = SafeHttp()
    updated = 0
    skipped_recent = 0
    failures = 0
    for position, (_, app, homepage) in enumerate(selected, start=1):
        package_id = app["id"]
        print(f"[{position}/{len(selected)}] Analisando {package_id}...", flush=True)
        cache_key = normalize_id(package_id)
        previous = state["packages"].get(cache_key, {})
        same_homepage = isinstance(previous, dict) and previous.get("homepage") == homepage
        try:
            checked_at = datetime.fromisoformat(str(previous.get("checkedAt", "")).replace("Z", "+00:00")) if same_homepage else None
        except ValueError:
            checked_at = None
        screenshot_field = "storeScreenshotUrls" if str(app.get("source", "winget")).casefold() == "msstore" else "screenshotUrls"
        if same_homepage and previous.get("publicUrl"):
            app[screenshot_field] = list(dict.fromkeys([*(app.get(screenshot_field) or []), previous["publicUrl"]]))
            skipped_recent += 1
            reports.append({"id": package_id, "status": "cached"})
            continue
        if not targeted and checked_at and now - checked_at < timedelta(days=args.recheck_days):
            skipped_recent += 1
            reports.append({"id": package_id, "status": "recently-checked"})
            continue
        try:
            found = fetch_screenshot(http, homepage)
            record = {"homepage": homepage, "checkedAt": now.isoformat(), "status": "not-found"}
            if found:
                body, extension, source_url = found
                digest = hashlib.sha256(body).hexdigest()
                key = f"{R2_PREFIX}/{quote(cache_key, safe='._-')}/screenshot-{digest}.{extension}"
                content_type = {"png": "image/png", "jpg": "image/jpeg", "webp": "image/webp", "gif": "image/gif"}[extension]
                client.put_object(Bucket=os.environ.get("R2_BUCKET") or "winprovision", Key=key, Body=body,
                                  ContentType=content_type, CacheControl="public, max-age=31536000, immutable",
                                  Metadata={"sha256": digest})
                public_url = f"{args.public_base}/{key}"
                app[screenshot_field] = list(dict.fromkeys([*(app.get(screenshot_field) or []), public_url]))
                record.update({"status": "found", "publicUrl": public_url, "sourceUrl": source_url,
                               "sha256": digest})
                updated += 1
            state["packages"][cache_key] = record
            reports.append({"id": package_id, "status": record["status"]})
        except (requests.RequestException, BotoCoreError, ClientError, OSError, ValueError) as exc:
            failures += 1
            state["packages"][cache_key] = {"homepage": homepage, "checkedAt": now.isoformat(), "status": "error"}
            reports.append({"id": package_id, "status": "error", "error": str(exc)[:300]})
            print(f"Aviso: {package_id}: {exc}", file=sys.stderr)

    state["nextIndex"] = next_index
    state["updatedAt"] = now.isoformat()
    args.catalog.write_text(json.dumps(catalog, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    args.state.parent.mkdir(parents=True, exist_ok=True)
    args.state.write_text(json.dumps(state, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps({"generatedAt": now.isoformat(), "batchSize": args.batch_size,
                                       "targetPackageId": args.package_id.strip() or None,
                                       "targetHomepageUrl": url_target or None,
                                       "selected": len(selected), "screenshotsAdded": updated,
                                       "skippedRecent": skipped_recent, "failures": failures,
                                       "nextIndex": next_index, "packages": reports}, ensure_ascii=False, indent=2),
                           encoding="utf-8")
    print(f"Páginas avaliadas: {len(selected)}; capturas adicionadas: {updated}; "
          f"checagens recentes reaproveitadas: {skipped_recent}; falhas isoladas: {failures}.")
    # Per-site errors are reported but do not fail independent packages in this job.
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
