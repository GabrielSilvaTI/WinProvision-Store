#!/usr/bin/env python3
"""
Sobe o icon-manifest.json gerado por build_manifest_from_r2.py de volta
pro bucket R2. Credenciais vem de variaveis de ambiente.
"""
import os

import boto3

ACCOUNT_ID = os.environ["R2_ACCOUNT_ID"]
ACCESS_KEY_ID = os.environ["R2_ACCESS_KEY_ID"]
SECRET_ACCESS_KEY = os.environ["R2_SECRET_ACCESS_KEY"]

BUCKET = os.environ.get("R2_BUCKET", "winprovision")
DEST_KEY = os.environ.get("R2_MANIFEST_KEY", "Store/icon-manifest.json")

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
        "icon-manifest.json",
        BUCKET,
        DEST_KEY,
        ExtraArgs={"ContentType": "application/json"},
    )
    print(f"Enviado: icon-manifest.json -> {BUCKET}/{DEST_KEY}")


if __name__ == "__main__":
    main()
