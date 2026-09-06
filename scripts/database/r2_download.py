"""
Baixa um objeto do R2 pra um caminho local. Usado para restaurar o
metrics-cache.json entre execuções diárias do workflow (nada disso passa
pelo Git — é só R2 <-> disco efêmero do runner).

Uso:
    python r2_download.py <r2_key> <caminho_local>

Se o objeto não existir no R2 ainda (ex.: primeira execução), o script
não falha: só avisa e segue em frente sem criar o arquivo local, deixando
o Indexer rodar com cache vazio (comportamento atual, sem regressão).

Env vars esperadas (as mesmas já usadas pelo upload_apps_json.py):
    R2_ACCOUNT_ID, R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY
    R2_BUCKET (opcional, default "winprovision")
"""

import os
import sys

import boto3
from botocore.exceptions import ClientError
from botocore.config import Config


def main() -> int:
    if len(sys.argv) != 3:
        print("Uso: python r2_download.py <r2_key> <caminho_local>", file=sys.stderr)
        return 2

    r2_key, local_path = sys.argv[1], sys.argv[2]

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

    os.makedirs(os.path.dirname(local_path) or ".", exist_ok=True)

    try:
        client.download_file(bucket, r2_key, local_path)
        print(f"OK: baixado '{r2_key}' -> '{local_path}'")
    except ClientError as e:
        code = e.response.get("Error", {}).get("Code", "")
        if code in ("404", "NoSuchKey"):
            print(f"Aviso: '{r2_key}' ainda não existe no R2 (provável 1a execução) — seguindo sem cache.")
        else:
            raise

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
