"""
Sobe um arquivo local pra uma chave qualquer do R2. Genérico de propósito
(ao contrário do upload_apps_json.py, que é dedicado ao apps.json) — usado
aqui pra devolver o metrics-cache.json depois de cada execução do Indexer.

Uso:
    python r2_upload.py <caminho_local> <r2_key>

Env vars esperadas (as mesmas já usadas pelo upload_apps_json.py):
    R2_ACCOUNT_ID, R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY
    R2_BUCKET (opcional, default "winprovision")
"""

import os
import sys

import boto3
from botocore.config import Config


def main() -> int:
    if len(sys.argv) != 3:
        print("Uso: python r2_upload.py <caminho_local> <r2_key>", file=sys.stderr)
        return 2

    local_path, r2_key = sys.argv[1], sys.argv[2]

    if not os.path.isfile(local_path):
        print(f"Aviso: '{local_path}' não existe — nada pra subir, seguindo sem erro.")
        return 0

    account_id = os.environ["R2_ACCOUNT_ID"]
    access_key = os.environ["R2_ACCESS_KEY_ID"]
    secret_key = os.environ["R2_SECRET_ACCESS_KEY"]
    bucket = os.environ.get("R2_BUCKET", "winprovision")

    client = boto3.client(
        "s3",
        endpoint_url=f"https://{account_id}.r2.cloudflarestorage.com",
        aws_access_key_id=access_key,
        aws_secret_access_key=secret_key,
        config=Config(signature_version="s3v4"),
        region_name="auto",
    )

    client.upload_file(local_path, bucket, r2_key)
    print(f"OK: subido '{local_path}' -> '{r2_key}'")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
