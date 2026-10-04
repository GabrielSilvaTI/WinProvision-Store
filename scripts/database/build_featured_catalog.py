#!/usr/bin/env python3
"""Build a compact, category-diverse featured catalog from V2 indexes and install metrics."""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
import unicodedata
from datetime import UTC, datetime
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

WINDOW_DAYS = 30
CATEGORIES = {
    "productivity": ("Produtividade", ("productivity", "office", "business", "calendar", "note", "document")),
    "development": ("Desenvolvimento", ("development", "developer", "programming", "code", "ide", "git")),
    "utilities": ("Ferramentas", ("utility", "utilities", "tool", "system", "terminal", "file manager")),
    "multimedia": ("Multimídia", ("multimedia", "photo", "video", "music", "audio", "image", "media")),
    "security": ("Segurança", ("security", "antivirus", "privacy", "password", "firewall", "vpn")),
    "communication": ("Comunicação", ("communication", "chat", "messaging", "meeting", "voip", "social")),
    "gaming": ("Jogos", ("game", "gaming", "launcher", "steam", "xbox")),
}


def normalized(value: object) -> str:
    text = unicodedata.normalize("NFKD", str(value or "").casefold())
    text = "".join(char for char in text if not unicodedata.combining(char))
    return re.sub(r"[^a-z0-9]+", " ", text).strip()


def load_rows(path: Path | None) -> list[dict]:
    if path is None or not path.is_file():
        return []
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(value, list):
        raise ValueError(f"Índice precisa ser uma lista: {path}")
    return [item for item in value if isinstance(item, dict)]


def load_install_counts(url: str | None) -> dict[tuple[str, str], int]:
    if not url:
        print("Métricas de instalações indisponíveis; ranking usará qualidade e avaliações.")
        return {}
    try:
        request = Request(url, headers={"Accept": "application/json", "User-Agent": "WinProvision-Catalog/1.0"})
        with urlopen(request, timeout=20) as response:
            document = json.load(response)
        if not isinstance(document, dict):
            raise ValueError("resposta de métricas não é um objeto JSON")
        if document.get("schemaVersion") != 1 or document.get("windowDays") != WINDOW_DAYS:
            raise ValueError("contrato de métricas incompatível")
        result = {}
        for row in document.get("installs", []):
            if not isinstance(row, dict):
                continue
            package_id = row.get("id")
            source = str(row.get("source", "")).casefold()
            count = row.get("installCount30d")
            if isinstance(package_id, str) and source in {"winget", "msstore"} and type(count) is int and count > 0:
                result[(source, package_id.casefold())] = count
        print(f"Métricas carregadas: {len(result)} pacote(s) com instalações nos últimos {WINDOW_DAYS} dias.")
        return result
    except (HTTPError, URLError, TimeoutError, OSError, ValueError, json.JSONDecodeError) as exc:
        print(f"Aviso: métricas indisponíveis ({type(exc).__name__}: {exc}); usando sinais do catálogo.", file=sys.stderr)
        return {}


def category_for(row: dict) -> str | None:
    media = row.get("media") if isinstance(row.get("media"), dict) else {}
    values = [row.get("name"), row.get("description"), row.get("storeCategory"), row.get("storeSubCategory")]
    values.extend(row.get("tags", []) if isinstance(row.get("tags"), list) else [])
    text = normalized(" ".join(str(value) for value in values if value))
    for key, (_, keywords) in CATEGORIES.items():
        if any(normalized(keyword) in text for keyword in keywords):
            return key
    return None


def product_key(row: dict) -> str:
    name = normalized(row.get("name"))
    publisher = normalized(row.get("publisher"))
    return f"{name}\0{publisher}" if name and publisher else f"{row.get('source')}\0{row.get('id')}"


def eligible(row: dict) -> bool:
    package_id, name, publisher = row.get("id"), row.get("name"), row.get("publisher")
    source = str(row.get("source", "")).casefold()
    if (
        not isinstance(package_id, str)
        or not package_id
        or len(package_id) > 128
        or not package_id[0].isalnum()
        or any(not char.isprintable() or char.isspace() or char in "/\\" for char in package_id)
        or any(part in {"", ".", ".."} for part in package_id.split("."))
    ):
        return False
    if source not in {"winget", "msstore"} or not isinstance(name, str) or len(name.strip()) < 2:
        return False
    if not isinstance(publisher, str) or not publisher.strip():
        return False
    regions = row.get("regionTags")
    return not isinstance(regions, list) or not regions or any(str(tag).casefold() == "br" for tag in regions)


