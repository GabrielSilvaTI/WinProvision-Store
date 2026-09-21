#!/usr/bin/env python3
"""
Sobe os JSONs por pacote da API (Store/Api/v1/packages/) pro R2, mas só os
que mudaram desde a execução anterior. São milhares de objetos pequenos,
então recalcula o sha256 de cada packages/<id>.json local e compara com o
estado da execução anterior (baixado antes do Indexer rodar), em vez de
reenviar tudo todo dia.

O index.json é sempre reenviado (é um objeto só, e muda toda execução).

Uso:
    python upload_api_json.py <dir_local_api> <estado_anterior_json> <r2_prefix>

    dir_local_api      pasta gerada pelo Indexer, com index.json e packages/*.json
    estado_anterior_json  caminho local baixado via r2_download.py (pode não existir
                           ainda, ex.: 1a execução -- nesse caso tudo é enviado)
    r2_prefix          ex.: Store/Api/v1

O novo estado (id -> sha256 de cada pacote enviado nesta execução) é salvo
em <r2_prefix>/_state/package-hashes.json, fora do que o Worker lê, pra
servir de comparação na próxima execução.

Env vars esperadas (as mesmas de r2_upload.py / r2_download.py):
    R2_ACCOUNT_ID, R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY
    R2_BUCKET (opcional, default "winprovision")
"""

import hashlib
import json
import os
import sys

import boto3
from botocore.config import Config


def sha256_of(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def main() -> int:
    if len(sys.argv) != 4:
        print(
            "Uso: python upload_api_json.py <dir_local_api> <estado_anterior_json> <r2_prefix>",
            file=sys.stderr,
        )
        return 2

    local_dir, previous_state_path, r2_prefix = sys.argv[1], sys.argv[2], sys.argv[3]
    r2_prefix = r2_prefix.rstrip("/")

    packages_dir = os.path.join(local_dir, "packages")
    index_path = os.path.join(local_dir, "index.json")

    if not os.path.isdir(packages_dir):
        print(f"Erro: '{packages_dir}' não existe.", file=sys.stderr)
        return 1

    previous_state = {}
    if os.path.isfile(previous_state_path):
        with open(previous_state_path, "r", encoding="utf-8") as f:
            previous_state = json.load(f)

    account_id = os.environ["R2_ACCOUNT_ID"]
    access_key = os.environ["R2_ACCESS_KEY_ID"]
    secret_key = os.environ["R2_SECRET_ACCESS_KEY"]
    bucket = os.environ.get("R2_BUCKET") or "winprovision"

    client = boto3.client(
        "s3",
        endpoint_url=f"https://{account_id}.r2.cloudflarestorage.com",
        aws_access_key_id=access_key,
        aws_secret_access_key=secret_key,
        config=Config(signature_version="s3v4"),
        region_name="auto",
    )

    new_state = {}
    sent = 0
    skipped = 0

    for filename in sorted(os.listdir(packages_dir)):
        if not filename.endswith(".json"):
            continue

        package_id = filename[: -len(".json")]
        local_path = os.path.join(packages_dir, filename)
        digest = sha256_of(local_path)
        new_state[package_id] = digest

        if previous_state.get(package_id) == digest:
            skipped += 1
            continue

        client.upload_file(
            local_path,
            bucket,
            f"{r2_prefix}/packages/{filename}",
            ExtraArgs={"ContentType": "application/json"},
        )
        sent += 1

    if os.path.isfile(index_path):
        client.upload_file(
            index_path,
            bucket,
            f"{r2_prefix}/index.json",
            ExtraArgs={"ContentType": "application/json"},
        )
    else:
        print(f"Aviso: '{index_path}' não existe, index.json não foi atualizado.")

    state_local_path = os.path.join(local_dir, "_package-hashes.json")
    with open(state_local_path, "w", encoding="utf-8") as f:
        json.dump(new_state, f)

    client.upload_file(
        state_local_path,
        bucket,
        f"{r2_prefix}/_state/package-hashes.json",
        ExtraArgs={"ContentType": "application/json"},
    )

    print(f"OK: {sent} pacote(s) enviado(s), {skipped} sem alteração.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
