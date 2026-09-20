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
        self.bucket = os.environ["R2_BUCKET"]
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
        return fail(f"versiondata_http_{status}" if status else "versiondata_rede", err)
    try:
        text, _ = probe.decode_payload(data)
        entry = probe.pick_version(probe.parse_yaml(text), pkg.get("latest"))
    except probe.DecodeError as exc:
        return fail("versiondata_decode", exc)
    except Exception as exc:  # noqa: BLE001
        return fail("versiondata_yaml", f"{type(exc).__name__}: {exc}")
    rp = str(entry.get("rP") or "").lstrip("/")
    res["version"] = str(entry.get("v") or "")
    if not rp:
        return fail("versiondata_sem_rP")

    status, data, err = probe.http_get(f"{cdn}/{rp}")
    if data is None:
        return fail(f"manifest_http_{status}" if status else "manifest_rede", err)
    try:
        text, _ = probe.decode_payload(data)
        manifest = probe.parse_yaml(text)
    except probe.DecodeError as exc:
        return fail("manifest_decode", exc)
    except Exception as exc:  # noqa: BLE001
        return fail("manifest_yaml", f"{type(exc).__name__}: {exc}")

    icons = probe.find_icons(manifest)
    if not icons:
        return fail("sem_icons_no_manifest")
    icon = probe.pick_icon(icons)
    if not icon:
        return fail("icons_sem_url_https")
    url = str(icon["IconUrl"])
    res["url"] = url

    status, blob, err = probe.http_get(url, max_bytes=probe.MAX_ICON_BYTES)
    if blob is None:
        return fail(f"icone_http_{status}" if status else "icone_rede", err)
    expected = str(icon.get("IconSha256") or "").strip().lower()
    if expected and hashlib.sha256(blob).hexdigest() != expected:
        return fail("icone_sha_diferente", url)
    try:
        res["ext"] = validate_image(blob)
    except Exception as exc:  # noqa: BLE001
        return fail("icone_invalido", f"{type(exc).__name__}: {exc}")
    res["data"] = blob
    res["status"] = "ok"
    return res


# ---------------------------------------------------------------- principal


