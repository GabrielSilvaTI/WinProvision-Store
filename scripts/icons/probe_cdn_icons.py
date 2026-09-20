#!/usr/bin/env python3
"""Sonda de cobertura de ícones na CDN oficial do winget.

Somente leitura: não usa credenciais e não escreve no R2. Para cada pacote da
amostra ele refaz o caminho que o próprio winget percorre:

  1. baixa cache/source2.msix e extrai Public/index.db (índice V2);
  2. lê, na tabela packages, o id e o hash da versionData de cada pacote;
  3. baixa cache/packages/<Id>/<8 primeiros do hash>/versionData.mszyml
     (YAML comprimido com MSZIP) e escolhe a versão mais recente;
  4. baixa o manifest dessa versão (campo rP) e procura a lista Icons;
  5. baixa o ícone escolhido, confere o SHA256 e converte para PNG.

Saída em --out: summary.md, report.json, results.csv, schema.txt,
png/, raw/ e debug/ (amostras do YAML para inspeção).

Dependências: pyyaml, pillow, pymszip.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import io
import json
import os
import random
import re
import sqlite3
import sys
import threading
import time
import urllib.error
import urllib.request
import zipfile
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from urllib.parse import quote, urlparse

import yaml

DEFAULT_CDN = "https://cdn.winget.microsoft.com/cache"
DEFAULT_APPS_URL = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/Database/apps.json"
UA = "WinProvisionStore-IconProbe/1.0"
MAX_ICON_BYTES = 2 * 1024 * 1024
PNG_MAX_SIDE = 256
# Só para testes locais com servidor http://127.0.0.1; em produção fica desligado.
ALLOWED_SCHEMES = ("https://", "http://") if os.environ.get("PROBE_ALLOW_HTTP") == "1" else ("https://",)

# Pacotes de controle: se estes falharem, o problema é do método e não do catálogo.
# Os que não existirem no índice são ignorados.
CONTROL_IDS = [
    "Microsoft.PowerToys",
    "Microsoft.PowerShell",
    "Insomnia.Insomnia",
    "cjpais.Handy",
    "astral-sh.uv",
]

ID_KEYS = {"id", "packageidentifier", "packageid", "package_id"}
ID_RE = re.compile(r"^[A-Za-z0-9][\w.+\-]*\.[\w.+\-]+$")


class DecodeError(Exception):
    pass


class SchemaError(Exception):
    pass


# ---------------------------------------------------------------- rede

def http_get(url, timeout=60, retries=3, max_bytes=None):
    """Devolve (status, bytes|None, erro). status 0 = falha de rede."""
    # Alguns "rP" do índice do winget trazem espaço cru (ex.: pasta de versão
    # "1, 36, 2, 0"). Python 3.12 rejeita URL com espaço bruto antes mesmo de
    # abrir a conexão. Escapa sem mexer em % já codificado.
    url = quote(url, safe=":/?&=%")
    last = "erro desconhecido"
    for attempt in range(retries):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": UA})
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                data = resp.read(max_bytes + 1) if max_bytes else resp.read()
                if max_bytes and len(data) > max_bytes:
                    return 0, None, "arquivo maior que o limite"
                return resp.status, data, ""
        except urllib.error.HTTPError as exc:
            if exc.code in (429, 500, 502, 503, 504) and attempt < retries - 1:
                last = f"HTTP {exc.code}"
                time.sleep(2 ** attempt + random.random())
                continue
            return exc.code, None, f"HTTP {exc.code}"
        except (urllib.error.URLError, TimeoutError, OSError) as exc:
            last = f"{type(exc).__name__}: {exc}"
            if attempt < retries - 1:
                time.sleep(2 ** attempt + random.random())
    return 0, None, last


# ---------------------------------------------------------------- decodificação

def looks_like_text(data: bytes) -> bool:
    head = data[:256]
    if not head:
        return False
    return not any(b < 9 or 13 < b < 32 for b in head)


def decode_payload(data: bytes) -> tuple[str, str]:
    """Devolve (texto, modo). Aceita YAML puro ou MSZIP."""
    if looks_like_text(data):
        return data.decode("utf-8-sig"), "plain"
    try:
        import pymszip
    except ImportError as exc:
        raise DecodeError("pymszip não instalado (pip install pymszip)") from exc
    try:
        raw = pymszip.decompress(data)
    except Exception as exc:  # noqa: BLE001 - queremos o motivo no relatório
        raise DecodeError(
            f"MSZIP falhou ({type(exc).__name__}: {exc}); início={data[:16].hex()}"
        ) from exc
    try:
        return raw.decode("utf-8-sig"), "mszip"
    except UnicodeDecodeError as exc:
        raise DecodeError(f"conteúdo descomprimido não é UTF-8: {exc}") from exc


def parse_yaml(text: str):
    # BaseLoader mantém tudo como texto: "2.0" continua "2.0" e não vira float.
    return yaml.load(text, Loader=yaml.BaseLoader)


def pick_version(vdata, latest):
    entries = vdata.get("vD") if isinstance(vdata, dict) else None
    if not isinstance(entries, list) or not entries:
        raise ValueError("versionData sem lista 'vD'")
    if latest:
        for entry in entries:
            if isinstance(entry, dict) and entry.get("v") == latest:
                return entry
    return entries[0]


def find_icons(node):
    if isinstance(node, dict):
        value = node.get("Icons")
        if isinstance(value, list):
            return value
        for child in node.values():
            found = find_icons(child)
            if found is not None:
                return found
    elif isinstance(node, list):
        for child in node:
            found = find_icons(child)
            if found is not None:
                return found
    return None


def pick_icon(icons):
    cands = [
        i for i in icons
        if isinstance(i, dict) and str(i.get("IconUrl") or "").startswith(ALLOWED_SCHEMES)
    ]
    if not cands:
        return None

    def key(icon):
        theme = (icon.get("IconTheme") or "").lower()
        match = re.search(r"(\d+)", icon.get("IconResolution") or "")
        res = int(match.group(1)) if match else 0
        ftype = (icon.get("IconFileType") or "").lower()
        return (0 if theme in ("", "default") else 1, -res, 0 if ftype == "png" else 1)

    return sorted(cands, key=key)[0]


def to_png(data: bytes) -> bytes:
    from PIL import Image

    img = Image.open(io.BytesIO(data))
    if img.format == "ICO":
        sizes = img.info.get("sizes") or {img.size}
        img.size = max(sizes)
    img.load()
    img = img.convert("RGBA")
    if max(img.size) > PNG_MAX_SIDE:
        img.thumbnail((PNG_MAX_SIDE, PNG_MAX_SIDE), Image.LANCZOS)
    out = io.BytesIO()
    img.save(out, format="PNG", optimize=True)
    return out.getvalue()


# ---------------------------------------------------------------- índice

def fetch_index(msix_url: str, work: Path) -> Path:
    print(f"Baixando {msix_url} ...", flush=True)
    status, data, err = http_get(msix_url, timeout=180, retries=4)
    if data is None:
        raise SystemExit(f"Não consegui baixar o índice ({err}).")
    print(f"  msix: {len(data) / 1e6:.1f} MB", flush=True)
    with zipfile.ZipFile(io.BytesIO(data)) as zf:
        name = next((n for n in zf.namelist() if n.lower().endswith("public/index.db")), None)
        if not name:
            raise SystemExit("Public/index.db não existe no msix: " + ", ".join(zf.namelist()[:20]))
        db_path = work / "index.db"
        db_path.write_bytes(zf.read(name))
    print(f"  index.db: {db_path.stat().st_size / 1e6:.1f} MB", flush=True)
    return db_path


def to_hex(value) -> str:
    if value is None:
        return ""
    if isinstance(value, (bytes, bytearray)):
        raw = bytes(value)
        if len(raw) == 32:
            return raw.hex()
        text = raw.decode("ascii", "ignore").strip().lower()
        return text if re.fullmatch(r"[0-9a-f]{8,64}", text) else ""
    text = str(value).strip().lower()
    return text if re.fullmatch(r"[0-9a-f]{8,64}", text) else ""


def read_packages(db_path: Path, schema_out: Path):
    con = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    try:
        tables = [r[0] for r in con.execute(
            "select name from sqlite_master where type='table' order by name")]
        schema = {t: [r[1] for r in con.execute(f'pragma table_info("{t}")')] for t in tables}
        lines = []
        for table, cols in schema.items():
            count = con.execute(f'select count(*) from "{table}"').fetchone()[0]
            lines.append(f"{table} ({count} linhas): {', '.join(cols)}")
        schema_out.write_text("\n".join(lines) + "\n", encoding="utf-8")
        print("Schema do index.db:\n  " + "\n  ".join(lines), flush=True)

        if "packages" not in schema:
            raise SchemaError("tabela 'packages' não existe; veja schema.txt")
        cols = schema["packages"]
        lower = {c.lower(): c for c in cols}
        id_col = lower.get("id")
        hash_col = next((c for c in cols if "hash" in c.lower()), None)
        ver_col = lower.get("latest_version")
        if not id_col or not hash_col:
            raise SchemaError(f"packages sem coluna de id/hash; colunas: {cols}")
        select = [f'"{id_col}"', f'"{hash_col}"'] + ([f'"{ver_col}"'] if ver_col else [])
        packages = []
        for row in con.execute(f'select {", ".join(select)} from packages'):
            packages.append({
                "id": str(row[0]),
                "hash": to_hex(row[1]),
                "latest": str(row[2]) if ver_col and row[2] is not None else "",
            })
        return packages
    finally:
        con.close()


# ---------------------------------------------------------------- amostra

def extract_ids(node, out):
    if isinstance(node, dict):
        for key, value in node.items():
            if isinstance(value, str) and key.lower() in ID_KEYS and ID_RE.match(value):
                out.append(value)
            else:
                extract_ids(value, out)
    elif isinstance(node, list):
        for item in node:
            extract_ids(item, out)


def extract_ids_from_keys(node):
    if isinstance(node, dict):
        keys = [k for k, v in node.items() if isinstance(v, dict) and ID_RE.match(k)]
        if keys:
            return keys
        for value in node.values():
            found = extract_ids_from_keys(value)
            if found:
                return found
    return []


def ids_from_json(obj):
    ids: list[str] = []
    extract_ids(obj, ids)
    return ids or extract_ids_from_keys(obj)


def dedupe(ids):
    seen, out = set(), []
    for i in ids:
        if i.lower() not in seen:
            seen.add(i.lower())
            out.append(i)
    return out


def load_wanted_ids(args):
    """Devolve (ids, origem). Lista vazia = usar o índice inteiro."""
    if args.ids:
        return dedupe([s.strip() for s in args.ids.split(",") if s.strip()]), "--ids"
    if args.ids_file:
        path = Path(args.ids_file)
        text = path.read_text(encoding="utf-8-sig")
        if path.suffix.lower() == ".json":
            return dedupe(ids_from_json(json.loads(text))), f"arquivo {path.name}"
        return dedupe([l.strip() for l in text.splitlines() if l.strip() and not l.startswith("#")]), f"arquivo {path.name}"
    if args.apps_url:
        status, data, err = http_get(args.apps_url, timeout=60)
        if data is None:
            print(f"AVISO: não consegui baixar {args.apps_url} ({err}); usando amostra do índice.", flush=True)
            return [], f"amostra aleatória do índice (apps.json falhou: {err})"
        try:
            ids = dedupe(ids_from_json(json.loads(data.decode("utf-8-sig"))))
        except json.JSONDecodeError as exc:
            print(f"AVISO: apps.json inválido ({exc}); usando amostra do índice.", flush=True)
            return [], "amostra aleatória do índice (apps.json inválido)"
        if not ids:
            print("AVISO: nenhum PackageIdentifier reconhecido no apps.json; usando amostra do índice.", flush=True)
            return [], "amostra aleatória do índice (apps.json sem ids reconhecidos)"
        return ids, "apps.json do catálogo"
    return [], "amostra aleatória do índice"


# ---------------------------------------------------------------- sondagem

class Ctx:
    def __init__(self, cdn, out: Path, total: int):
        self.cdn = cdn.rstrip("/")
        self.png_dir = out / "png"
        self.raw_dir = out / "raw"
        self.debug_dir = out / "debug"
        for d in (self.png_dir, self.raw_dir, self.debug_dir):
            d.mkdir(parents=True, exist_ok=True)
        self.total = total
        self.done = 0
        self.lock = threading.Lock()
        self.debug_counts: Counter = Counter()

    def tick(self, rec):
        with self.lock:
            self.done += 1
            if self.done % 25 == 0 or self.done == self.total:
                print(f"  [{self.done}/{self.total}] último: {rec['id']} -> {rec['status']}", flush=True)

    def dump(self, kind, category, pkg_id, text, control=False, limit=4):
        with self.lock:
            if not control and self.debug_counts[(kind, category)] >= limit:
                return
            self.debug_counts[(kind, category)] += 1
        name = f"{safe_name(pkg_id)}.{kind}.{category}.yaml"
        (self.debug_dir / name).write_text(text, encoding="utf-8")


def safe_name(value: str) -> str:
    return re.sub(r"[^A-Za-z0-9._-]", "_", value)


def probe_one(pkg, ctx: Ctx):
    rec = {
        "id": pkg["id"], "control": bool(pkg.get("control")), "status": "", "detail": "",
        "version": "", "manifest_path": "", "n_icons": 0, "icon_url": "", "icon_host": "",
        "icon_type": "", "icon_resolution": "", "icon_theme": "", "icon_bytes": 0,
        "sha_ok": "", "png": False,
    }

    def fail(status, detail=""):
        rec["status"] = status
        rec["detail"] = str(detail)[:240]
        ctx.tick(rec)
        return rec

    if not pkg["hash"]:
        return fail("sem_hash_no_indice")

    vd_url = f"{ctx.cdn}/packages/{pkg['id']}/{pkg['hash'][:8]}/versionData.mszyml"
    status, data, err = http_get(vd_url)
    if data is None:
        return fail(f"versiondata_http_{status}" if status else "versiondata_rede", err)
    try:
        vd_text, _ = decode_payload(data)
        entry = pick_version(parse_yaml(vd_text), pkg.get("latest"))
    except DecodeError as exc:
        return fail("versiondata_decode", exc)
    except Exception as exc:  # noqa: BLE001
        return fail("versiondata_yaml", f"{type(exc).__name__}: {exc}")
    ctx.dump("versionData", "amostra", pkg["id"], vd_text, control=rec["control"], limit=3)

    rp = str(entry.get("rP") or "").lstrip("/")
    rec["version"] = str(entry.get("v") or "")
    if not rp:
        return fail("versiondata_sem_rP")
    rec["manifest_path"] = rp

    status, data, err = http_get(f"{ctx.cdn}/{rp}")
    if data is None:
        return fail(f"manifest_http_{status}" if status else "manifest_rede", err)
    try:
        mf_text, _ = decode_payload(data)
        manifest = parse_yaml(mf_text)
    except DecodeError as exc:
        return fail("manifest_decode", exc)
    except Exception as exc:  # noqa: BLE001
        return fail("manifest_yaml", f"{type(exc).__name__}: {exc}")

    icons = find_icons(manifest)
    if not icons:
        ctx.dump("manifest", "sem_icons", pkg["id"], mf_text, control=rec["control"])
        return fail("sem_icons_no_manifest")
    rec["n_icons"] = len(icons)
    icon = pick_icon(icons)
    if not icon:
        ctx.dump("manifest", "icons_sem_url", pkg["id"], mf_text, control=rec["control"])
        return fail("icons_sem_url_https")

    url = str(icon["IconUrl"])
    rec.update(
        icon_url=url, icon_host=urlparse(url).netloc,
        icon_type=(icon.get("IconFileType") or "").lower(),
        icon_resolution=icon.get("IconResolution") or "",
        icon_theme=icon.get("IconTheme") or "",
    )
    status, blob, err = http_get(url, max_bytes=MAX_ICON_BYTES)
    if blob is None:
        return fail(f"icone_http_{status}" if status else "icone_rede", err)
    rec["icon_bytes"] = len(blob)

    expected = str(icon.get("IconSha256") or "").strip().lower()
    if expected:
        rec["sha_ok"] = "sim" if hashlib.sha256(blob).hexdigest() == expected else "nao"
    else:
        rec["sha_ok"] = "sem_hash"

    ext = re.sub(r"[^a-z0-9]", "", rec["icon_type"]) or (Path(urlparse(url).path).suffix.lstrip(".").lower() or "bin")
    (ctx.raw_dir / f"{safe_name(pkg['id'])}.{ext}").write_bytes(blob)
    try:
        png = to_png(blob)
    except Exception as exc:  # noqa: BLE001
        return fail("icone_conversao", f"{type(exc).__name__}: {exc}")
    (ctx.png_dir / f"{safe_name(pkg['id'])}.png").write_bytes(png)
    rec["png"] = True
    ctx.dump("manifest", "ok", pkg["id"], mf_text, control=rec["control"])
    rec["status"] = "ok"
    ctx.tick(rec)
    return rec


# ---------------------------------------------------------------- relatório

def pct(part, whole):
    return f"{(100.0 * part / whole):.1f}%" if whole else "n/a"


def build_summary(records, meta) -> str:
    body = [r for r in records if not r["control"]]
    controls = [r for r in records if r["control"]]
    counts = Counter(r["status"] for r in body)
    ok = counts.get("ok", 0)
    lines = [
        "# Sondagem de ícones da CDN oficial do winget",
        "",
        f"- Origem da amostra: {meta['ids_source']}",
        f"- Pacotes no índice: {meta['index_packages']}",
        f"- Pacotes sondados (sem controles): {len(body)}",
        f"- **Cobertura: {ok} de {len(body)} ({pct(ok, len(body))}) com ícone baixado e convertido**",
        f"- Ids pedidos que não existem no índice: {meta['unmatched']}",
        "",
        "## Resultado por status",
        "",
        "| Status | Pacotes | % |",
        "|---|---:|---:|",
    ]
    for status, n in counts.most_common():
        lines.append(f"| {status} | {n} | {pct(n, len(body))} |")

    hosts = Counter(r["icon_host"] for r in body if r["icon_host"])
    if hosts:
        lines += ["", "## Hosts dos ícones encontrados", "", "| Host | Pacotes |", "|---|---:|"]
        lines += [f"| {h} | {n} |" for h, n in hosts.most_common(10)]
    types = Counter(r["icon_type"] or "?" for r in body if r["icon_url"])
    if types:
        lines += ["", "Tipos declarados: " + ", ".join(f"{t}={n}" for t, n in types.most_common())]
    sha = Counter(r["sha_ok"] for r in body if r["sha_ok"])
    if sha:
        lines += ["", "Conferência do SHA256 do ícone: " + ", ".join(f"{k}={n}" for k, n in sha.most_common())]

    if controls:
        lines += ["", "## Controles", "", "| Id | Status | Versão | Host do ícone |", "|---|---|---|---|"]
        lines += [f"| {r['id']} | {r['status']} | {r['version']} | {r['icon_host']} |" for r in controls]

    failed = [r for r in body if r["status"] != "ok"]
    if failed:
        lines += ["", "## Exemplos de falha (até 15)", "", "| Id | Status | Detalhe |", "|---|---|---|"]
        for r in failed[:15]:
            lines.append(f"| {r['id']} | {r['status']} | {r['detail'].replace('|', '/')} |")
    lines += [
        "",
        "Os YAML de amostra estão em `debug/` e os ícones convertidos em `png/` (artifact do run).",
    ]
    return "\n".join(lines) + "\n"


def write_outputs(out: Path, records, meta):
    fields = list(records[0].keys()) if records else []
    with open(out / "results.csv", "w", newline="", encoding="utf-8") as fh:
        writer = csv.DictWriter(fh, fieldnames=fields)
        writer.writeheader()
        writer.writerows(records)
    body = [r for r in records if not r["control"]]
    ok = sum(1 for r in body if r["status"] == "ok")
    report = {
        "meta": meta,
        "sondados": len(body),
        "com_icone": ok,
        "cobertura_pct": round(100.0 * ok / len(body), 2) if body else None,
        "status": dict(Counter(r["status"] for r in body)),
    }
    (out / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    summary = build_summary(records, meta)
    (out / "summary.md").write_text(summary, encoding="utf-8")
    step_summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if step_summary:
        with open(step_summary, "a", encoding="utf-8") as fh:
            fh.write(summary)
    print("\n" + summary, flush=True)


# ---------------------------------------------------------------- main

def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default="probe-out")
    ap.add_argument("--sample", type=int, default=300, help="tamanho da amostra (0 = todos)")
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--workers", type=int, default=8)
    ap.add_argument("--ids", default="", help="lista de PackageIdentifier separados por vírgula")
    ap.add_argument("--ids-file", default="", help="arquivo .txt (um id por linha) ou .json")
    ap.add_argument("--apps-url", default=DEFAULT_APPS_URL, help="URL do apps.json do catálogo (vazio = amostra aleatória do índice)")
    ap.add_argument("--cdn", default=os.environ.get("WINGET_CDN", DEFAULT_CDN))
    ap.add_argument("--msix-url", default="", help="padrão: <cdn>/source2.msix")
    args = ap.parse_args(argv)

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    msix_url = args.msix_url or f"{args.cdn.rstrip('/')}/source2.msix"

    db_path = fetch_index(msix_url, out)
    try:
        packages = read_packages(db_path, out / "schema.txt")
    except SchemaError as exc:
        print(f"ERRO de schema: {exc}", file=sys.stderr)
        return 2
    db_path.unlink(missing_ok=True)
    index_map = {p["id"].lower(): p for p in packages}
    with_hash = sum(1 for p in packages if p["hash"])
    print(f"Índice: {len(packages)} pacotes, {with_hash} com hash de versionData.", flush=True)

    wanted, source = load_wanted_ids(args)
    unmatched = 0
    if wanted:
        pool = [index_map[i.lower()] for i in wanted if i.lower() in index_map]
        unmatched = len(wanted) - len(pool)
        print(f"Ids pedidos: {len(wanted)} ({unmatched} fora do índice).", flush=True)
    else:
        pool = list(packages)

    controls = [dict(index_map[c.lower()], control=True) for c in CONTROL_IDS if c.lower() in index_map]
    control_keys = {c["id"].lower() for c in controls}
    pool = [p for p in pool if p["id"].lower() not in control_keys]
    random.Random(args.seed).shuffle(pool)
    if args.sample and args.sample > 0:
        pool = pool[: args.sample]
    work = controls + pool
    if not work:
        print("Nada para sondar.", file=sys.stderr)
        return 2

    print(f"Sondando {len(work)} pacotes ({len(controls)} controles) com {args.workers} threads...", flush=True)
    ctx = Ctx(args.cdn, out, len(work))
    with ThreadPoolExecutor(max_workers=max(1, args.workers)) as ex:
        records = list(ex.map(lambda p: probe_one(p, ctx), work))

    meta = {
        "ids_source": source, "index_packages": len(packages), "with_hash": with_hash,
        "unmatched": unmatched, "sample": args.sample, "seed": args.seed, "cdn": args.cdn,
        "generated_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    }
    write_outputs(out, records, meta)
    return 0


if __name__ == "__main__":
    sys.exit(main())
