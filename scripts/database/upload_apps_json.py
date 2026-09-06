#!/usr/bin/env python3
"""
Sobe o apps.json gerado pelo WinProvision.Indexer para o bucket R2, em uma
pasta separada da dos ícones (Store/Database/, ao invés de Store/Icon_Database/).

Credenciais vem de variaveis de ambiente (Secrets do GitHub Actions).
"""
import os

import boto3

ACCOUNT_ID = os.environ["R2_ACCOUNT_ID"]
ACCESS_KEY_ID = os.environ["R2_ACCESS_KEY_ID"]
SECRET_ACCESS_KEY = os.environ["R2_SECRET_ACCESS_KEY"]

BUCKET = os.environ.get("R2_BUCKET", "winprovision")
DEST_KEY = os.environ.get("R2_APPS_JSON_KEY", "Store/Database/apps.json")
SOURCE_FILE = os.environ.get("APPS_JSON_SOURCE", "apps.json")

ENDPOINT_URL = f"https://{ACCOUNT_ID}.r2.cloudflarestorage.com"


def main():
    s3 = boto3.client(
        "s3",
        endpoint_url=ENDPOINT_URL,
        aws_access_key_id=ACCESS_KEY_ID,
        aws_secret_access_key=SECRET_ACCESS_KEY,
        region_name="auto",
    )
    s3.upload_file(
        SOURCE_FILE,
        BUCKET,
        DEST_KEY,
        ExtraArgs={"ContentType": "application/json"},
    )
    print(f"Enviado: {SOURCE_FILE} -> {BUCKET}/{DEST_KEY}")


if __name__ == "__main__":
    main()
