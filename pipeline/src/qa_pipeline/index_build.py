"""Azure AI Search index builder (plan §9).

Shapes the currently-indexable sources into §9 documents, embeds
``title + content`` with the deployed ``text-embedding-3-large``, and uploads
to AI Search (manual indexing — chunking is source-specific, per §9):

- **PDSRG chunks** (§6 output) — already §9-stamped (id/authority/products/
  locator/citation_url/is_current/product_status); pass-through plus
  string-normalized ``part_no``s.
- **products.json** (§5) — section-split per family: the canonical SKU's
  sections are the family documents; variants contribute only genuinely
  distinct sections (whitespace-normalized text diff against the canonical's
  same-named section). §5's ``faq_item`` split is data-driven: no FAQ-shaped
  sections exist in the corpus yet, so none are produced.
- **menu descriptions** (§8) — one small doc per menu type (10), with the
  calorie range computed from the CSV.

QA (Stage 2, blocked on the small-chat quota) and podcast (§7 ASR) sources
join later; their records already follow the same §9 field contract.

Determinism: ``documents.jsonl`` (no vectors) is byte-identical across
reruns — pure shaping from committed inputs, sorted by ``id``. Vectors are
API results cached locally (see ``embeddings.py``), and ``runs/`` outputs
are gitignored.
"""

from __future__ import annotations

import csv
import re
import unicodedata
from pathlib import Path
from typing import Any

from azure.search.documents.indexes import models as m

from .alias import strip_variant_suffix

INDEX_NAME = "kb-main"
EMBEDDING_DIMS = 3072          # text-embedding-3-large — the §9 index contract
UPLOAD_BATCH = 200

# §5 section taxonomy; headers not in this map keep a slugified header name
# (deterministic) — real content is never dropped, only empty sections are
SECTION_SLUGS = {
    "description": "description",
    "supplement facts": "supplement_facts",
    "nutrition facts": "nutrition_facts",
    "specifications": "specifications",
    "directions": "directions",
    "suggested use": "directions",
    "faq": "faq",
}


def _slug(text: str) -> str:
    norm = unicodedata.normalize("NFKC", text).casefold()
    slug = re.sub(r"[^a-z0-9]+", "-", norm).strip("-")
    return slug or "section"


def _norm_text(text: str) -> str:
    return re.sub(r"\s+", " ", text).strip()


def embed_text(doc: dict[str, Any]) -> str:
    """The vector input: title rides along for context (product sections
    don't always name their product in body text)."""
    return f"{doc['title']}\n\n{doc['content']}"


# --------------------------------------------------------------------------
# products.json (§5)


def split_sections(searchcontent: str) -> list[tuple[str, str]]:
    """Split markdown ``searchcontent`` into ``(slug, content)`` sections.

    The header line is kept in the content (context for BM25 + vectors).
    Sections with no body text are dropped (the corpus has empty
    ``### Specifications`` wrappers). The headerless preamble, if any,
    is a ``description`` section.
    """
    text = searchcontent.replace("\r\n", "\n").replace("\r", "\n")
    parts = re.split(r"^(#{1,4} .+)$", text, flags=re.M)
    out: list[tuple[str, str]] = []
    preamble = parts[0].strip()
    if preamble:
        out.append(("description", preamble))
    for hdr, body in zip(parts[1::2], parts[2::2]):
        name = hdr.lstrip("#").strip()
        body = body.strip()
        if not body:
            continue
        slug = SECTION_SLUGS.get(name.casefold(), _slug(name))
        out.append((slug, f"{name}\n\n{body}"))
    return out


