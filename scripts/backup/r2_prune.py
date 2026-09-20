"""
Mantém só os N backups mais recentes sob um prefixo do R2 e apaga o resto.
Usado pelo workflow de backup do repositório pra não deixar os bundles semanais
se acumularem pra sempre.

Uso:
    python r2_prune.py <prefixo/> <quantos_manter>

Os nomes dos backups têm a data no nome (WinProvision-Store-AAAA-MM-DD.bundle),
então a ordem alfabética das chaves já é a ordem cronológica.

Por segurança, só apaga chaves DENTRO do prefixo informado, que precisa terminar
em "/" e ter pelo menos duas pastas (ex.: Backups/Repo/weekly/) — assim um erro de
digitação nunca varre o bucket inteiro.

Env vars: R2_ACCOUNT_ID, R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY, R2_BUCKET
(aqui o R2_BUCKET é obrigatório, sem default, pra nunca cair no bucket público).
"""

import os
import sys

import boto3
from botocore.config import Config


def select_keys_to_delete(keys, keep):
    """Devolve as chaves que devem ser apagadas (tudo menos as `keep` mais novas)."""
    ordered = sorted(keys, reverse=True)
    return ordered[keep:]


def main() -> int:
    if len(sys.argv) != 3:
        print("Uso: python r2_prune.py <prefixo/> <quantos_manter>", file=sys.stderr)
        return 2

    prefix = sys.argv[1]
    try:
        keep = int(sys.argv[2])
    except ValueError:
        print("<quantos_manter> precisa ser um inteiro.", file=sys.stderr)
        return 2

    if keep < 1:
        print("<quantos_manter> precisa ser >= 1.", file=sys.stderr)
        return 2
    if not prefix.endswith("/") or prefix.strip("/").count("/") < 1:
        print(
            "O prefixo precisa terminar em '/' e ter ao menos duas pastas (ex.: Backups/Repo/weekly/).", file=sys.stderr
        )
        return 2

    bucket = os.environ.get("R2_BUCKET")
    if not bucket:
        print("R2_BUCKET não definido.", file=sys.stderr)
        return 2

    client = boto3.client(
        "s3",
        endpoint_url=f"https://{os.environ['R2_ACCOUNT_ID']}.r2.cloudflarestorage.com",
        aws_access_key_id=os.environ["R2_ACCESS_KEY_ID"],
        aws_secret_access_key=os.environ["R2_SECRET_ACCESS_KEY"],
        config=Config(signature_version="s3v4"),
        region_name="auto",
    )

    keys = []
    for page in client.get_paginator("list_objects_v2").paginate(Bucket=bucket, Prefix=prefix):
        keys.extend(obj["Key"] for obj in page.get("Contents", []))

    to_delete = select_keys_to_delete(keys, keep)
    print(f"{len(keys)} backup(s) em '{prefix}'; mantendo {min(keep, len(keys))}, apagando {len(to_delete)}.")

    for key in to_delete:
        client.delete_object(Bucket=bucket, Key=key)
        print(f"  apagado: {key}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
