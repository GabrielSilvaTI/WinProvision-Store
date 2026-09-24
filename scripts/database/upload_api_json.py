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
from concurrent.futures import ThreadPoolExecutor, as_completed

import boto3
from botocore.config import Config

# Quantidade de uploads simultâneos para maximizar a banda do GitHub Actions
MAX_WORKERS = 32


def sha256_of(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def process_package(filename: str, packages_dir: str, previous_state: dict):
    """
    Função worker: calcula a hash local e verifica se precisa de upload.
    Retorna a tupla (package_id, digest, local_path, needs_upload)
    """
    package_id = filename[: -len(".json")]
    local_path = os.path.join(packages_dir, filename)
    digest = sha256_of(local_path)

    needs_upload = previous_state.get(package_id) != digest
    return package_id, digest, local_path, needs_upload


def upload_single_file(client, local_path: str, bucket: str, r2_key: str) -> bool:
    """Envia um único arquivo para o R2."""
    try:
        client.upload_file(
            local_path,
            bucket,
            r2_key,
            ExtraArgs={"ContentType": "application/json"},
        )
        return True
    except Exception as e:  # noqa: BLE001 - isola falhas por arquivo sem abortar os demais uploads.
        print(f"Erro ao enviar {r2_key}: {e}", file=sys.stderr)
        return False


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
        with open(previous_state_path, encoding="utf-8") as f:
            previous_state = json.load(f)

    account_id = os.environ["R2_ACCOUNT_ID"]
    access_key = os.environ["R2_ACCESS_KEY_ID"]
    secret_key = os.environ["R2_SECRET_ACCESS_KEY"]
    bucket = os.environ.get("R2_BUCKET") or "winprovision"

    # Aumenta o número máximo de conexões no pool do botocore para casar com as threads
    boto_config = Config(signature_version="s3v4", max_pool_connections=MAX_WORKERS)

    client = boto3.client(
        "s3",
        endpoint_url=f"https://{account_id}.r2.cloudflarestorage.com",
        aws_access_key_id=access_key,
        aws_secret_access_key=secret_key,
        config=boto_config,
        region_name="auto",
    )

    all_json_files = [f for f in sorted(os.listdir(packages_dir)) if f.endswith(".json")]

    print(f"Processando {len(all_json_files)} arquivos de pacotes com {MAX_WORKERS} workers...")

    new_state = {}
    to_upload = []

    # 1. Leitura e cálculo de hashes em paralelo
    with ThreadPoolExecutor(max_workers=MAX_WORKERS) as executor:
        futures = [executor.submit(process_package, f, packages_dir, previous_state) for f in all_json_files]
        for future in as_completed(futures):
            package_id, digest, local_path, needs_upload = future.result()
            new_state[package_id] = digest
            if needs_upload:
                to_upload.append((local_path, f"{r2_prefix}/packages/{package_id}.json"))

    sent = 0
    skipped = len(all_json_files) - len(to_upload)

    # 2. Upload paralelo dos arquivos alterados/novos para o Cloudflare R2
    if to_upload:
        print(f"Enviando {len(to_upload)} pacotes atualizados/novos para o R2...")
        with ThreadPoolExecutor(max_workers=MAX_WORKERS) as executor:
            upload_futures = [executor.submit(upload_single_file, client, path, bucket, key) for path, key in to_upload]
            for future in as_completed(upload_futures):
                if future.result():
                    sent += 1

    # 3. Envio do index.json
    if os.path.isfile(index_path):
        client.upload_file(
            index_path,
            bucket,
            f"{r2_prefix}/index.json",
            ExtraArgs={"ContentType": "application/json"},
        )
    else:
        print(f"Aviso: '{index_path}' não existe, index.json não foi atualizado.")

    # 4. Envio do arquivo de estado com as novas hashes
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
