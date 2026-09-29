#!/usr/bin/env python3
"""Fail closed when a generated catalog is invalid or unexpectedly incomplete."""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path


def validate_catalog(
    current_path: str | Path,
    previous_path: str | Path | None = None,
    *,
    min_count: int = 1,
    min_retention: float = 0.0,
    expected_source: str | None = None,
) -> tuple[int, int | None]:
    current_file = Path(current_path)
    with current_file.open(encoding="utf-8-sig") as stream:
        current = json.load(stream)
    if not isinstance(current, list):
        raise ValueError("a raiz do catálogo precisa ser uma lista JSON")
    if len(current) < min_count:
        raise ValueError(f"catálogo tem {len(current)} item(ns); mínimo configurado: {min_count}")

    seen: set[str] = set()
    for index, item in enumerate(current):
        if not isinstance(item, dict):
            raise ValueError(f"entrada {index} não é um objeto JSON")
        package_id = item.get("id")
        name = item.get("name")
        source = item.get("source")
        if not isinstance(package_id, str) or not package_id.strip():
            raise ValueError(f"entrada {index} não tem ID válido")
        if not isinstance(name, str) or not name.strip():
            raise ValueError(f"{package_id} não tem nome válido")
        key = package_id.casefold()
        if key in seen:
            raise ValueError(f"ID duplicado no catálogo: {package_id}")
        seen.add(key)
        if expected_source and source != expected_source:
            raise ValueError(f"{package_id} tem source={source!r}; esperado {expected_source!r}")
        if not expected_source and source not in ("winget", "msstore"):
            raise ValueError(f"{package_id} tem source desconhecida: {source!r}")

    previous_count: int | None = None
    if previous_path and Path(previous_path).is_file():
        with Path(previous_path).open(encoding="utf-8-sig") as stream:
            previous = json.load(stream)
        if not isinstance(previous, list):
            raise ValueError("o catálogo anterior não é uma lista JSON; bloqueando publicação")
        previous_count = len(previous)
        threshold = math.ceil(previous_count * min_retention)
        if previous_count > 0 and len(current) < threshold:
            raise ValueError(
                f"catálogo caiu de {previous_count} para {len(current)} item(ns), "
                f"abaixo do mínimo de retenção ({min_retention:.0%}: {threshold}); nada será publicado"
            )

    print(
        json.dumps(
            {
                "status": "valid",
                "count": len(current),
                "previousCount": previous_count,
                "changePercent": (
                    round((len(current) - previous_count) * 100 / previous_count, 2) if previous_count else None
                ),
                "expectedSource": expected_source,
            },
            ensure_ascii=False,
        )
    )
    return len(current), previous_count


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("current", type=Path)
    parser.add_argument("--previous", type=Path)
    parser.add_argument("--min-count", type=int, default=1)
    parser.add_argument("--min-retention", type=float, default=0.0)
    parser.add_argument("--expected-source")
    args = parser.parse_args()
    if args.min_count < 1 or not 0.0 <= args.min_retention <= 1.0:
        parser.error("--min-count deve ser positivo e --min-retention deve ficar entre 0 e 1")
    try:
        validate_catalog(
            args.current,
            args.previous,
            min_count=args.min_count,
            min_retention=args.min_retention,
            expected_source=args.expected_source,
        )
    except (OSError, json.JSONDecodeError, ValueError) as exc:
        print(f"Validação do catálogo falhou; nada será publicado: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
