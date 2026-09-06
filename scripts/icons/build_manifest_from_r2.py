#!/usr/bin/env python3
"""
Versao do build_kv_mapping.py pensada pra rodar no GitHub Actions: em vez
de ler uma pasta local, lista os objetos direto do bucket R2 (prefixo
Store/Icon_Database/) e gera:

  - kv_bulk.json      -> formato de bulk upload do wrangler
  - icon-manifest.json -> {id: url}, pra publicar de volta no R2

Credenciais e configuracao vem de variaveis de ambiente (setadas pelo
workflow a partir de Secrets do repositorio), nunca hardcoded.

Uso (local, pra testar):
    set R2_ACCOUNT_ID=...
    set R2_ACCESS_KEY_ID=...
    set R2_SECRET_ACCESS_KEY=...
    python build_manifest_from_r2.py
"""
import json
import os
import sys
from collections import defaultdict
from pathlib import PurePosixPath

import boto3

ACCOUNT_ID = os.environ["R2_ACCOUNT_ID"]
ACCESS_KEY_ID = os.environ["R2_ACCESS_KEY_ID"]
SECRET_ACCESS_KEY = os.environ["R2_SECRET_ACCESS_KEY"]

BUCKET = os.environ.get("R2_BUCKET", "winprovision")
PREFIX = os.environ.get("R2_ICON_PREFIX", "Store/Icon_Database/")
PUBLIC_BASE = os.environ.get(
    "R2_PUBLIC_BASE",
    "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/Icon_Database",
)
KEY_PREFIX = "icon:"

ENDPOINT_URL = f"https://{ACCOUNT_ID}.r2.cloudflarestorage.com"

EXT_PRIORITY = [".png", ".webp", ".jpg", ".jpeg", ".gif", ".bmp", ".svg", ".ico"]


def ext_rank(name: str) -> int:
    ext = PurePosixPath(name).suffix.lower()
    return EXT_PRIORITY.index(ext) if ext in EXT_PRIORITY else len(EXT_PRIORITY)


def list_bucket_objects(s3):
    paginator = s3.get_paginator("list_objects_v2")
    for page in paginator.paginate(Bucket=BUCKET, Prefix=PREFIX):
        for obj in page.get("Contents", []):
            yield obj["Key"]


def main():
    s3 = boto3.client(
        "s3",
        endpoint_url=ENDPOINT_URL,
        aws_access_key_id=ACCESS_KEY_ID,
        aws_secret_access_key=SECRET_ACCESS_KEY,
        region_name="auto",
    )

    keys = list(list_bucket_objects(s3))
    if not keys:
        print(f"Nenhum objeto encontrado em {BUCKET}/{PREFIX}")
        sys.exit(1)

    # agrupa por app_id (nome do arquivo sem extensao, minusculo)
    grupos = defaultdict(list)
    for key in keys:
        filename = PurePosixPath(key).name
        app_id = PurePosixPath(filename).stem.lower()
        grupos[app_id].append(filename)

    entries = []
    conflitos = {}

    for app_id, candidatos in grupos.items():
        if len(candidatos) > 1:
            candidatos.sort(key=ext_rank)
            conflitos[app_id] = candidatos

        escolhido = candidatos[0]
        url = f"{PUBLIC_BASE}/{escolhido}"
        entries.append({"key": f"{KEY_PREFIX}{app_id}", "value": url})

    with open("kv_bulk.json", "w", encoding="utf-8") as f:
        json.dump(entries, f, ensure_ascii=False, indent=2)

    manifest = {e["key"][len(KEY_PREFIX):]: e["value"] for e in entries}
    with open("icon-manifest.json", "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2, sort_keys=True)

    if conflitos:
        with open("conflitos_extensao.json", "w", encoding="utf-8") as f:
            json.dump(conflitos, f, ensure_ascii=False, indent=2)

    print(f"Objetos no bucket: {len(keys)}")
    print(f"Ícones únicos mapeados: {len(entries)}")
    print(f"Conflitos de extensão: {len(conflitos)}")


if __name__ == "__main__":
    main()
