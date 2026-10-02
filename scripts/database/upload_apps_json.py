#!/usr/bin/env python3
"""
Sobe o apps.json gerado pelo WinProvision.Indexer para o bucket R2, em uma
pasta separada da dos ícones (Store/Database/, ao invés de Store/Icon_Database/).

Credenciais vem de variaveis de ambiente (Secrets do GitHub Actions).
"""

import json
import os
import sys

import boto3
from botocore.config import Config
from r2_upload import object_matches, sha256_file
from validate_catalog import validate_catalog

ACCOUNT_ID = os.environ["R2_ACCOUNT_ID"]
ACCESS_KEY_ID = os.environ["R2_ACCESS_KEY_ID"]
SECRET_ACCESS_KEY = os.environ["R2_SECRET_ACCESS_KEY"]

BUCKET = os.environ.get("R2_BUCKET") or "winprovision"
DEST_KEY = os.environ.get("R2_APPS_JSON_KEY", "Store/Database/apps.json")
SOURCE_FILE = os.environ.get("APPS_JSON_SOURCE", "apps.json")
PREVIOUS_FILE = os.environ.get("PREVIOUS_APPS_JSON_PATH")
MIN_COUNT = int(os.environ.get("WINPROVISION_MIN_CATALOG_APPS", "5000"))
MIN_RETENTION = float(os.environ.get("WINPROVISION_MIN_CATALOG_RETENTION", "0.70"))

ENDPOINT_URL = f"https://{ACCOUNT_ID}.r2.cloudflarestorage.com"


def main():
    try:
        validate_catalog(
            SOURCE_FILE,
            PREVIOUS_FILE,
            min_count=MIN_COUNT,
            min_retention=MIN_RETENTION,
        )
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(f"Validação de apps.json falhou; nada foi publicado: {exc}", file=sys.stderr)
        return 1

    s3 = boto3.client(
        "s3",
        endpoint_url=ENDPOINT_URL,
        aws_access_key_id=ACCESS_KEY_ID,
        aws_secret_access_key=SECRET_ACCESS_KEY,
        config=Config(retries={"max_attempts": 5, "mode": "standard"}),
        region_name="auto",
    )
    digest = sha256_file(SOURCE_FILE)
    if object_matches(s3, BUCKET, DEST_KEY, digest):
        print(f"Sem alterações: {DEST_KEY} já contém SHA-256 {digest}; upload ignorado.")
        return 0

    s3.upload_file(
        SOURCE_FILE,
        BUCKET,
        DEST_KEY,
        ExtraArgs={
            "ContentType": "application/json",
            "CacheControl": "public, max-age=60, s-maxage=60, must-revalidate",
            "Metadata": {"sha256": digest},
        },
    )
    print(f"Enviado: {SOURCE_FILE} -> {BUCKET}/{DEST_KEY}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
