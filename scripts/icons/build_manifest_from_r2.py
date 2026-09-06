#!/usr/bin/env python3
"""
Versao do build_kv_mapping.py pensada pra rodar no GitHub Actions: em vez
de ler uma pasta local, lista os objetos direto do bucket R2 (prefixo
Store/Icon_Database/) e gera:

  - kv_bulk.json           -> formato de bulk upload do wrangler (SÓ o lote
                               desta rodada, limitado a MAX_KV_WRITES_PER_RUN)
  - icon-manifest.json     -> {id: url} completo, sempre republicado no R2
                               pro app consumir (não usado pra diff)

Credenciais e configuracao vem de variaveis de ambiente (setadas pelo
workflow a partir de Secrets do repositorio), nunca hardcoded.

IMPORTANTE: o diff usa kv-synced-manifest.json (não icon-manifest.json).
Esse segundo arquivo só é atualizado por mark_kv_synced.py, e só DEPOIS
que o wrangler confirmar que a gravação no KV deu certo. Isso evita que
uma tentativa que falhou no meio do caminho faça a próxima rodada achar
que já sincronizou tudo (falso negativo no diff).

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
# manifesto PÚBLICO, sempre republicado por inteiro -- é o que o app usa
# pra resolver URL de ícone. NÃO é usado pra decidir o que vai pro KV.
PUBLIC_MANIFEST_URL = os.environ.get(
    "R2_PUBLIC_MANIFEST_URL",
    "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/icon-manifest.json",
)
# rastreamento do que JÁ foi confirmado gravado no KV -- só é atualizado
# por mark_kv_synced.py depois de um bulk put com sucesso. É ISSO que usamos
# pro diff, não o icon-manifest.json público.
KV_SYNCED_URL = os.environ.get(
    "R2_KV_SYNCED_URL",
    "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/kv-synced-manifest.json",
)
KEY_PREFIX = "icon:"

# teto de segurança por rodada -- nunca manda mais que isso pro wrangler,
# mesmo que o diff calcule um número maior (protege contra qualquer
# divergência no diff e contra a primeira carga gigante). Fica com folga
# do limite de 1.000/dia do plano free.
MAX_KV_WRITES_PER_RUN = int(os.environ.get("MAX_KV_WRITES_PER_RUN", "900"))

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


def download_json(url: str, label: str) -> dict:
    """Baixa um JSON dict do R2. Se ainda não existir (404, primeira vez) ou
    falhar, trata como vazio -- é esperado só na primeira execução."""
    try:
        with urllib.request.urlopen(url, timeout=15) as resp:
            data = json.loads(resp.read().decode("utf-8"))
            print(f"[{label}] baixado com sucesso: {len(data)} chaves em {url}")
            return data
    except Exception as e:
        print(f"[{label}] aviso: não consegui baixar ({e}) -- tratando como vazio ({url})")
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
    # um arquivo estático, não conta como operação de KV, e não é usado
    # pra decidir o que vai pro KV (ver KV_SYNCED_URL acima)
    with open("icon-manifest.json", "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2, sort_keys=True)

    # diff contra o que JÁ foi confirmado no KV (não contra o manifesto público)
    kv_synced = download_json(KV_SYNCED_URL, "kv-synced")

    pendentes = [
        {"key": f"{KEY_PREFIX}{app_id}", "value": url}
        for app_id, url in manifest.items()
        if kv_synced.get(app_id) != url
    ]

    total_pendentes = len(pendentes)
    lote = pendentes[:MAX_KV_WRITES_PER_RUN]

    with open("kv_bulk.json", "w", encoding="utf-8") as f:
        json.dump(lote, f, ensure_ascii=False, indent=2)

    if conflitos:
        with open("conflitos_extensao.json", "w", encoding="utf-8") as f:
            json.dump(conflitos, f, ensure_ascii=False, indent=2)

    print(f"Objetos no bucket: {len(keys)}")
    print(f"Ícones únicos no manifesto: {len(manifest)}")
    print(f"Pendentes de sincronizar no KV (total): {total_pendentes}")
    print(f"Indo pro KV nesta rodada (limitado a {MAX_KV_WRITES_PER_RUN}): {len(lote)}")
    print(f"Conflitos de extensão: {len(conflitos)}")

    restantes = total_pendentes - len(lote)
    if restantes > 0:
        print(
            f"\nAVISO: ainda restam {restantes} chaves pendentes após esta rodada -- "
            "o cron do dia seguinte continua de onde parou automaticamente."
        )


if __name__ == "__main__":
    main()