def write_report(out: Path, meta, results, still_missing):
    counts = Counter(r["status"] for r in results)
    uploaded = counts.get("ok", 0)
    cov_before, cov_after, total = meta["covered_before"], meta["covered_after"], meta["catalog"]
    pct = lambda n: f"{100.0 * n / total:.1f}%" if total else "n/a"
    mode = "SIMULAÇÃO (nada foi enviado ao R2)" if meta["dry_run"] else "envio real"
    lines = [
        "# Sincronização de ícones (CDN winget -> R2)",
        "",
        f"- Modo: {mode}",
        f"- Apps no catálogo: {total}",
        f"- Com ícone antes: {cov_before} ({pct(cov_before)})",
        f"- **Ícones novos: {uploaded}** -> com ícone agora: {cov_after} ({pct(cov_after)})",
        f"- Pulados por teste recente sem ícone: {meta['skipped_negative']}",
        f"- Testados nesta execução: {len(results)}"
        + (f" (limite de {meta['max_probes']} atingido)" if meta["limit_hit"] else ""),
        "",
        "## Resultado dos testes",
        "",
        "| Status | Apps |",
        "|---|---:|",
    ]
    lines += [f"| {s} | {n} |" for s, n in counts.most_common()]
    errors = [r for r in results if r["status"] != "ok" and not is_negative(r["status"])]
    if errors:
        lines += [
            "",
            "## Erros que não entram no cache (serão testados de novo)",
            "",
            "| Id | Status | Detalhe |",
            "|---|---|---|",
        ]
        lines += [f"| {r['id']} | {r['status']} | {r['detail'].replace('|', '/')} |" for r in errors[:20]]
    lines += ["", f"## Ainda sem ícone: {len(still_missing)} (primeiros 40 na ordem do apps.json)", ""]
    lines += [f"- {i}" for i in still_missing[:40]]
    text = "\n".join(lines) + "\n"
    (out / "summary.md").write_text(text, encoding="utf-8")
    (out / "missing-icons.json").write_text(json.dumps(still_missing, ensure_ascii=False, indent=1), encoding="utf-8")
    (out / "results.json").write_text(
        json.dumps([{k: v for k, v in r.items() if k != "data"} for r in results], ensure_ascii=False, indent=1),
        encoding="utf-8",
    )
    (out / "report.json").write_text(
        json.dumps({**meta, "status": dict(counts)}, ensure_ascii=False, indent=1), encoding="utf-8"
    )
    step = os.environ.get("GITHUB_STEP_SUMMARY")
    if step:
        with open(step, "a", encoding="utf-8") as fh:
            fh.write(text)
    print("\n" + text, flush=True)


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--apps-url", default=DEFAULT_APPS_URL)
    ap.add_argument("--prefix", default=DEFAULT_PREFIX)
    ap.add_argument("--state-key", default=DEFAULT_STATE_KEY)
    ap.add_argument("--out", default="sync-out")
    ap.add_argument("--dry-run", action="store_true", help="baixa e valida, mas não envia nada ao R2")
    ap.add_argument("--max-probes", type=int, default=0, help="máximo de apps testados por execução (0 = sem limite)")
    ap.add_argument("--recheck-days", type=int, default=30)
    ap.add_argument("--workers", type=int, default=8)
    ap.add_argument(
        "--dotless-alias", action="store_true", help="grava também a cópia sem pontos (só quando não há colisão)"
    )
    ap.add_argument("--cdn", default=os.environ.get("WINGET_CDN", probe.DEFAULT_CDN))
    ap.add_argument("--local-store", default="", help="usa uma pasta local no lugar do R2 (testes)")
    args = ap.parse_args(argv)

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    cdn = args.cdn.rstrip("/")
    prefix = args.prefix if args.prefix.endswith("/") else args.prefix + "/"
    store = LocalStore(args.local_store) if args.local_store else R2Store()
    today = date.today()

    catalog = load_catalog(args.apps_url)
    print(f"Catálogo: {len(catalog)} apps.", flush=True)
    existing = list_existing(store, prefix)
    print(f"Bucket: {len(existing)} ícones em {prefix}.", flush=True)

    groups = defaultdict(set)
    for pid in catalog:
        groups[dotless(norm_id(pid))].add(norm_id(pid))

    def has_icon(pid: str) -> bool:
        low = norm_id(pid)
        if low in existing:
            return True
        dl = dotless(low)
        return dl in existing and len(groups[dl]) == 1

    state = store.get_json(args.state_key) or {}
    negative = {k: v for k, v in (state.get("no_icon") or {}).items()}
    cutoff = today - timedelta(days=args.recheck_days)

    def recently_negative(pid: str) -> bool:
        stamp = negative.get(norm_id(pid))
        try:
            return bool(stamp) and date.fromisoformat(stamp) > cutoff
        except ValueError:
            return False

    covered_before = [p for p in catalog if has_icon(p)]
    without = [p for p in catalog if not has_icon(p)]
    skipped_negative = [p for p in without if recently_negative(p)]
    pending = [p for p in without if not recently_negative(p)]
    limit_hit = bool(args.max_probes) and len(pending) > args.max_probes
    if args.max_probes:
        pending = pending[: args.max_probes]
    print(
        f"Sem ícone: {len(without)} | em cache negativo: {len(skipped_negative)} | a testar: {len(pending)}", flush=True
    )

    results: list[dict] = []
    if pending:
        db_path = probe.fetch_index(f"{cdn}/source2.msix", out)
        try:
            packages = probe.read_packages(db_path, out / "schema.txt")
        except probe.SchemaError as exc:
            print(f"ERRO de schema do índice: {exc}", file=sys.stderr)
            return 2
        finally:
            db_path.unlink(missing_ok=True)
        index_map = {norm_id(p["id"]): p for p in packages}

        lock = threading.Lock()
        done = [0]
        new_dir = out / "new-icons"

        def work(pid: str) -> dict:
            pkg = index_map.get(norm_id(pid))
            if pkg is None:
                res = {
                    "id": pid,
                    "status": "fora_do_indice",
                    "detail": "",
                    "url": "",
                    "ext": "",
                    "data": None,
                    "version": "",
                }
            else:
                res = resolve_icon(pkg, cdn)
                res["id"] = pid
            if res["status"] == "ok":
                key_name = f"{norm_id(pid)}.{res['ext']}"
                try:
                    if args.dry_run:
                        new_dir.mkdir(exist_ok=True)
                        (new_dir / key_name).write_bytes(res["data"])
                    else:
                        store.put_bytes(prefix + key_name, res["data"], CONTENT_TYPES[res["ext"]])
                        dl = dotless(norm_id(pid))
                        if args.dotless_alias and dl not in existing and len(groups[dl]) == 1 and dl != norm_id(pid):
                            store.put_bytes(f"{prefix}{dl}.{res['ext']}", res["data"], CONTENT_TYPES[res["ext"]])
                except Exception as exc:  # noqa: BLE001
                    res["status"], res["detail"] = "upload_falhou", f"{type(exc).__name__}: {exc}"[:240]
            res["data"] = None
            with lock:
                done[0] += 1
                if done[0] % 50 == 0 or done[0] == len(pending):
                    print(f"  [{done[0]}/{len(pending)}] {pid} -> {res['status']}", flush=True)
            return res

        print(f"Testando {len(pending)} apps com {args.workers} threads...", flush=True)
        with ThreadPoolExecutor(max_workers=max(1, args.workers)) as ex:
            results = list(ex.map(work, pending))

    # estado: lembra quem não tem ícone na CDN, esquece quem ganhou ícone e quem saiu do catálogo
    ok_ids = {norm_id(r["id"]) for r in results if r["status"] == "ok"}
    catalog_norm = {norm_id(p) for p in catalog}
    new_negative = {k: v for k, v in negative.items() if k in catalog_norm and k not in ok_ids}
    for r in results:
        if is_negative(r["status"]):
            new_negative[norm_id(r["id"])] = today.isoformat()
    if not args.dry_run:
        store.put_json(
            args.state_key, {"version": 1, "updated": today.isoformat(), "no_icon": dict(sorted(new_negative.items()))}
        )

    covered_after = len(covered_before) + len(ok_ids)
    still_missing = [p for p in catalog if not has_icon(p) and norm_id(p) not in ok_ids]
    meta = {
        "catalog": len(catalog),
        "covered_before": len(covered_before),
        "covered_after": covered_after,
        "skipped_negative": len(skipped_negative),
        "max_probes": args.max_probes,
        "limit_hit": limit_hit,
        "dry_run": args.dry_run,
        "recheck_days": args.recheck_days,
        "dotless_alias": args.dotless_alias,
        "generated_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    }
    write_report(out, meta, results, still_missing)

    gh_out = os.environ.get("GITHUB_OUTPUT")
    if gh_out:
        with open(gh_out, "a", encoding="utf-8") as fh:
            fh.write(f"uploaded={0 if args.dry_run else len(ok_ids)}\n")

    errors = [r for r in results if r["status"] != "ok" and not is_negative(r["status"])]
    if len(results) >= 20 and len(errors) > 0.10 * len(results):
        print(
            f"ERRO: {len(errors)} de {len(results)} testes falharam por motivo que não é 'sem ícone'.", file=sys.stderr
        )
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
