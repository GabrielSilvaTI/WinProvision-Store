#!/usr/bin/env python3
"""Copy legacy WinGet icons into missing V2 catalog media entries."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import sys
from datetime import UTC, datetime
from pathlib import Path
from urllib.parse import unquote, urlparse

import boto3
from botocore.config import Config
from botocore.exceptions import ClientError
from catalog_media_naming import icon_filename
from PIL import Image, UnidentifiedImageError

sys.path.insert(0, str(Path(__file__).resolve().parent))
BUCKET = os.environ.get("R2_BUCKET") or "winprovision"
LEGACY_MANIFEST_KEY = "Store/icon-manifest.json"
CATALOG_MANIFEST_KEY = "Store/Catalog/manifest.json"
SEARCH_INDEX_KEY = "Store/Catalog/manifest/search-index.json"
MEDIA_INDEX_KEY = "Store/Catalog/manifest/media-index.json"
LEGACY_PUBLIC_HOST = "pub-166b41912a994dbe86583ba10596d673.r2.dev"
LEGACY_ICON_PREFIX = "/Store/Icon_Database/"
MAX_ICON_BYTES = 20 * 1024 * 1024
SUPPORTED_EXTENSIONS = {".ico", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp"}


def make_client():
    required = ("R2_ACCOUNT_ID", "R2_ACCESS_KEY_ID", "R2_SECRET_ACCESS_KEY")
    missing = [name for name in required if not os.environ.get(name)]
    if missing:
        raise ValueError("Variáveis R2 ausentes: " + ", ".join(missing))
    return boto3.client(
        "s3",
        endpoint_url=f"https://{os.environ['R2_ACCOUNT_ID']}.r2.cloudflarestorage.com",
        aws_access_key_id=os.environ["R2_ACCESS_KEY_ID"],
        aws_secret_access_key=os.environ["R2_SECRET_ACCESS_KEY"],
        config=Config(signature_version="s3v4", retries={"max_attempts": 5, "mode": "standard"}),
        region_name="auto",
    )


def read_object(client, key: str) -> bytes:
    try:
        return client.get_object(Bucket=BUCKET, Key=key)["Body"].read()
    except ClientError as exc:
        code = exc.response.get("Error", {}).get("Code", "")
        raise ValueError(f"Não foi possível ler {key} no R2 ({code}).") from exc


def write_json(client, key: str, value: dict | list, *, cache_control: str) -> bytes:
    payload = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    client.put_object(
        Bucket=BUCKET,
        Key=key,
        Body=payload,
        ContentType="application/json",
        CacheControl=cache_control,
    )
    return payload


def load_documents(client):
    manifest = json.loads(read_object(client, CATALOG_MANIFEST_KEY))
    index_bytes = read_object(client, SEARCH_INDEX_KEY)
    index = json.loads(index_bytes)
    if not isinstance(manifest, dict) or manifest.get("schemaVersion") != 2:
        raise ValueError("manifest.json do catálogo V2 inválido.")
    if not isinstance(index, list) or len(index) != manifest.get("appCount"):
        raise ValueError("search-index.json não corresponde à contagem do manifesto.")
    media_bytes = read_object(client, MEDIA_INDEX_KEY)
    media_index = json.loads(media_bytes)
    if not isinstance(media_index, dict) or not isinstance(media_index.get("apps"), dict):
        raise ValueError("media-index.json inválido.")

    legacy = json.loads(read_object(client, LEGACY_MANIFEST_KEY))
    if not isinstance(legacy, dict):
        raise ValueError("O manifesto legado de ícones precisa ser um objeto JSON.")
    paginator = client.get_paginator("list_objects_v2")
    legacy_keys = {
        item["Key"]
        for page in paginator.paginate(Bucket=BUCKET, Prefix="Store/Icon_Database/")
        for item in page.get("Contents", [])
        if isinstance(item.get("Key"), str)
    }
    consistency = {
        "searchIndexHashValid": hashlib.sha256(index_bytes).hexdigest() == manifest.get("indexSha256"),
        "mediaIndexHashValid": not manifest.get("mediaIndexSha256")
        or hashlib.sha256(media_bytes).hexdigest() == manifest["mediaIndexSha256"],
    }
    return manifest, index, media_index, legacy, legacy_keys, media_bytes, consistency


def legacy_key(url: str) -> str | None:
    parsed = urlparse(url)
    if parsed.scheme != "https" or parsed.hostname != LEGACY_PUBLIC_HOST or parsed.query or parsed.fragment:
        return None
    if not parsed.path.startswith(LEGACY_ICON_PREFIX):
        return None
    filename = unquote(parsed.path[len(LEGACY_ICON_PREFIX) :])
    if not filename or "/" in filename or "\\" in filename or filename in {".", ".."}:
        return None
    if Path(filename).suffix.casefold() not in SUPPORTED_EXTENSIONS:
        return None
    return "Store/Icon_Database/" + filename


def has_v2_icon(*records: dict | None) -> bool:
    for record in records:
        media = record.get("media") if isinstance(record, dict) else None
        if isinstance(media, dict):
            icon = media.get("icon")
            if isinstance(icon, str) and icon.startswith("media/"):
                return True
    return False


def build_plan(index: list[dict], media_index: dict, legacy: dict, legacy_keys: set[str]) -> tuple[list[dict], dict]:
    legacy_by_id: dict[str, list[tuple[str, str]]] = {}
    for package_id, url in legacy.items():
        if not isinstance(package_id, str) or not isinstance(url, str):
            continue
        legacy_by_id.setdefault(package_id.casefold(), []).append((package_id, url))

    plan: list[dict] = []
    counts = {
        "catalogApps": 0,
        "alreadyHaveV2Icon": 0,
        "legacyMatch": 0,
        "missingLegacyIcon": 0,
        "missingLegacyObject": 0,
        "ambiguousId": 0,
        "invalidLegacyUrl": 0,
    }
    media_apps = media_index["apps"]
    for row in index:
        if not isinstance(row, dict) or not isinstance(row.get("id"), str):
            continue
        if str(row.get("source", "winget")).casefold() == "msstore":
            continue
        package_id = row["id"]
        counts["catalogApps"] += 1
        registry = media_apps.get(package_id.casefold())
        if has_v2_icon(row, registry):
            counts["alreadyHaveV2Icon"] += 1
            continue

        candidates = legacy_by_id.get(package_id.casefold(), [])
        if len(candidates) > 1:
            counts["ambiguousId"] += 1
            plan.append({"id": package_id, "status": "ambiguous-id", "legacyIds": [item[0] for item in candidates]})
            continue
        if not candidates:
            counts["missingLegacyIcon"] += 1
            plan.append({"id": package_id, "status": "missing-legacy-icon"})
            continue

        old_id, url = candidates[0]
        key = legacy_key(url)
        if key is None:
            counts["invalidLegacyUrl"] += 1
            plan.append({"id": package_id, "legacyId": old_id, "status": "invalid-legacy-url"})
            continue
        if key not in legacy_keys:
            counts["missingLegacyObject"] += 1
            plan.append({"id": package_id, "legacyId": old_id, "sourceKey": key, "status": "missing-legacy-object"})
            continue
        counts["legacyMatch"] += 1
        plan.append({"id": package_id, "legacyId": old_id, "sourceKey": key, "status": "ready"})

    return plan, counts


def convert_icon(data: bytes) -> bytes:
    if not data or len(data) > MAX_ICON_BYTES:
        raise ValueError("imagem vazia ou acima do limite de 20 MiB")
    try:
        with Image.open(io.BytesIO(data)) as source:
            if source.width < 16 or source.height < 16 or max(source.size) > 2048:
                raise ValueError(f"dimensões de ícone inválidas: {source.size}")
            source.load()
            image = source.convert("RGBA")
        output = io.BytesIO()
        image.save(output, format="PNG", optimize=True)
        return output.getvalue()
    except (OSError, UnidentifiedImageError, Image.DecompressionBombError) as exc:
        raise ValueError(f"arquivo de ícone inválido: {exc}") from exc


def repair_interrupted_checkpoint(
    client, manifest: dict, index: list[dict], media_index: dict, media_bytes: bytes
) -> int:
    """Repair index/manifest hashes after an interrupted prior migration checkpoint."""
    repaired = 0
    rows_by_id = {str(row["id"]).casefold(): row for row in index if isinstance(row, dict) and row.get("id")}
    for package_id, registry in media_index["apps"].items():
        if (
            not isinstance(registry, dict)
            or not isinstance(registry.get("icon"), str)
            or not registry["icon"].startswith("media/")
        ):
            continue
        row = rows_by_id.get(str(package_id).casefold())
        if row is None or row.get("media") == registry:
            continue
        detail_rel = row.get("detailPath")
        decoded_detail_rel = unquote(detail_rel) if isinstance(detail_rel, str) else ""
        if (
            not decoded_detail_rel.startswith("apps/")
            or "\\" in decoded_detail_rel
            or any(part in {"", ".", ".."} for part in decoded_detail_rel.split("/"))
        ):
            raise ValueError(f"detailPath inválido durante a recuperação de {package_id}.")
        detail_key = "Store/Catalog/" + decoded_detail_rel
        detail_bytes = read_object(client, detail_key)
        detail = json.loads(detail_bytes)
        if not isinstance(detail, dict) or str(detail.get("id", "")).casefold() != str(package_id).casefold():
            raise ValueError(f"O detalhe publicado não corresponde ao ID {package_id} durante a recuperação.")
        detail_media = detail.get("media") if isinstance(detail.get("media"), dict) else {}
        if detail_media.get("icon") != registry["icon"]:
            raise ValueError(
                f"Mídia do manifesto e detalhe divergem para {package_id}; recuperação interrompida para preservar os dados."
            )
        row["media"] = detail_media
        row["recordSha256"] = hashlib.sha256(detail_bytes).hexdigest()
        repaired += 1

    index_bytes = json.dumps(index, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    index_digest = hashlib.sha256(index_bytes).hexdigest()
    digest_builder = hashlib.sha256()
    for row in index:
        digest_builder.update(unquote(str(row["detailPath"])).encode("utf-8"))
        digest_builder.update(b"\0")
        digest_builder.update(bytes.fromhex(row["recordSha256"]))
    digest_builder.update(hashlib.sha256(index_bytes).digest())
    manifest["mediaIndexSha256"] = hashlib.sha256(media_bytes).hexdigest()
    manifest["indexSha256"] = index_digest
    manifest["catalogSha256"] = digest_builder.hexdigest()
    manifest["generatedUtc"] = datetime.now(UTC).isoformat().replace("+00:00", "Z")
    client.put_object(
        Bucket=BUCKET,
        Key=SEARCH_INDEX_KEY,
        Body=index_bytes,
        ContentType="application/json",
        CacheControl="public, max-age=300, must-revalidate",
        Metadata={"sha256": index_digest},
    )
    write_json(client, CATALOG_MANIFEST_KEY, manifest, cache_control="no-cache, max-age=0, must-revalidate")
    return repaired


def stored_object_sha256(client, key: str) -> str | None:
    try:
        head = client.head_object(Bucket=BUCKET, Key=key)
    except ClientError as exc:
        code = exc.response.get("Error", {}).get("Code", "")
        if code in {"404", "NoSuchKey", "NotFound"}:
            return None
        raise
    digest = head.get("Metadata", {}).get("sha256")
    if isinstance(digest, str) and len(digest) == 64 and all(char in "0123456789abcdefABCDEF" for char in digest):
        return digest.lower()
    return hashlib.sha256(read_object(client, key)).hexdigest()


def publish_batch(client, manifest: dict, index: list[dict], media_index: dict, batch: list[dict]) -> tuple[int, int]:
    rows_by_id = {str(row["id"]).casefold(): row for row in index if isinstance(row, dict) and row.get("id")}
    published = 0
    reconciled = 0
    for item in batch:
        package_id = item["id"]
        row = rows_by_id[package_id.casefold()]
        detail_rel = row.get("detailPath")
        decoded_detail_rel = unquote(detail_rel) if isinstance(detail_rel, str) else ""
        if (
            not decoded_detail_rel.startswith("apps/")
            or "\\" in decoded_detail_rel
            or any(part in {"", ".", ".."} for part in decoded_detail_rel.split("/"))
        ):
            raise ValueError(f"detailPath inválido para {package_id}.")
        detail_key = "Store/Catalog/" + decoded_detail_rel
        current_detail_bytes = read_object(client, detail_key)
        detail = json.loads(current_detail_bytes)
        if not isinstance(detail, dict) or str(detail.get("id", "")).casefold() != package_id.casefold():
            raise ValueError(f"O detalhe publicado não corresponde ao ID {package_id}.")
        current_detail_hash = hashlib.sha256(current_detail_bytes).hexdigest()
        expected_detail_hash = row.get("recordSha256")
        if expected_detail_hash and expected_detail_hash != current_detail_hash and not has_v2_icon(detail):
            raise ValueError(
                f"O detalhe de {package_id} mudou depois da leitura do índice; atualize o catálogo e rode novamente."
            )
        registry = media_index["apps"].get(package_id.casefold(), {})

        # A previous interrupted run may have uploaded the detail before its
        # checkpoint. Reconcile that association without uploading over the icon.
        detail_media = detail.get("media") if isinstance(detail.get("media"), dict) else {}
        row_media = row.get("media") if isinstance(row.get("media"), dict) else {}
        existing_media = next(
            (
                candidate
                for candidate in (detail_media, registry, row_media)
                if isinstance(candidate.get("icon"), str) and candidate["icon"].startswith("media/")
            ),
            None,
        )
        if existing_media is not None:
            media = dict(registry) if isinstance(registry, dict) else {}
            media.update({"icon": existing_media["icon"]})
            icon_sha = existing_media.get("iconSha256")
            if isinstance(icon_sha, str):
                media["iconSha256"] = icon_sha
            screenshot_groups = [
                candidate.get("screenshots")
                for candidate in (detail_media, registry, row_media)
                if isinstance(candidate.get("screenshots"), list)
            ]
            media["screenshots"] = list(
                dict.fromkeys(path for group in screenshot_groups for path in group if isinstance(path, str))
            )
            detail["media"] = media
            detail_bytes = json.dumps(detail, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
            detail_hash = hashlib.sha256(detail_bytes).hexdigest()
            if detail_bytes != current_detail_bytes:
                client.put_object(
                    Bucket=BUCKET,
                    Key=detail_key,
                    Body=detail_bytes,
                    ContentType="application/json",
                    CacheControl="public, max-age=300, must-revalidate",
                    Metadata={"sha256": detail_hash},
                )
            media_index["apps"][package_id.casefold()] = media
            row["media"] = media
            row["recordSha256"] = detail_hash
            item["status"] = "skipped-v2-icon-present"
            reconciled += 1
            continue

        try:
            source = read_object(client, item["sourceKey"])
            png = convert_icon(source)
        except (ValueError, ClientError, OSError) as exc:
            item.update({"status": "source-error", "error": f"{type(exc).__name__}: {exc}"})
            print(f"{package_id}: ícone legado ignorado ({type(exc).__name__}: {exc})", file=sys.stderr)
            continue
        digest = hashlib.sha256(png).hexdigest()
        icon_path = f"media/{icon_filename(package_id, 'png')}"
        icon_key = detail_key.rsplit("/", 1)[0] + "/" + icon_path
        destination_digest = stored_object_sha256(client, icon_key)
        if destination_digest and destination_digest != digest:
            item.update(
                {
                    "status": "destination-conflict",
                    "path": icon_key.removeprefix("Store/Catalog/"),
                    "existingSha256": destination_digest,
                }
            )
            print(
                f"{package_id}: destino V2 já existe com conteúdo diferente; preservado sem sobrescrita",
                file=sys.stderr,
            )
            continue
        if destination_digest is None:
            client.put_object(
                Bucket=BUCKET,
                Key=icon_key,
                Body=png,
                ContentType="image/png",
                CacheControl="public, max-age=31536000, immutable",
                Metadata={"sha256": digest},
            )

        media = dict(registry) if isinstance(registry, dict) else {}
        media.update({"icon": icon_path, "iconSha256": digest})
        screenshot_groups = [
            candidate.get("screenshots")
            for candidate in (detail_media, registry, row_media)
            if isinstance(candidate.get("screenshots"), list)
        ]
        media["screenshots"] = list(
            dict.fromkeys(path for group in screenshot_groups for path in group if isinstance(path, str))
        )
        detail["media"] = media
        detail_bytes = json.dumps(detail, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        detail_hash = hashlib.sha256(detail_bytes).hexdigest()
        client.put_object(
            Bucket=BUCKET,
            Key=detail_key,
            Body=detail_bytes,
            ContentType="application/json",
            CacheControl="public, max-age=300, must-revalidate",
            Metadata={"sha256": detail_hash},
        )

        media_index["apps"][package_id.casefold()] = media
        row["media"] = media
        row["recordSha256"] = detail_hash
        item.update({"status": "published", "path": icon_key.removeprefix("Store/Catalog/"), "sha256": digest})
        published += 1

    if not published and not reconciled:
        return 0, 0

    media_index["generatedUtc"] = datetime.now(UTC).isoformat().replace("+00:00", "Z")
    media_bytes = write_json(client, MEDIA_INDEX_KEY, media_index, cache_control="no-cache, max-age=0, must-revalidate")
    manifest["mediaIndexSha256"] = hashlib.sha256(media_bytes).hexdigest()

    index_bytes = json.dumps(index, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    digest_builder = hashlib.sha256()
    for row in index:
        digest_builder.update(unquote(str(row["detailPath"])).encode("utf-8"))
        digest_builder.update(b"\0")
        digest_builder.update(bytes.fromhex(row["recordSha256"]))
    digest_builder.update(hashlib.sha256(index_bytes).digest())
    manifest["indexSha256"] = hashlib.sha256(index_bytes).hexdigest()
    manifest["catalogSha256"] = digest_builder.hexdigest()
    manifest["generatedUtc"] = datetime.now(UTC).isoformat().replace("+00:00", "Z")
    client.put_object(
        Bucket=BUCKET,
        Key=SEARCH_INDEX_KEY,
        Body=index_bytes,
        ContentType="application/json",
        CacheControl="public, max-age=300, must-revalidate",
        Metadata={"sha256": manifest["indexSha256"]},
    )
    write_json(client, CATALOG_MANIFEST_KEY, manifest, cache_control="no-cache, max-age=0, must-revalidate")
    return published, reconciled


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true", help="publica os ícones; sem isso apenas audita")
    parser.add_argument("--limit", type=int, default=0, help="limite de ícones elegíveis nesta execução; 0 = todos")
    parser.add_argument("--batch-size", type=int, default=100, help="checkpoint de publicação em apps")
    parser.add_argument("--report", type=Path, default=Path("legacy-icon-migration-report.json"))
    args = parser.parse_args()
    if args.limit < 0 or args.batch_size < 1:
        parser.error("limit precisa ser >= 0 e batch-size precisa ser positivo")

    try:
        client = make_client()
        manifest, index, media_index, legacy, legacy_keys, media_bytes, consistency = load_documents(client)
        recovered = 0
        if args.apply and not all(consistency.values()):
            recovered = repair_interrupted_checkpoint(client, manifest, index, media_index, media_bytes)
            print(
                f"Checkpoint anterior recuperado; {recovered} detalhe(s) reconciliado(s) com o media-index.", flush=True
            )
        plan, counts = build_plan(index, media_index, legacy, legacy_keys)
        ready = [item for item in plan if item["status"] == "ready"]
        report = {
            "schemaVersion": 1,
            "generatedUtc": datetime.now(UTC).isoformat().replace("+00:00", "Z"),
            "mode": "apply" if args.apply else "audit",
            "catalogConsistencyBeforeApply": consistency,
            "interruptedCheckpointDetailsReconciled": recovered,
            "counts": counts,
            "items": plan,
        }
        if args.apply:
            if args.limit:
                ready = ready[: args.limit]
            report["counts"]["selectedForApply"] = len(ready)
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
            for offset in range(0, len(ready), args.batch_size):
                batch = ready[offset : offset + args.batch_size]
                count, reconciled = publish_batch(client, manifest, index, media_index, batch)
                print(
                    f"Lote {offset // args.batch_size + 1}: {count} ícone(s) publicado(s), {reconciled} associação(ões) V2 preservada(s); último ID: {batch[-1]['id']}",
                    flush=True,
                )
                args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        else:
            report["counts"]["eligibleToCopy"] = len(ready)

        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps(report["counts"], ensure_ascii=False, indent=2))
        print(f"Relatório: {args.report.resolve()}")
        return 0
    except (OSError, ValueError, ClientError, json.JSONDecodeError) as exc:
        print(f"Migração cancelada: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
