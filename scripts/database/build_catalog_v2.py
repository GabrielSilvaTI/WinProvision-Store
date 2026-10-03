#!/usr/bin/env python3
"""Build the Store's hierarchical catalog from the processed Indexer JSON."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from catalog_v2 import build_catalog_v2


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="apps.json produzido pelo WinProvision.Indexer")
    parser.add_argument("output", type=Path, help="diretório catalog-v2 a gerar")
    parser.add_argument("--source-revision", help="commit do repositório winget-pkgs usado nesta geração")
    parser.add_argument("--build-revision", help="commit do WinProvision que gerou o catálogo")
    parser.add_argument("--msstore-sha256", help="SHA-256 do catálogo de entradas Microsoft Store usado")
    parser.add_argument("--media-index", type=Path, help="índice de assets já associados aos IDs dos apps")
    args = parser.parse_args()
    try:
        manifest = build_catalog_v2(
            args.source,
            args.output,
            source_revision=args.source_revision,
            build_revision=args.build_revision,
            msstore_sha256=args.msstore_sha256,
            media_index_file=args.media_index,
        )
    except (OSError, UnicodeError, json.JSONDecodeError, ValueError) as exc:
        print(f"Falha ao gerar catálogo hierárquico; saída anterior preservada: {exc}", file=sys.stderr)
        return 1
    print(
        f"Catálogo v2 válido: {manifest['appCount']} apps, "
        f"{len(manifest['prefixes'])} prefixos, SHA-256 {manifest['catalogSha256']}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
