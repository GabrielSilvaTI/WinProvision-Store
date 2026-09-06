#!/usr/bin/env python3
"""
Roda DEPOIS que o 'wrangler kv bulk put' terminou com sucesso. Lê o
kv_bulk.json (as chaves que acabaram de ser gravadas nesta rodada),
funde com o kv-synced-manifest.json já publicado no R2 e sobe a versão
atualizada -- pra que a próxima execução do build_manifest_from_r2.py
saiba, com certeza, o que já está no KV.

Se esse passo não rodar (porque o wrangler falhou antes), o
kv-synced-manifest.json fica como estava, e a próxima rodada tenta de
novo as mesmas chaves -- sem nunca fingir que algo foi sincronizado
quando na verdade não foi.
"""
import json
import os
import urllib.request

import boto3

ACCOUNT_ID = os.environ["R2_ACCOUNT_ID"]
ACCESS_KEY_ID = os.environ["R2_ACCESS_KEY_ID"]
SECRET_ACCESS_KEY = os.environ["R2_SECRET_ACCESS_KEY"]

BUCKET = os.environ.get("R2_BUCKET", "winprovision")
KV_SYNCED_KEY = os.environ.get("R2_KV_SYNCED_KEY", "Store/kv-synced-manifest.json")
KV_SYNCED_URL = os.environ.get(
    "R2_KV_SYNCED_URL",
    "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/kv-synced-manifest.json",
)
KEY_PREFIX = "icon:"

ENDPOINT_URL = f"https://{ACCOUNT_ID}.r2.cloudflarestorage.com"


def download_current_synced() -> dict:
    try:
        with urllib.request.urlopen(KV_SYNCED_URL, timeout=15) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except Exception as e:
        print(f"Aviso: kv-synced-manifest.json ainda não existe ou falhou ao baixar ({e}) -- começando vazio")
        return {}


def main():
    with open("kv_bulk.json", "r", encoding="utf-8") as f:
        lote = json.load(f)

    if not lote:
        print("Nenhuma chave nesta rodada, nada a marcar como sincronizado.")
        return

    kv_synced = download_current_synced()

    for entry in lote:
        app_id = entry["key"][len(KEY_PREFIX):]
        kv_synced[app_id] = entry["value"]

    with open("kv-synced-manifest.json", "w", encoding="utf-8") as f:
        json.dump(kv_synced, f, ensure_ascii=False, indent=2, sort_keys=True)

    s3 = boto3.client(
        "s3",
        endpoint_url=ENDPOINT_URL,
        aws_access_key_id=ACCESS_KEY_ID,
        aws_secret_access_key=SECRET_ACCESS_KEY,
        region_name="auto",
    )
    s3.upload_file(
        "kv-synced-manifest.json",
        BUCKET,
        KV_SYNCED_KEY,
        ExtraArgs={"ContentType": "application/json"},
    )

    print(f"Marcadas {len(lote)} chaves como sincronizadas no KV.")
    print(f"kv-synced-manifest.json agora tem {len(kv_synced)} chaves no total, publicado em {KV_SYNCED_KEY}.")


if __name__ == "__main__":
    main()
