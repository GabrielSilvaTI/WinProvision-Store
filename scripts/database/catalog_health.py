#!/usr/bin/env python3
"""Read-only health check for the public WinProvision catalogs and their media URLs."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import UTC, datetime
from pathlib import Path
from urllib.parse import urlparse, urlunsplit

import requests
from requests.adapters import HTTPAdapter
from urllib3.util.retry import Retry

CATALOGS = {
    "winget": "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/Catalog/manifest.json",
    "microsoft_store": "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/Catalog/msstore/manifest.json",
    "installer_api": "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/Api/manifest.json",
    "office": "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Office/Database/catalog.json",
    "icon_manifest": "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/icon-manifest.json",
}
MEDIA_KEYS = {
    "iconurl",
    "storeiconurl",
    "bannerurl",
    "storebannerurl",
    "screenshoturls",
    "storescreenshoturls",
    "screenshots",
    "screenshoturl",
    "iconurls",
}
MAX_JSON_BYTES = 256 * 1024 * 1024
_local = threading.local()


def session() -> requests.Session:
    value = getattr(_local, "session", None)
    if value is None:
        value = requests.Session()
        retry = Retry(
            total=3,
            connect=3,
            read=2,
            backoff_factor=0.5,
            status_forcelist=(408, 429, 500, 502, 503, 504),
            allowed_methods=frozenset({"GET"}),
            respect_retry_after_header=True,
        )
        value.mount("https://", HTTPAdapter(max_retries=retry, pool_connections=24, pool_maxsize=24))
        value.headers["User-Agent"] = "WinProvision-CatalogHealth/1.0"
        _local.session = value
    return value


def get_json(name: str, url: str):
    parsed = urlparse(url)
    if parsed.scheme != "https" or parsed.hostname not in {"pub-166b41912a994dbe86583ba10596d673.r2.dev"}:
        raise ValueError(f"URL de catálogo não permitida: {url}")
    response = session().get(url, timeout=(15, 60), stream=True)
    try:
        response.raise_for_status()
        if urlparse(response.url).scheme != "https":
            raise ValueError("catálogo redirecionou para URL sem HTTPS")
        body = bytearray()
        for chunk in response.iter_content(1024 * 1024):
            body.extend(chunk)
            if len(body) > MAX_JSON_BYTES:
                raise ValueError("JSON excedeu o limite de 256 MiB")
        return json.loads(body.decode("utf-8-sig"))
    finally:
        response.close()


def walk_media(node, path: str = ""):
    if isinstance(node, dict):
        for key, value in node.items():
            next_path = f"{path}.{key}" if path else str(key)
            if str(key).casefold() in MEDIA_KEYS:
                values = value if isinstance(value, list) else [value]
                for index, item in enumerate(values):
                    if isinstance(item, str) and item.strip():
                        yield next_path + (f"[{index}]" if isinstance(value, list) else ""), item.strip()
            else:
                yield from walk_media(value, next_path)
    elif isinstance(node, list):
        for index, value in enumerate(node):
            yield from walk_media(value, f"{path}[{index}]")


def check_image(url: str) -> tuple[str, str | None]:
    parsed = urlparse(url)
    if parsed.scheme != "https" or not parsed.hostname:
        return url, "URL não HTTPS ou inválida"
    if parsed.hostname.casefold() != "pub-166b41912a994dbe86583ba10596d673.r2.dev":
        return url, "host de mídia inesperado"
    try:
        response = session().get(url, timeout=(12, 30), stream=True, allow_redirects=True)
        try:
            response.raise_for_status()
            final_url = urlparse(response.url)
            if final_url.scheme != "https" or final_url.hostname != "pub-166b41912a994dbe86583ba10596d673.r2.dev":
                return url, "redirecionou para URL sem HTTPS"
            content_type = response.headers.get("Content-Type", "").split(";", 1)[0].strip().casefold()
            if content_type and not content_type.startswith("image/"):
                return url, f"Content-Type inesperado: {content_type}"
            # Read a small prefix so the connection checks an actual response body.
            if not next(response.iter_content(128), b""):
                return url, "resposta vazia"
            return url, None
        finally:
            response.close()
    except requests.RequestException as exc:
        return url, f"{type(exc).__name__}: {exc}"


def validate_payloads(payloads: dict, minimums: dict[str, int]) -> list[str]:
    errors: list[str] = []
    apps = payloads.get("winget")
    if not isinstance(apps, list) or len(apps) < minimums["winget"]:
        errors.append(f"Índice de busca WinGet: esperada lista com ao menos {minimums['winget']} itens")
    else:
        seen: set[str] = set()
        sources = {"winget": 0, "msstore": 0}
        for index, item in enumerate(apps):
            if not isinstance(item, dict):
                errors.append(f"search-index[{index}] não é objeto")
                continue
            app_id, name, source = item.get("id"), item.get("name"), item.get("source")
            if not isinstance(app_id, str) or not app_id.strip() or not isinstance(name, str) or not name.strip():
                errors.append(f"search-index[{index}] sem ID ou nome válido")
                continue
            key = app_id.casefold()
            if key in seen:
                errors.append(f"ID duplicado no search-index: {app_id}")
            seen.add(key)
            if source in sources:
                sources[source] += 1
            else:
                errors.append(f"O índice WinGet contém origem separada inesperada para {app_id}: {source!r}")
        if sources["winget"] < minimums["winget"]:
            errors.append(f"Contagem WinGet abaixo do mínimo: {sources['winget']}")

    msstore = payloads.get("microsoft_store")
    if not isinstance(msstore, list) or len(msstore) < minimums["microsoft_store"]:
        errors.append(f"Índice Microsoft Store: esperada lista com ao menos {minimums['microsoft_store']} itens")
    else:
        ids: set[str] = set()
        for index, item in enumerate(msstore):
            if not isinstance(item, dict) or item.get("source") != "msstore":
                errors.append(f"msstore search-index[{index}] inválido ou com origem diferente de msstore")
                continue
            app_id = str(item.get("id", "")).strip().casefold()
            if not app_id or app_id in ids:
                errors.append(f"ID ausente/duplicado no catálogo Microsoft Store: {app_id!r}")
            ids.add(app_id)

    office = payloads.get("office")
    if not isinstance(office, dict) or office.get("schemaVersion") not in (1, 2):
        errors.append("Catálogo Office ausente ou schemaVersion inválido")
    elif not isinstance(office.get("products"), list) or not office["products"]:
        errors.append("Catálogo Office sem produtos")

    manifest = payloads.get("icon_manifest")
    if not isinstance(manifest, dict) or len(manifest) < minimums["icon_manifest"]:
        errors.append(f"Manifesto de ícones inválido ou com menos de {minimums['icon_manifest']} entradas")
    elif any(not isinstance(url, str) or not url.startswith("https://") for url in manifest.values()):
        errors.append("Manifesto de ícones contém URL inválida")
    return errors


def main() -> int:
    started = time.perf_counter()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report-dir", type=Path, default=Path("catalog-health-report"))
    parser.add_argument("--workers", type=int, default=24)
    parser.add_argument(
        "--max-media-urls",
        type=int,
        default=0,
        help="0 verifica todas as URLs; valor positivo seleciona uma amostra rotativa",
    )
    parser.add_argument("--max-failure-rate", type=float, default=0.01)
    args = parser.parse_args()
    if args.workers < 1 or args.max_media_urls < 0 or not 0 <= args.max_failure_rate <= 1:
        parser.error("workers precisa ser positivo, max-media-urls não negativo e max-failure-rate entre 0 e 1")

    payloads, catalog_errors = {}, []
    for name, url in CATALOGS.items():
        try:
            payloads[name] = get_json(name, url)
            print(f"JSON OK: {name}")
        except (requests.RequestException, UnicodeError, json.JSONDecodeError, ValueError) as exc:
            catalog_errors.append(f"{name}: {type(exc).__name__}: {exc}")
            print(f"JSON ERRO: {name}: {exc}", file=sys.stderr)

    manifest = payloads.get("winget")
    if isinstance(manifest, dict):
        digest = manifest.get("catalogSha256")
        if (
            manifest.get("schemaVersion") != 2
            or not isinstance(digest, str)
            or not re.fullmatch(r"[0-9a-f]{64}", digest)
        ):
            catalog_errors.append("Manifesto do catálogo WinGet inválido")
        else:
            base = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/Catalog"
            try:
                search_index = get_json("winget search-index", f"{base}/manifest/search-index.json")
                expected_index_hash = manifest.get("indexSha256")
                if (
                    not isinstance(expected_index_hash, str)
                    or not re.fullmatch(r"[0-9a-f]{64}", expected_index_hash)
                    or hashlib.sha256(
                        json.dumps(search_index, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
                    ).hexdigest()
                    != expected_index_hash
                ):
                    raise ValueError("SHA-256 do índice de busca diverge do manifesto")
                if not isinstance(search_index, list) or len(search_index) != manifest.get("appCount"):
                    raise ValueError("contagem do índice não corresponde ao manifesto")
                payloads["winget"] = search_index
                details = []
                for row in search_index[: min(24, len(search_index))]:
                    detail_path = row.get("detailPath") if isinstance(row, dict) else None
                    if (
                        not isinstance(detail_path, str)
                        or not detail_path.startswith("apps/")
                        or any(segment in ("", ".", "..") for segment in detail_path.split("/"))
                        or not re.fullmatch(r"[0-9a-f]{64}", str(row.get("recordSha256", "")))
                    ):
                        raise ValueError("caminho de detalhe inválido no índice")
                    detail = get_json("winget detail", f"{base}/{detail_path}")
                    actual_detail_hash = hashlib.sha256(
                        json.dumps(detail, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
                    ).hexdigest()
                    if actual_detail_hash != row["recordSha256"]:
                        raise ValueError(f"hash de detalhe incorreto para {row.get('id')}")
                    if (
                        not isinstance(detail, dict)
                        or str(detail.get("id", "")).casefold() != str(row.get("id", "")).casefold()
                    ):
                        raise ValueError(f"detalhe não corresponde ao resumo: {row.get('id')}")
                    details.append(detail)
                payloads["winget_details_sample"] = details
                print(f"JSON OK: winget search-index ({len(search_index)} apps; {len(details)} detalhes amostrados)")
            except (requests.RequestException, UnicodeError, json.JSONDecodeError, ValueError) as exc:
                catalog_errors.append(f"Catálogo WinGet v2: {type(exc).__name__}: {exc}")
                print(f"JSON ERRO: catálogo WinGet v2: {exc}", file=sys.stderr)

    msstore_manifest = payloads.get("microsoft_store")
    if isinstance(msstore_manifest, dict):
        base = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/Catalog/msstore"
        try:
            if msstore_manifest.get("schemaVersion") != 2 or msstore_manifest.get("catalog") != "msstore":
                raise ValueError("manifesto MS Store inválido")
            search_index = get_json("microsoft_store search-index", f"{base}/manifest/search-index.json")
            expected = msstore_manifest.get("indexSha256")
            actual = hashlib.sha256(
                json.dumps(search_index, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
            ).hexdigest()
            if (
                actual != expected
                or not isinstance(search_index, list)
                or len(search_index) != msstore_manifest.get("appCount")
            ):
                raise ValueError("hash/contagem do índice MS Store diverge do manifesto")
            payloads["microsoft_store"] = search_index
            details = []
            for row in search_index[: min(24, len(search_index))]:
                detail_path = row.get("detailPath") if isinstance(row, dict) else None
                if (
                    not isinstance(detail_path, str)
                    or not detail_path.startswith("apps/")
                    or any(segment in ("", ".", "..") for segment in detail_path.split("/"))
                    or not re.fullmatch(r"[0-9a-f]{64}", str(row.get("recordSha256", "")))
                ):
                    raise ValueError("caminho ou hash de detalhe inválido no índice MS Store")
                detail = get_json("microsoft_store detail", f"{base}/{detail_path}")
                detail_hash = hashlib.sha256(
                    json.dumps(detail, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
                ).hexdigest()
                if (
                    detail_hash != row["recordSha256"]
                    or detail.get("id", "").casefold() != row.get("id", "").casefold()
                ):
                    raise ValueError(f"detalhe MS Store divergente para {row.get('id')}")
                details.append(detail)
            payloads["microsoft_store_details_sample"] = details
            print(f"JSON OK: microsoft_store search-index ({len(search_index)} apps)")
        except (requests.RequestException, UnicodeError, json.JSONDecodeError, ValueError) as exc:
            catalog_errors.append(f"Catálogo Microsoft Store: {type(exc).__name__}: {exc}")
            print(f"JSON ERRO: catálogo Microsoft Store: {exc}", file=sys.stderr)

    api_root = payloads.get("installer_api")
    if isinstance(api_root, dict):
        try:
            sources = api_root.get("sources")
            if (
                api_root.get("schemaVersion") != 2
                or api_root.get("catalog") != "installer-api"
                or not isinstance(sources, dict)
                or set(sources) != {"winget", "msstore"}
            ):
                raise ValueError("manifesto raiz da API inválido")
            api_base = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/Api"
            for source, refs in sources.items():
                if (
                    not isinstance(refs, dict)
                    or refs.get("manifestPath") != f"{source}/manifest.json"
                    or refs.get("indexPath") != f"{source}/manifest/search-index.json"
                ):
                    raise ValueError(f"referências da origem {source} inválidas")
                source_manifest = get_json(f"installer_api {source} manifest", f"{api_base}/{refs['manifestPath']}")
                rows = get_json(f"installer_api {source} index", f"{api_base}/{refs['indexPath']}")
                index_hash = hashlib.sha256(
                    json.dumps(rows, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
                ).hexdigest()
                if (
                    not isinstance(source_manifest, dict)
                    or source_manifest.get("schemaVersion") != 2
                    or source_manifest.get("catalog") != "installer-api"
                    or source_manifest.get("source") != source
                    or not isinstance(rows, list)
                    or source_manifest.get("packageCount") != len(rows)
                    or len(rows) < (5000 if source == "winget" else 20)
                    or source_manifest.get("indexSha256") != index_hash
                ):
                    raise ValueError(f"manifesto/índice da origem {source} inválido")
                sample_details = []
                for row in rows[: min(12, len(rows))]:
                    detail_path = row.get("detailPath") if isinstance(row, dict) else None
                    if (
                        not isinstance(row, dict)
                        or row.get("source") != source
                        or not isinstance(detail_path, str)
                        or not detail_path.startswith("apps/")
                        or any(segment in ("", ".", "..") for segment in detail_path.split("/"))
                        or not re.fullmatch(r"[0-9a-f]{64}", str(row.get("recordSha256", "")))
                    ):
                        raise ValueError(f"entrada ou caminho inválido no índice {source}")
                    detail = get_json(f"installer_api {source} detail", f"{api_base}/{source}/{detail_path}")
                    detail_hash = hashlib.sha256(
                        json.dumps(detail, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
                    ).hexdigest()
                    if (
                        detail_hash != row["recordSha256"]
                        or not isinstance(detail, dict)
                        or detail.get("schema") != 2
                        or detail.get("source") != source
                        or str(detail.get("id", "")).casefold() != str(row.get("id", "")).casefold()
                    ):
                        raise ValueError(f"detalhe da API diverge para {row.get('id')}")
                    installers = detail.get("installers", [])
                    if source == "winget" and not installers or source == "msstore" and installers:
                        raise ValueError(f"instaladores incompatíveis com a origem para {row.get('id')}")
                    sample_details.append(detail)
                payloads[f"installer_api_{source}"] = rows
                payloads[f"installer_api_{source}_details"] = sample_details
                print(
                    f"JSON OK: installer_api {source} ({len(rows)} pacotes; {len(sample_details)} detalhes amostrados)"
                )
        except (requests.RequestException, UnicodeError, json.JSONDecodeError, ValueError) as exc:
            catalog_errors.append(f"API própria V2: {type(exc).__name__}: {exc}")
            print(f"JSON ERRO: API própria V2: {exc}", file=sys.stderr)

    errors = catalog_errors + validate_payloads(
        payloads,
        {
            "winget": 5000,
            "microsoft_store": 20,
            "icon_manifest": 100,
        },
    )
    media_map: dict[str, list[str]] = {}
    for name, payload in payloads.items():
        if name == "icon_manifest":
            for app_id, url in payload.items():
                if isinstance(url, str):
                    media_map.setdefault(url, []).append(f"icon_manifest.{app_id}")
        else:
            for location, url in walk_media(payload):
                media_map.setdefault(url, []).append(f"{name}.{location}")
    if not media_map:
        errors.append("Nenhuma URL de mídia foi encontrada nos catálogos consultados")

    urls = sorted(media_map)
    if args.max_media_urls and len(urls) > args.max_media_urls:
        # Rotate the deterministic sample weekly to eventually cover the entire set.
        week = datetime.now(UTC).isocalendar().week
        start = (week * args.max_media_urls) % len(urls)
        urls = (urls + urls)[start : start + args.max_media_urls]

    media_results = []
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        future_map = {pool.submit(check_image, url): url for url in urls}
        for future in as_completed(future_map):
            url, error = future.result()
            media_results.append({"url": url, "sources": media_map[url], "error": error})
    for item in media_results:
        parsed = urlparse(item["url"])
        item["url"] = urlunsplit((parsed.scheme, parsed.netloc, parsed.path, "", ""))
    media_results.sort(key=lambda item: item["url"])
    failures = [item for item in media_results if item["error"]]
    failure_rate = len(failures) / len(media_results) if media_results else 0.0
    if media_results and failure_rate > args.max_failure_rate:
        errors.append(
            f"Mídias indisponíveis: {len(failures)}/{len(media_results)} ({failure_rate:.2%}), limite {args.max_failure_rate:.2%}"
        )

    report = {
        "schemaVersion": 1,
        "generatedAt": datetime.now(UTC).isoformat(),
        "durationSeconds": round(time.perf_counter() - started, 2),
        "catalogs": {
            name: {"items": len(value) if isinstance(value, (list, dict)) else None} for name, value in payloads.items()
        },
        "media": {
            "checked": len(media_results),
            "failed": len(failures),
            "failureRate": failure_rate,
            "sampleLimit": args.max_media_urls,
            "failures": failures,
        },
        "errors": errors,
    }
    args.report_dir.mkdir(parents=True, exist_ok=True)
    (args.report_dir / "health.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    lines = [
        "# Saúde dos catálogos WinProvision",
        "",
        f"Gerado em: {report['generatedAt']}",
        "",
        "## Catálogos",
        "",
        "| Fonte | Entradas |",
        "|---|---:|",
    ]
    lines.extend(
        f"| {name} | {details['items'] if details['items'] is not None else 'inválido'} |"
        for name, details in report["catalogs"].items()
    )
    lines.extend(
        [
            "",
            "## Mídias",
            "",
            f"URLs verificadas: {len(media_results)}",
            f"Falhas: {len(failures)} ({failure_rate:.2%})",
            "",
        ]
    )
    if failures:
        lines.extend(["| URL | Motivo | Referências |", "|---|---|---|"])
        lines.extend(
            f"| {item['url']} | {item['error']} | {', '.join(item['sources'][:3])} |" for item in failures[:500]
        )
    if errors:
        lines.extend(["", "## Erros", "", *[f"- {error}" for error in errors]])
    (args.report_dir / "health.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"Mídia: {len(media_results)} verificadas, {len(failures)} falha(s).")
    if errors:
        print("Validação de saúde falhou; consulte os relatórios anexados.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
