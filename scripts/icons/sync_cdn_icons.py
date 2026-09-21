#!/usr/bin/env python3
"""Baixa da CDN oficial do winget os ícones que faltam no R2 e sobe para o bucket.

Fluxo:
  1. lê o apps.json do catálogo e lista o que já existe em Store/Icon_Database/;
  2. separa os apps sem ícone, pulando os que já foram testados sem sucesso
     nos últimos --recheck-days dias (Store/Icon_Database/icon-sync-state.json);
  3. para cada um, refaz o caminho do winget (index.db -> versionData ->
     manifest -> Icons), baixa o ícone, confere o SHA256 e valida a imagem;
  4. sobe o arquivo original, sem converter, como <PackageIdentifier em
     minúsculas>.<ext>, sem nunca sobrescrever um ícone que já existe;
  5. grava o relatório e atualiza o estado.

Reaproveita as funções de probe_cdn_icons.py (mesma pasta).
Credenciais: R2_ACCOUNT_ID, R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY, R2_BUCKET.
Para testar sem R2 use --local-store <pasta>.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import sys
import threading
import time
import unicodedata
from collections import Counter, defaultdict
from concurrent.futures import ThreadPoolExecutor
from datetime import date, timedelta
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import probe_cdn_icons as probe  # noqa: E402

ICON_EXTS = {"ico", "png", "jpg", "jpeg", "svg", "gif", "webp"}
FORMAT_EXT = {"ICO": "ico", "PNG": "png", "JPEG": "jpg", "GIF": "gif", "WEBP": "webp"}
CONTENT_TYPES = {
    "ico": "image/x-icon",
    "png": "image/png",
    "jpg": "image/jpeg",
    "gif": "image/gif",
    "webp": "image/webp",
}
DEFAULT_APPS_URL = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/Database/apps.json"
DEFAULT_PREFIX = "Store/Icon_Database/"
DEFAULT_STATE_KEY = "Store/Icon_Database/icon-sync-state.json"

# Sem ícone na CDN: vale lembrar e só tentar de novo depois de --recheck-days.
NEGATIVE_STATUS = {
    "fora_do_indice",
    "sem_hash_no_indice",
    "sem_icons_no_manifest",
    "icons_sem_url_https",
    "versiondata_http_404",
    "manifest_http_404",
}


def is_negative(status: str) -> bool:
    return status in NEGATIVE_STATUS or status.startswith("icone_http_4")


# ---------------------------------------------------------------- armazenamento


class R2Store:
    def __init__(self):
        import boto3
        from botocore.config import Config

        account = os.environ["R2_ACCOUNT_ID"]
        # Usa 'winprovision' como fallback caso R2_BUCKET não esteja definido nas variáveis de ambiente
        self.bucket = os.environ.get("R2_BUCKET") or "winprovision"
        self.s3 = boto3.client(
            "s3",
            endpoint_url=f"https://{account}.r2.cloudflarestorage.com",
            aws_access_key_id=os.environ["R2_ACCESS_KEY_ID"],
            aws_secret_access_key=os.environ["R2_SECRET_ACCESS_KEY"],
            region_name="auto",
            config=Config(retries={"max_attempts": 5, "mode": "standard"}),
        )

    def list_keys(self, prefix):
        pager = self.s3.get_paginator("list_objects_v2")
        for page in pager.paginate(Bucket=self.bucket, Prefix=prefix):
            for obj in page.get("Contents", []):
                yield obj["Key"]

    def get_json(self, key):
        from botocore.exceptions import ClientError

        try:
            body = self.s3.get_object(Bucket=self.bucket, Key=key)["Body"].read()
        except ClientError as exc:
            if exc.response.get("Error", {}).get("Code") in ("NoSuchKey", "404"):
                return None
            raise
        return json.loads(body.decode("utf-8-sig"))

    def put_json(self, key, obj):
        self.put_bytes(
            key, json.dumps(obj, ensure_ascii=False, indent=1).encode("utf-8"), "application/json", cache="no-cache"
        )

    def put_bytes(self, key, data, ctype, cache="public, max-age=86400"):
        self.s3.put_object(Bucket=self.bucket, Key=key, Body=data, ContentType=ctype, CacheControl=cache)


class LocalStore:
    """Mesma interface do R2Store, em disco. Só para testes e simulações."""

    def __init__(self, root):
        self.root = Path(root)
        self.root.mkdir(parents=True, exist_ok=True)

    def list_keys(self, prefix):
        for p in self.root.rglob("*"):
            if p.is_file():
                key = p.relative_to(self.root).as_posix()
                if key.startswith(prefix):
                    yield key

    def get_json(self, key):
        p = self.root / key
        return json.loads(p.read_text(encoding="utf-8")) if p.exists() else None

    def put_json(self, key, obj):
        self.put_bytes(key, json.dumps(obj, ensure_ascii=False, indent=1).encode("utf-8"), "application/json")

    def put_bytes(self, key, data, ctype, cache=""):
        p = self.root / key
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_bytes(data)


# ---------------------------------------------------------------- catálogo


def norm_id(pkg_id: str) -> str:
    return unicodedata.normalize("NFC", pkg_id).lower()


def dotless(key: str) -> str:
    return key.replace(".", "")


def load_catalog(apps_url: str) -> list[str]:
    status, data, err = probe.http_get(apps_url, timeout=90)
    if data is None:
        raise SystemExit(f"Não consegui baixar o apps.json ({err}).")
    ids = probe.dedupe(probe.ids_from_json(json.loads(data.decode("utf-8-sig"))))
    ids = [i for i in ids if "/" not in i and "\\" not in i]
    if not ids:
        raise SystemExit("Nenhum PackageIdentifier reconhecido no apps.json.")
    return ids


def list_existing(store, prefix) -> dict[str, str]:
    """stem em minúsculas -> chave no bucket, só arquivos de imagem."""
    existing = {}
    for key in store.list_keys(prefix):
        name = key[len(prefix) :]
        if not name or "/" in name or "." not in name:
            continue
        stem, ext = name.rsplit(".", 1)
        if ext.lower() in ICON_EXTS:
            existing[norm_id(stem)] = key
    return existing


# ---------------------------------------------------------------- CDN


def validate_image(blob: bytes) -> str:
    from PIL import Image

    img = Image.open(io.BytesIO(blob))
    fmt = img.format
    img.load()
    if fmt not in FORMAT_EXT:
        raise ValueError(f"formato de imagem não aceito: {fmt}")
    if min(img.size) < 16:
        raise ValueError(f"imagem pequena demais: {img.size}")
    return FORMAT_EXT[fmt]


def resolve_icon(pkg: dict, cdn: str) -> dict:
    res = {"id": pkg["id"], "status": "", "detail": "", "url": "", "ext": "", "data": None, "version": ""}

    def fail(status, detail=""):
        res["status"], res["detail"] = status, str(detail)[:240]
        return res

    if not pkg.get("hash"):
        return fail("sem_hash_no_indice")
    status, data, err = probe.http_get(f"{cdn}/packages/{pkg['id']}/{pkg['hash'][:8]}/versionData.mszyml")
    if data is None:
        return fail(f"versiondata_http_{status}" if status else
