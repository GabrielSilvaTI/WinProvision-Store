#!/usr/bin/env python3
"""Publish validated installer API manifests to R2.

Immutable, content-addressed manifest objects are uploaded first. The public
index is the commit point and is published only after every referenced object
(and the legacy compatibility objects) has been uploaded successfully.
"""

import hashlib
import json
import math
import os
import re
import sys
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime
from urllib.parse import quote

import boto3
from botocore.config import Config
from r2_upload import object_matches

MAX_WORKERS = 32
CACHE_CONTROL = "public, max-age=60, s-maxage=60, must-revalidate"
SHA256_RE = re.compile(r"^[0-9a-fA-F]{64}$")
INVALID_ID_CHARS = set('\\/:*?"<>|\0')


def sha256_of(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def validate_api(local_dir: str):
    packages_dir = os.path.join(local_dir, "packages")
    index_path = os.path.join(local_dir, "index.json")
    if not os.path.isdir(packages_dir) or not os.path.isfile(index_path):
        raise ValueError("A pasta da API deve conter index.json e packages/.")

    with open(index_path, encoding="utf-8") as stream:
        index = json.load(stream)
    if not isinstance(index, dict) or index.get("schema") != 1:
        raise ValueError("schema de index.json inválido ou incompatível.")
    packages = index.get("packages")
    if not isinstance(packages, list) or index.get("count") != len(packages):
        raise ValueError("count do index.json não corresponde à lista packages.")
    minimum = int(os.environ.get("WINPROVISION_MIN_API_PACKAGES", "5000"))
    if len(packages) < minimum:
        raise ValueError(f"índice tem {len(packages)} pacote(s); mínimo configurado: {minimum}.")
    generated_at = index.get("generatedAt")
    if not isinstance(generated_at, str):
        raise ValueError("generatedAt ausente no index.json.")
    try:
        datetime.fromisoformat(generated_at.replace("Z", "+00:00"))
    except ValueError as exc:
        raise ValueError("generatedAt inválido no index.json.") from exc

    seen_ids = set()
    digests = {}
    expected_files = set()
    for entry in packages:
        if not isinstance(entry, dict):
            raise ValueError("Entrada inválida em packages do índice.")
        package_id = entry.get("id")
        version = entry.get("version", "")
        source = entry.get("source", "winget")
        if not isinstance(package_id, str) or not package_id.strip():
            raise ValueError("Há um pacote sem ID no índice.")
        if any(char in INVALID_ID_CHARS for char in package_id):
            raise ValueError(f"ID inválido para caminho: {package_id!r}.")
        if not isinstance(version, str) or not isinstance(source, str):
            raise ValueError(f"Versão/origem inválida para {package_id}.")
        if source not in ("winget", "msstore"):
            raise ValueError(f"Origem não reconhecida em {package_id}: {source!r}.")
        key = package_id.casefold()
        if key in seen_ids:
            raise ValueError(f"ID duplicado no índice (ignorando maiúsculas): {package_id}.")
        seen_ids.add(key)

        filename = package_id + ".json"
        expected_files.add(filename)
        manifest_path = os.path.join(packages_dir, filename)
        if not os.path.isfile(manifest_path):
            raise ValueError(f"Manifesto referenciado está ausente: {filename}.")
        with open(manifest_path, encoding="utf-8") as stream:
            manifest = json.load(stream)
        if not isinstance(manifest, dict) or manifest.get("schema") != 1:
            raise ValueError(f"schema inválido no manifesto {filename}.")
        manifest_id = manifest.get("id")
        if not isinstance(manifest_id, str) or manifest_id.casefold() != package_id.casefold():
            raise ValueError(f"ID do manifesto não corresponde ao índice: {filename}.")
        if manifest.get("version", "") != version:
            raise ValueError(f"Versão do manifesto não corresponde ao índice: {filename}.")
        if manifest.get("source", "winget") != source:
            raise ValueError(f"Origem do manifesto não corresponde ao índice: {filename}.")

        installers = manifest.get("installers", [])
        if not isinstance(installers, list):
            raise ValueError(f"installers inválido em {filename}.")
        if source == "winget" and not installers:
            raise ValueError(f"Pacote WinGet sem instaladores: {filename}.")
        if source == "msstore" and installers:
            raise ValueError(f"Manifesto Microsoft Store não deve conter instaladores: {filename}.")
        for installer in installers:
            if not isinstance(installer, dict):
                raise ValueError(f"Instalador inválido em {filename}.")
            url = installer.get("url")
            if not isinstance(url, str) or not url.lower().startswith("https://"):
                raise ValueError(f"Instalador sem URL HTTPS em {filename}.")
            digest = installer.get("sha256")
            if digest is not None and (not isinstance(digest, str) or not SHA256_RE.fullmatch(digest)):
                raise ValueError(f"SHA-256 inválido em {filename}.")
            if installer.get("silentSupported") is True and not SHA256_RE.fullmatch(digest or ""):
                raise ValueError(f"Instalador silencioso sem SHA-256 válido em {filename}.")

        digests[package_id] = sha256_of(manifest_path)

    actual_files = {name for name in os.listdir(packages_dir) if name.endswith(".json")}
    if actual_files != expected_files:
        extra = sorted(actual_files - expected_files)
        missing = sorted(expected_files - actual_files)
        raise ValueError(f"Arquivos fora do índice; extras={extra[:5]}, ausentes={missing[:5]}.")

    return index, digests


def upload_single_file(client, local_path: str, bucket: str, r2_key: str) -> bool:
    try:
        digest = sha256_of(local_path)
        client.upload_file(
            local_path,
            bucket,
            r2_key,
            ExtraArgs={
                "ContentType": "application/json",
                "CacheControl": CACHE_CONTROL,
                "Metadata": {"sha256": digest},
            },
        )
        return True
    except Exception as exc:  # noqa: BLE001 - coleta falhas para não ativar uma publicação parcial.
        print(f"Erro ao enviar {r2_key}: {exc}", file=sys.stderr)
        return False


def upload_batch(client, bucket: str, files: list[tuple[str, str]], label: str) -> bool:
    if not files:
        return True
    failures = []
    print(f"Enviando {len(files)} arquivo(s) ({label}) com {MAX_WORKERS} workers...")
    with ThreadPoolExecutor(max_workers=MAX_WORKERS) as executor:
        futures = {executor.submit(upload_single_file, client, path, bucket, key): key for path, key in files}
        for future in as_completed(futures):
            if not future.result():
                failures.append(futures[future])
    if failures:
        print(
            f"Publicação abortada: {len(failures)} upload(s) falharam; index.json não foi ativado. "
            f"Exemplos: {failures[:5]}",
            file=sys.stderr,
        )
        return False
    return True


def load_previous_state(path: str):
    if not os.path.isfile(path):
        return {}, False
    try:
        with open(path, encoding="utf-8") as stream:
            state = json.load(stream)
    except (OSError, json.JSONDecodeError):
        print("Estado anterior ausente/corrompido; todos os manifestos serão publicados.")
        return {}, False

    if isinstance(state, dict) and state.get("schema") == 2 and isinstance(state.get("packages"), dict):
        return state["packages"], True
    # Estado legado: hashes anteriores ainda servem para decidir se o alias mutável
    # precisa ser atualizado, mas os objetos imutáveis ainda precisam ser criados.
    return state if isinstance(state, dict) else {}, False


def main() -> int:
    if len(sys.argv) != 4:
        print(
            "Uso: python upload_api_json.py <dir_local_api> <estado_anterior_json> <r2_prefix>",
            file=sys.stderr,
        )
        return 2

    local_dir, previous_state_path, r2_prefix = sys.argv[1], sys.argv[2], sys.argv[3].rstrip("/")
    try:
        index, digests = validate_api(local_dir)
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(f"Validação da API falhou; nada foi publicado: {exc}", file=sys.stderr)
        return 1

    previous_hashes, has_immutable_state = load_previous_state(previous_state_path)
    minimum_retention = float(os.environ.get("WINPROVISION_MIN_API_RETENTION", "0.70"))
    if not 0.0 <= minimum_retention <= 1.0:
        print("WINPROVISION_MIN_API_RETENTION precisa ficar entre 0 e 1.", file=sys.stderr)
        return 2
    previous_count = len(previous_hashes)
    current_count = len(index["packages"])
    if previous_count and current_count < math.ceil(previous_count * minimum_retention):
        print(
            f"Validação da API falhou; nada foi publicado: índice caiu de {previous_count} para "
            f"{current_count} pacote(s), abaixo da retenção mínima ({minimum_retention:.0%}).",
            file=sys.stderr,
        )
        return 1
    account_id = os.environ["R2_ACCOUNT_ID"]
    access_key = os.environ["R2_ACCESS_KEY_ID"]
    secret_key = os.environ["R2_SECRET_ACCESS_KEY"]
    bucket = os.environ.get("R2_BUCKET") or "winprovision"
    client = boto3.client(
        "s3",
        endpoint_url=f"https://{account_id}.r2.cloudflarestorage.com",
        aws_access_key_id=access_key,
        aws_secret_access_key=secret_key,
        config=Config(signature_version="s3v4", max_pool_connections=MAX_WORKERS),
        region_name="auto",
    )

    packages_dir = os.path.join(local_dir, "packages")
    immutable_uploads = []
    legacy_uploads = []
    published_index = json.loads(json.dumps(index))
    for entry in published_index["packages"]:
        package_id = entry["id"]
        digest = digests[package_id]
        filename = package_id + ".json"
        manifest_path = os.path.join(packages_dir, filename)
        encoded_id = quote(package_id, safe="")
        entry["manifestPath"] = f"packages/{encoded_id}/{digest}.json"
        entry["manifestSha256"] = digest

        if not has_immutable_state or previous_hashes.get(package_id) != digest:
            immutable_uploads.append(
                (
                    manifest_path,
                    f"{r2_prefix}/packages/{package_id}/{digest}.json",
                )
            )
        if previous_hashes.get(package_id) != digest:
            legacy_uploads.append((manifest_path, f"{r2_prefix}/packages/{filename}"))

    if not upload_batch(client, bucket, immutable_uploads, "manifestos imutáveis"):
        return 1
    # Mantém compatibilidade com versões antigas do cliente que ainda leem packages/<id>.json.
    if not upload_batch(client, bucket, legacy_uploads, "compatibilidade legada"):
        return 1

    publish_path = os.path.join(local_dir, "index.publish.json")
    with open(publish_path, "w", encoding="utf-8", newline="\n") as stream:
        json.dump(published_index, stream, ensure_ascii=False, separators=(",", ":"))
        stream.write("\n")

    index_key = f"{r2_prefix}/index.json"
    index_digest = sha256_of(publish_path)
    semantic_index = {key: value for key, value in published_index.items() if key != "generatedAt"}
    semantic_digest = hashlib.sha256(
        json.dumps(semantic_index, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    ).hexdigest()
    try:
        if object_matches(client, bucket, index_key, semantic_digest, metadata_key="semantic-sha256"):
            print("index.json sem alterações nos pacotes; upload ignorado.")
        else:
            client.upload_file(
                publish_path,
                bucket,
                index_key,
                ExtraArgs={
                    "ContentType": "application/json",
                    "CacheControl": CACHE_CONTROL,
                    "Metadata": {"sha256": index_digest, "semantic-sha256": semantic_digest},
                },
            )
    except Exception as exc:  # noqa: BLE001
        print(f"Falha ao ativar o index.json; a execução seguinte poderá repetir com segurança: {exc}", file=sys.stderr)
        return 1

    # Gravar estado somente depois que o índice ativo foi publicado. Se falhar, a próxima
    # execução reenviará objetos imutáveis (seguros/idempotentes) em vez de pular conteúdo.
    new_state = {"schema": 2, "packages": digests}
    state_local_path = os.path.join(local_dir, "_package-hashes.v2.json")
    with open(state_local_path, "w", encoding="utf-8", newline="\n") as stream:
        json.dump(new_state, stream, separators=(",", ":"))
    state_key = f"{r2_prefix}/_state/package-hashes.json"
    state_digest = sha256_of(state_local_path)
    try:
        if object_matches(client, bucket, state_key, state_digest):
            print("Estado de pacotes sem alterações; upload ignorado.")
        else:
            client.upload_file(
                state_local_path,
                bucket,
                state_key,
                ExtraArgs={
                    "ContentType": "application/json",
                    "CacheControl": "no-cache",
                    "Metadata": {"sha256": state_digest},
                },
            )
    except Exception as exc:  # noqa: BLE001 - o índice está íntegro; repetir publicação é seguro.
        print(
            f"Aviso: índice publicado; estado não atualizado e será reconstruído na próxima execução: {exc}",
            file=sys.stderr,
        )
        return 1

    print(
        f"API validada e publicada: {len(digests):,} pacotes; "
        f"{len(immutable_uploads):,} manifesto(s) imutável(is) criado(s), "
        f"{len(legacy_uploads):,} alias(es) legado(s) atualizado(s)."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
