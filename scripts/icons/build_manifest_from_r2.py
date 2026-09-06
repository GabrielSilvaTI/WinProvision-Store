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
import urllib.request
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
# manifesto publicado na rodada anterior -- usado só pra saber o que ja
# estava lá e nao precisa ser reenviado ao KV
PUBLISHED_MANIFEST_URL = os.environ.get(
    "R2_PUBLISHED_MANIFEST_URL",
    "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/icon-manifest.json",
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


def load_published_manifest() -> dict:
    """Baixa o manifesto publicado na rodada anterior, pra diff. Se ainda não
    existir (primeira execução) ou falhar, trata como vazio -- nesse caso
    TUDO conta como novo, e o KV bulk put pode estourar o limite diário do
    plano free numa primeira carga grande (esperado só na primeira vez)."""
    try:
        with urllib.request.urlopen(PUBLISHED_MANIFEST_URL, timeout=15) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except Exception as e:
        print(f"Aviso: não consegui baixar o manifesto anterior ({e}) -- tratando tudo como novo")
        return {}


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

    manifest = {}
    conflitos = {}

    for app_id, candidatos in grupos.items():
        if len(candidatos) > 1:
            candidatos.sort(key=ext_rank)
            conflitos[app_id] = candidatos

        escolhido = candidatos[0]
        manifest[app_id] = f"{PUBLIC_BASE}/{escolhido}"

    # manifesto completo -- sempre publicado por inteiro no R2, isso é só
    # um arquivo estático, não conta como operação de KV
    with open("icon-manifest.json", "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2, sort_keys=True)

    # kv_bulk.json -- só as chaves NOVAS ou com URL diferente da última
    # publicação, pra não estourar o limite diário de escritas do KV free
    manifest_anterior = load_published_manifest()

    entries = []
    for app_id, url in manifest.items():
        if manifest_anterior.get(app_id) != url:
            entries.append({"key": f"{KEY_PREFIX}{app_id}", "value": url})

    with open("kv_bulk.json", "w", encoding="utf-8") as f:
        json.dump(entries, f, ensure_ascii=False, indent=2)

    if conflitos:
        with open("conflitos_extensao.json", "w", encoding="utf-8") as f:
            json.dump(conflitos, f, ensure_ascii=False, indent=2)

    print(f"Objetos no bucket: {len(keys)}")
    print(f"Ícones únicos no manifesto: {len(manifest)}")
    print(f"Novos/alterados desde a última publicação (vão pro KV agora): {len(entries)}")
    print(f"Conflitos de extensão: {len(conflitos)}")

    if len(entries) > 1000:
        print(
            f"\nAVISO: {len(entries)} chaves novas/alteradas excede o limite diário "
            "gratuito de 1.000 escritas do Workers KV. O wrangler vai falhar no meio "
            "do bulk put -- rode de novo nos próximos dias pra ir completando aos "
            "poucos (cada rodada só reenvia o que ainda não bateu com o manifesto "
            "publicado), ou considere o plano pago do Workers ($5/mês) pra fazer tudo "
            "de uma vez."
        )


if __name__ == "__main__":
    main()


if __name__ == "__main__":
    main()