def product_documents(products: list[dict], families: list[dict]) -> list[dict]:
    """Family-grouped §5 product documents from products.json."""
    by_pn = {str(p["part_no"]): p for p in products}
    docs: list[dict] = []

    def base_doc(pn: str, section: str, title: str, part_nos: list[str]) -> dict:
        p = by_pn[pn]
        return {
            "id": f"product:{pn}:{section}",
            "source_type": "product",
            "authority": 1,                     # §3: legal-approved copy
            "title": title,
            "content": "",                      # filled by caller
            "citation_url": p.get("URL"),
            "locator": None,
            "products": part_nos,
            "topics": [""],                     # filled by caller (family)
            "date": None,                       # products.json has no export date
            "is_current": True,
            "product_status": None,             # no products.json SKU is discontinued
        }

    for fam in sorted(families, key=lambda f: f["family"]):
        family = fam["family"]
        canon = str(fam["canonical_part_no"])
        pns = [str(x) for x in fam["part_nos"]]
        if canon not in by_pn:
            raise ValueError(
                f"family {family!r}: canonical SKU {canon} missing from products.json"
            )
        canon_secs = split_sections(by_pn[canon]["searchcontent"])
        canon_text = {slug: _norm_text(text) for slug, text in canon_secs}
        for slug, text in canon_secs:
            doc = base_doc(canon, slug, family, pns)
            doc["content"] = text
            doc["topics"] = [family]
            docs.append(doc)
        for vpn in sorted(pn for pn in pns if pn != canon):
            variant = by_pn[vpn]
            longname = variant["longname"]
            flavor = longname[len(strip_variant_suffix(longname)):].strip(" -")
            for slug, text in split_sections(variant["searchcontent"]):
                if slug in canon_text and canon_text[slug] == _norm_text(text):
                    continue   # identical to the canonical section — family doc covers it
                doc = base_doc(vpn, slug, longname, [vpn])
                doc["content"] = text
                doc["topics"] = [family]
                doc["locator"] = flavor or None
                docs.append(doc)
    return docs


# --------------------------------------------------------------------------
# PDSRG (§6 output — pass-through) and menus (§8)


def pdsrg_documents(chunks: list[dict]) -> list[dict]:
    docs = []
    for c in chunks:
        docs.append({
            "id": c["id"],
            "source_type": "pdsrg",
            "authority": c.get("authority", 2),
            "title": c.get("title"),
            "content": c["content"],
            "citation_url": c.get("citation_url"),
            "locator": c.get("locator"),
            "products": [str(p) for p in (c.get("products") or [])],
            "topics": c.get("topics") or [],
            "date": c.get("date"),
            "is_current": bool(c.get("is_current", True)),
            "product_status": c.get("product_status"),
        })
    return docs


def menu_documents(rows: list[dict]) -> list[dict]:
    """One §8 description doc per menu type: name + description + calorie range.

    The export carries case-variant duplicate names (``Gluten Free`` /
    ``Gluten free``, ``Night Out`` / ``Night out`` — identical descriptions),
    which would otherwise produce duplicate document ids (the slug is already
    casefolded). Case-insensitive grouping with the dominant spelling as the
    display name (most rows; tie → lexicographic) keeps the §8 count at 10.
    """
    groups: dict[str, dict[str, Any]] = {}
    for r in rows:
        name = r["menu_name"].strip()
        g = groups.setdefault(name.casefold(),
                              {"count": {}, "descr": {}, "cals": set()})
        g["count"][name] = g["count"].get(name, 0) + 1
        g["descr"].setdefault(name, r["menu_descr"].strip())
        g["cals"].add(int(r["menu_calories"]))
    docs = []
    for key in sorted(groups):
        g = groups[key]
        name = sorted(g["count"].items(), key=lambda kv: (-kv[1], kv[0]))[0][0]
        descr, cals = g["descr"][name], g["cals"]
        lo, hi = min(cals), max(cals)
        content = (f"{descr}\n\nAvailable calorie targets range from "
                   f"{lo:,} to {hi:,} calories across {len(cals)} levels.")
        docs.append({
            "id": f"menu_desc:{_slug(name)}:description",
            "source_type": "menu_desc",
            "authority": None,                  # §3 assigns no authority to menus
            "title": name,
            "content": content,
            "citation_url": None,
            "locator": None,
            "products": [],
            "topics": ["menus"],
            "date": None,
            "is_current": True,
            "product_status": None,
        })
    return docs


def read_menu_rows(path: Path) -> list[dict]:
    """Menu CSV rows (``;``-delimited, quoted). The export in the wild is
    Windows-1252 (en-dashes in descriptions); UTF-8 (± BOM) is tried first so
    a future UTF-8 re-export just works, then cp1252."""
    for encoding in ("utf-8-sig", "cp1252"):
        try:
            with path.open("r", encoding=encoding, newline="") as f:
                return list(csv.DictReader(f, delimiter=";"))
        except UnicodeDecodeError:
            continue
    raise ValueError(f"menu CSV is neither UTF-8 nor cp1252: {path}")


def build_documents(chunks: list[dict], products: list[dict],
                    families: list[dict], menu_rows: list[dict]) -> list[dict]:
    """All §9 documents, sorted by id (documents.jsonl is byte-stable)."""
    docs = (pdsrg_documents(chunks)
            + product_documents(products, families)
            + menu_documents(menu_rows))
    return sorted(docs, key=lambda d: d["id"])


