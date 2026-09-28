"""Valida o catálogo Office próprio antes de publicá-lo no R2."""

import json
import re
import sys
from pathlib import Path

SAFE_ID = re.compile(r"^[A-Za-z0-9._-]{2,100}$")
CATEGORIES = {"Corporate365", "Ltsc", "Personal", "VisioProject"}


def main() -> int:
    path = Path(sys.argv[1] if len(sys.argv) > 1 else "data/office/catalog.json")
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        products = data["products"]
        offers = data.get("storeOffers", [])
        if data.get("schemaVersion") not in (1, 2) or not isinstance(products, list) or len(products) > 200:
            raise ValueError("Versão/formato do catálogo inválido")
        product_ids = set()
        for item in products:
            pid = item.get("productId", "")
            if not SAFE_ID.fullmatch(pid) or pid.casefold() in product_ids:
                raise ValueError(f"Product ID ausente, inválido ou duplicado: {pid}")
            product_ids.add(pid.casefold())
            if not item.get("displayName") or item.get("category") not in CATEGORIES:
                raise ValueError(f"Produto sem nome ou categoria válida: {pid}")
            if item.get("channel") and not SAFE_ID.fullmatch(item["channel"]):
                raise ValueError(f"Canal inválido em {pid}")
            for field in ("iconUrl", "bannerUrl"):
                value = item.get(field)
                if value and not value.startswith("https://"):
                    raise ValueError(f"{field} precisa ser HTTPS em {pid}")

        store_ids = set()
        for offer in offers:
            sid = offer.get("storeProductId", "")
            linked = offer.get("odtProductId", "")
            if not SAFE_ID.fullmatch(sid) or sid.casefold() in store_ids:
                raise ValueError(f"Store Product ID inválido ou duplicado: {sid}")
            store_ids.add(sid.casefold())
            if not offer.get("displayName") or not SAFE_ID.fullmatch(linked):
                raise ValueError(f"Oferta incompleta: {sid}")
            if linked.casefold() not in product_ids and linked.casefold() not in {
                "o365homepremretail", "o365businessretail"
            }:
                raise ValueError(f"Oferta {sid} aponta para Product ID ausente: {linked}")
            for field in ("iconUrl", "bannerUrl"):
                value = offer.get(field)
                if value and not value.startswith("https://"):
                    raise ValueError(f"{field} precisa ser HTTPS em {sid}")

        print(f"Catálogo válido: {len(products)} produto(s), {len(offers)} oferta(s).")
        return 0
    except (OSError, KeyError, TypeError, json.JSONDecodeError, ValueError) as exc:
        print(f"Catálogo Office inválido: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