def score_row(row: dict, count: int, max_count: int, max_reviews: int) -> float:
    rating = row.get("storeRating") if isinstance(row.get("storeRating"), (int, float)) else 0.0
    reviews = row.get("storeRatingCount") if isinstance(row.get("storeRatingCount"), int) else 0
    rating = min(5.0, max(0.0, float(rating)))
    reviews = max(0, reviews)
    bayesian_rating = ((rating * reviews) + (4.0 * 1000.0)) / (reviews + 1000.0) / 5.0
    rating_volume = math.log1p(reviews) / math.log1p(max(1, max_reviews))
    rating_signal = 0.7 * bayesian_rating + 0.3 * rating_volume
    install_signal = math.log1p(count) / math.log1p(max(1, max_count))
    catalog_score = row.get("score") if isinstance(row.get("score"), (int, float)) else 0.0
    catalog_signal = min(1.0, max(0.0, float(catalog_score) / 100.0))
    media = row.get("media") if isinstance(row.get("media"), dict) else {}
    has_icon = bool(media.get("icon"))
    has_visuals = bool(media.get("banner") or media.get("screenshots"))
    pt_br = str(row.get("packageLocale", "")).casefold() == "pt-br"
    metadata_signal = (0.4 if has_icon else 0.0) + (0.35 if has_visuals else 0.0) + (0.25 if pt_br else 0.0)
    return 0.52 * install_signal + 0.23 * rating_signal + 0.17 * catalog_signal + 0.08 * metadata_signal


def reference(row: dict, count: int, category: str | None) -> dict:
    return {
        "id": row["id"],
        "source": str(row["source"]).casefold(),
        "name": row["name"],
        "publisher": row["publisher"],
        "installCount30d": count,
        "category": category,
    }


def build(winget_path: Path, msstore_path: Path | None, output_path: Path, metrics_url: str | None) -> dict:
    rows = load_rows(winget_path) + load_rows(msstore_path)
    rows = [row for row in rows if eligible(row)]
    if not rows:
        raise ValueError("Nenhum aplicativo válido nos índices V2.")

    counts = load_install_counts(metrics_url)
    max_count = max(counts.values(), default=0)
    max_reviews = max(
        (row.get("storeRatingCount", 0) for row in rows if isinstance(row.get("storeRatingCount"), int)),
        default=0,
    )
    candidates = []
    seen: dict[str, dict] = {}
    for row in rows:
        source = str(row["source"]).casefold()
        count = counts.get((source, row["id"].casefold()), 0)
        category = category_for(row)
        identity = product_key(row)
        candidate = {"row": row, "count": count, "category": category, "score": score_row(row, count, max_count, max_reviews)}
        previous = seen.get(identity)
        if previous is None or (candidate["score"], source == "winget") > (previous["score"], previous["row"]["source"] == "winget"):
            seen[identity] = candidate
    candidates = sorted(seen.values(), key=lambda item: (-item["score"], -item["count"], normalized(item["row"].get("name"))))

    hero_candidates = [item for item in candidates if isinstance(item["row"].get("media"), dict) and item["row"]["media"].get("banner")]
    hero: list[dict] = []
    hero_categories: set[str] = set()
    for item in hero_candidates:
        category = item["category"] or "general"
        if category in hero_categories:
            continue
        hero.append(reference(item["row"], item["count"], item["category"]))
        hero_categories.add(category)
        if len(hero) == 3:
            break

    popular = [reference(item["row"], item["count"], item["category"]) for item in candidates[:24]]
    featured_categories = []
    for category_id, (title, _keywords) in CATEGORIES.items():
        selected = [item for item in candidates if item["category"] == category_id][:4]
        if selected:
            featured_categories.append({
                "id": category_id,
                "title": title,
                "apps": [reference(item["row"], item["count"], category_id) for item in selected],
            })

    document = {
        "schemaVersion": 1,
        "generatedUtc": datetime.now(UTC).isoformat().replace("+00:00", "Z"),
        "metricWindowDays": WINDOW_DAYS,
        "installMetricsAvailable": bool(counts),
        "hero": hero,
        "popular": popular,
        "categories": featured_categories,
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    temporary = output_path.with_suffix(output_path.suffix + ".tmp")
    temporary.write_text(json.dumps(document, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    temporary.replace(output_path)
    return document


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--winget-index", type=Path, required=True)
    parser.add_argument("--msstore-index", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--metrics-url", default=None)
    args = parser.parse_args()
    try:
        document = build(args.winget_index, args.msstore_index, args.output, args.metrics_url)
    except (OSError, UnicodeError, json.JSONDecodeError, ValueError) as exc:
        print(f"Catálogo de destaques não gerado: {exc}", file=sys.stderr)
        return 1
    print(
        f"Destaques publicados: {len(document['hero'])} banners, {len(document['popular'])} populares, "
        f"{len(document['categories'])} categorias; métricas: {'ativas' if document['installMetricsAvailable'] else 'indisponíveis'}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