# --------------------------------------------------------------------------
# AI Search schema + upload (§9)


def index_schema(name: str = INDEX_NAME) -> m.SearchIndex:
    """`kb-main` per §9: hybrid BM25 + vector, int8 scalar quantization with
    rescoring and no stored vector copies, semantic ranker config available
    (query-side toggle is decided on the golden set, open item 5)."""
    vector_search = m.VectorSearch(
        profiles=[m.VectorSearchProfile(
            name="profile-sq", algorithm_configuration_name="hnsw",
            compression_name="sq-int8",
        )],
        algorithms=[m.HnswAlgorithmConfiguration(
            name="hnsw",
            parameters=m.HnswParameters(metric="cosine", m=4,
                                       ef_construction=400, ef_search=500),
        )],
        compressions=[m.ScalarQuantizationCompression(
            compression_name="sq-int8",
            rescoring_options=m.RescoringOptions(rescoring_status="enabled"),
        )],
    )
    semantic = m.SemanticSearch(configurations=[m.SemanticConfiguration(
        name="sem-default",
        prioritized_fields=m.SemanticPrioritizedFields(
            title_fields=[m.SemanticField(field_name="title")],
            content_fields=[m.SemanticField(field_name="content")],
        ),
    )])
    fields = [
        m.SimpleField(name="id", type="Edm.String", key=True),
        m.SimpleField(name="source_type", type="Edm.String",
                      filterable=True, facetable=True),
        m.SimpleField(name="authority", type="Edm.Int32",
                      filterable=True, sortable=True),
        m.SearchableField(name="title", type="Edm.String"),
        m.SearchableField(name="content", type="Edm.String"),
        m.SearchField(name="content_vector", type="Collection(Edm.Single)",
                      searchable=True, stored=False,          # no stored vector copies
                      vector_search_dimensions=EMBEDDING_DIMS,
                      vector_search_profile_name="profile-sq"),
        m.SimpleField(name="citation_url", type="Edm.String", retrievable=True),
        m.SimpleField(name="locator", type="Edm.String", retrievable=True),
        m.SimpleField(name="products", type="Collection(Edm.String)",
                      filterable=True, facetable=True),
        m.SimpleField(name="topics", type="Collection(Edm.String)",
                      filterable=True, facetable=True),
        m.SimpleField(name="date", type="Edm.DateTimeOffset",
                      filterable=True, sortable=True),
        m.SimpleField(name="is_current", type="Edm.Boolean", filterable=True),
        m.SimpleField(name="product_status", type="Edm.String", filterable=True),
    ]
    return m.SearchIndex(name=name, fields=fields,
                          vector_search=vector_search, semantic_search=semantic)


def ensure_index(search_endpoint: str, admin_key: str, name: str,
                 reset: bool = False) -> str:
    """Create the index if missing (or delete + recreate with *reset*).

    Single-resource GET, never a list call: the serverless tier rejects
    index enumeration outright ("cannot enumerate resources without paging").
    """
    from azure.core.credentials import AzureKeyCredential
    from azure.core.exceptions import ResourceNotFoundError
    from azure.search.documents.indexes import SearchIndexClient

    client = SearchIndexClient(search_endpoint, AzureKeyCredential(admin_key))
    if reset:
        try:
            client.delete_index(name)
        except ResourceNotFoundError:
            pass
        client.create_or_update_index(index_schema(name))
        return "recreated"
    try:
        client.get_index(name)
        return "exists"
    except ResourceNotFoundError:
        client.create_or_update_index(index_schema(name))
        return "created"


def upload_documents(search_endpoint: str, admin_key: str, index_name: str,
                     docs: list[dict], vectors: list[list[float]],
                     batch: int = UPLOAD_BATCH) -> tuple[int, list[dict]]:
    """merge_or_upload §9 documents (+vectors) in batches; returns
    (n_succeeded, errors)."""
    from azure.core.credentials import AzureKeyCredential
    from azure.search.documents import SearchClient

    client = SearchClient(search_endpoint, index_name, AzureKeyCredential(admin_key))
    n_ok, errors = 0, []
    for start in range(0, len(docs), batch):
        payload = [{**d, "content_vector": v}
                   for d, v in zip(docs[start:start + batch],
                                   vectors[start:start + batch])]
        for r in client.merge_or_upload_documents(payload):
            if getattr(r, "succeeded", False):
                n_ok += 1
            else:
                errors.append({"key": getattr(r, "key", None),
                               "error": str(getattr(r, "error_message", r))[:200]})
    return n_ok, errors
