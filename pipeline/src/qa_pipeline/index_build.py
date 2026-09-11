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
- **infopages.json** — the dotFIT.com info pages (about/FAQ/policies/learn
  hubs): the same export channel as products.json, so the same §3 weight —
  authority 1 site copy (owner ruling 2026-09-11). Section-split reuses
  ``split_sections``; curated per-page metadata lives in ``PAGE_META``.
- **menu descriptions** (§8) — one small doc per menu type (10), with the
  calorie range computed from the CSV.

Podcast segments (§7 step 3 output) join here as fourth source; QA
(Stage 2, blocked on the small-chat quota) joins later under the same
§9 field contract.

AI Search document keys may only contain letters, digits, ``_``, ``-`` and
``=`` (``InvalidDocumentKey`` otherwise), so §9's colon-separated id style
(``pdsrg:stem:001``) is mapped to dashes at index time: ``pdsrg-stem-001``.
The committed source records keep their ids; the substitution is purely
mechanical and reversible. A test pins the key rule on every built id.

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
from .podcast import citation_url

# The §9 index. `kb-main` was the original name — its delete wedged mid-flight
# (open item 10, closed 2026-09-08: the orphan is gone, the name is free again)
# and the corpus was rebuilt under `-v2`. The default stays `-v2`; a rename is
# cosmetic and is pinned by tests on both sides of the runtime mirror.
INDEX_NAME = "kb-main-v2"
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
            "id": f"product-{pn}-{section}",
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
            "id": c["id"].replace(":", "-"),  # colon keys are illegal in AI Search
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


# --------------------------------------------------------------------------
# infopages.json (dotFIT.com info pages — same export channel as products.json)

# Curated per-page metadata, keyed by the export's ``coid`` (stable page id).
# ``title`` is the display form of the export ``longname`` — coid 41964's
# longname is truncated mid-sentence in the export (and carries literal
# ``<br>`` tags), the rest are dash/case cleanups; ``topic`` is the page-class
# facet. An unknown coid raises rather than emit untagged docs — the STEM_META
# rule: an unattested mapping must never silently pass (alias lesson,
# 2026-09-01).
PAGE_META: dict[int, dict] = {
    # about / brand
    41819: {"title": "Nutrition Solutions For Exercisers and Athletes",
            "topic": "about"},
    42026: {"title": "dotFIT Difference", "topic": "about"},
    # partner / certification programs
    41952: {"title": "Become a dotFIT Licensed Partner", "topic": "partner"},
    41964: {"title": "fibrPRO: Get Paid for Your Guidance",
            "topic": "partner"},          # export longname truncated mid-sentence
    41951: {"title": "Become dotFIT Certified", "topic": "certification"},
    41223: {"title": "Masterclass - Recorded Webinars", "topic": "education"},
    41701: {"title": "Infographics", "topic": "education"},
    3968: {"title": "dotFIT FAQs", "topic": "faq"},
    # learn hubs (navigation stubs + category blurbs)
    3954: {"title": "Learn", "topic": "learn"},
    38566: {"title": "Ask the Experts", "topic": "learn"},
    38594: {"title": "Keto | Paleo | Atkins", "topic": "learn"},
    4149: {"title": "General Health & Fitness", "topic": "learn"},
    4221: {"title": "Muscle Gain", "topic": "learn"},
    4186: {"title": "Performance & Sports Nutrition", "topic": "learn"},
    4458: {"title": "Healthy Recipes", "topic": "learn"},
    4148: {"title": "Supplements", "topic": "learn"},
    3939: {"title": "Weight Loss", "topic": "learn"},
    # site policies
    3966: {"title": "dotFIT Privacy Policy", "topic": "policy"},
    3965: {"title": "dotFIT Return/Refund Policy", "topic": "policy"},
    3821: {"title": "dotFIT Terms and Conditions of Use", "topic": "policy"},
}


def _strip_page_h1(searchcontent: str) -> str:
    """Drop a leading ``#`` page-title header (newline-normalized first).

    Every info page opens with ``# <page title>`` — the products.json shape
    has no such header, so stripping it turns the page's intro prose into the
    ``description`` preamble section and leaves content sections starting at
    ``##``/``###``, exactly what ``split_sections`` was shaped on.
    """
    text = searchcontent.replace("\r\n", "\n").replace("\r", "\n")
    text = text.lstrip("\n")
    m = re.match(r"^# .+\n", text)
    return text[m.end():] if m else text


def infopage_documents(pages: list[dict]) -> list[dict]:
    """dotFIT.com info pages (infopages.json) -> §9 docs (``authority=1``).

    The §9 stamp mirrors the product source the pages share an export channel
    with: authority 1 legal-approved site copy (owner ruling 2026-09-11), no
    date (the export carries none), ``is_current=True``, no product_status,
    no ``products`` tags (the pages carry no part_nos; the FAQ page names
    SKUs in prose, but deterministic tagging of site copy is future work on
    the podcast precedent). ``citation_url`` is the page's public URL, so —
    unlike QA/podcast — these docs cite to live links. ``locator`` is the
    section heading (the header line ``split_sections`` keeps in the content);
    the description preamble has no heading, so it gets ``None``.
    """
    docs: list[dict] = []
    for page in sorted(pages, key=lambda p: p["coid"]):
        coid = page["coid"]
        meta = PAGE_META.get(coid)
        if meta is None:
            raise ValueError(
                f"infopage coid {coid} ({page.get('longname')!r}) is not in "
                "PAGE_META in qa_pipeline/index_build.py — curate it before "
                "indexing")
        seen: set[str] = set()
        for slug, text in split_sections(_strip_page_h1(page["searchcontent"])):
            if slug in seen:
                raise ValueError(
                    f"infopage coid {coid}: two headers collapse to section "
                    f"slug {slug!r} — one document id would silently win")
            seen.add(slug)
            header = text.partition("\n")[0]
            docs.append({
                "id": f"infopage-{coid}-{slug}",
                "source_type": "infopage",
                "authority": 1,                     # §3: legal-approved site copy
                "title": meta["title"],
                "content": text,
                "citation_url": page.get("URL"),
                "locator": None if slug == "description" else header,
                "products": [],
                "topics": [meta["topic"]],
                "date": None,                       # infopages.json has no export date
                "is_current": True,
                "product_status": None,
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
    for key, g in groups.items():
        if len(set(g["descr"].values())) > 1:
            raise ValueError(
                f"menu {key!r}: case-variant names carry different "
                f"descriptions {sorted(set(g['descr'].values()))!r} — "
                f"resolve the export before indexing")
    docs = []
    for key in sorted(groups):
        g = groups[key]
        name = sorted(g["count"].items(), key=lambda kv: (-kv[1], kv[0]))[0][0]
        descr, cals = g["descr"][name], g["cals"]
        lo, hi = min(cals), max(cals)
        content = (f"{descr}\n\nAvailable calorie targets range from "
                   f"{lo:,} to {hi:,} calories across {len(cals)} levels.")
        docs.append({
            "id": f"menu_desc-{_slug(name)}-description",
            "source_type": "menu_desc",
            "authority": 5,                       # §3: menus rank last
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


def podcast_documents(segments: list[dict],
                      video_ids: dict[str, str] | None = None) -> list[dict]:
    """§7 segments → §9 docs (``authority=4``).

    - ``id`` gets a ``podcast-`` namespace prefix: segment ids are
      slug-based and can start with a digit (``1-expert-reacts…``).
    - ``title`` carries the mm:ss range so result lists disambiguate
      segments of one episode; ``locator`` is the citable time range.
    - ``citation_url`` is the episode's YouTube link, deep-linked to the
      segment's start second so §7.4's "as covered at 14:32 in *Creatine
      FAQs*" lands where it says. The ``archive.txt`` → video mapping was
      verified 2026-09-08 and frozen as ``podcast.PODCAST_VIDEO_IDS``; an
      episode absent from it raises rather than silently citing linkless.
    - ``products``/``topics`` stay empty: spoken text gets no
      deterministic alias tagging (future work, same policy as the
      corpus-never-rewritten rule).
    - ``is_current`` is True: v1 has no supersession logic for episodes,
      and an unstamped (null) doc is invisible to filtered queries.
    """
    docs = []
    for s in segments:
        docs.append({
            "id": f"podcast-{s['id']}",
            "source_type": "podcast",
            "authority": 4,                      # §3: podcast transcripts
            "title": f"{s['episode_title']} ({s['start']}–{s['end']})",
            "content": s["text"],
            "citation_url": citation_url(s["source_file"], s["start_ms"],
                                         video_ids),
            "locator": f"{s['start']}–{s['end']}",
            "products": [],
            "topics": [],
            "date": None,                       # §9: nullable for podcast
            "is_current": True,
            "product_status": None,
        })
    return docs


def qa_date(value: str | None) -> str | None:
    """Stage 2 ``thread_date`` (``YYYY-MM-DD``) -> ``DateTimeOffset``.

    QA carries the first real dates in the index (every other source stamps
    null); AI Search needs the full offset shape, midnight UTC — the thread
    date is day-precision by construction (plan §4: the enquiry's ``Sent:``
    header, a currency lower bound, never a timestamp). Garbage stays null
    rather than failing the build.
    """
    if not value or not re.fullmatch(r"\d{4}-\d{2}-\d{2}", value):
        return None
    return f"{value}T00:00:00Z"


QA_PART_CHARS = 20_000  # answer part size: title + part must clear Embedder.MAX_INPUT_CHARS


def split_answer_parts(answer: str, max_chars: int = QA_PART_CHARS) -> list[str]:
    """Paragraph-boundary split for answers that would overflow the embedding
    input cap (a few expert notes are slide decks, not Q&A pairs). Greedy
    accumulation; a single over-long paragraph hard-splits at the cap so the
    fit is guaranteed, never silent truncation — every part indexes."""
    paras = [p for p in answer.split("\n\n") if p.strip()]
    parts, current = [], ""
    for para in paras:
        while len(para) > max_chars:  # one giant paragraph: hard-split
            parts.append(para[:max_chars])
            para = para[max_chars:]
        if current and len(current) + 2 + len(para) > max_chars:
            parts.append(current)
            current = ""
        current = f"{current}\n\n{para}" if current else para
    if current.strip():
        parts.append(current)
    return parts or [answer]


def qa_documents(records: list[dict]) -> list[dict]:
    """Stage 2 canonical records (§4 Stage 5) -> §9 docs, one per Q&A pair.

    No chunking (pairs are already the right size); ``question_canonical``
    and ``answer`` are both searchable via title+content. Expert notes with
    no canonical question fall back to the filename (a load-bearing topic
    summary, not a rewrite). Records with no answer text are skipped — there
    is nothing to retrieve (Stage 1 already excludes answer-less docs, so
    this is belt-and-braces). Stage 4-superseded records
    (``is_current=false``) are skipped: §4 keeps them in the committed store
    (the "what happened to X" audit trail) but only ``is_current=true``
    canonicals proceed to the index. Currency cues never reach the index —
    they are Stage 4 input, not query text. ``citation_url`` stays null (no
    verified link — podcast precedent).
    """
    docs = []
    for r in records:
        answer = (r.get("answer") or "").strip()
        if not answer:
            continue
        if not r.get("is_current", True):
            continue
        question = (r.get("question_canonical") or "").strip()
        title = question or r.get("filename") or r["source_file"]
        base = {
            "source_type": "qa",
            "authority": 3,                       # §3: QA corpus
            "citation_url": None,
            "locator": r.get("filename"),
            "products": [str(p) for p in (r.get("products") or [])],
            "topics": r.get("topics") or [],
            "date": qa_date(r.get("thread_date")),
            "is_current": True,
            "product_status": None,
        }
        parts = split_answer_parts(answer)
        if len(parts) == 1:  # the common case keeps the stable short id
            docs.append({"id": f"qa-{r['id']}", "title": title,
                         "content": parts[0], **base})
        else:
            for n, part in enumerate(parts, 1):
                docs.append({"id": f"qa-{r['id']}-p{n}",
                             "title": f"{title} (part {n} of {len(parts)})",
                             "content": part, **base})
    return docs


def build_documents(chunks: list[dict], products: list[dict],
                    families: list[dict], menu_rows: list[dict],
                    podcast_segments: list[dict] | None = None,
                    qa_records: list[dict] | None = None,
                    podcast_video_ids: dict[str, str] | None = None,
                    infopages: list[dict] | None = None) -> list[dict]:
    """All §9 documents, sorted by id (documents.jsonl is byte-stable)."""
    docs = (pdsrg_documents(chunks)
            + product_documents(products, families)
            + menu_documents(menu_rows)
            + podcast_documents(podcast_segments or [], podcast_video_ids)
            + qa_documents(qa_records or [])
            + infopage_documents(infopages or []))
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
        m.SimpleField(name="id", type="Edm.String", key=True, sortable=True),
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


def _deletion_pending(exc: Exception) -> bool:
    """True when a 404 means "still deleting", not "gone".

    Azure answers `GET /indexes/<name>` with 404 for both a clean miss and an
    in-flight delete, and the SDK raises the same `ResourceNotFoundError` for
    each — only the body separates them ("The index ... is being deleted."
    vs "No index with the name ... was found."). Reading the pending 404 as
    "gone" is what let the 2026-09-08 rebuild create into a half-deleted
    index; the delete then never completed and kb-main was lost.
    """
    return "being deleted" in str(exc).lower()


def ensure_index(search_endpoint: str, admin_key: str, name: str,
                 reset: bool = False, poll_seconds: float = 2.0,
                 timeout: float = 120.0) -> str:
    """Create the index if missing (or delete + recreate with *reset*).

    Single-resource GET, never a list call: the serverless tier rejects
    index enumeration outright ("cannot enumerate resources without paging").
    Deletion is asynchronous server-side — a reset must poll the old index
    away before creating the new one, or create fails with a bare "could not
    be created" (the 2026-09-07 rebuild race) and the service is left with
    NO index at all. The poll distinguishes the two 404 bodies via
    `_deletion_pending`; a delete that never finishes raises rather than
    creating into it.
    """
    import time
    from azure.core.credentials import AzureKeyCredential
    from azure.core.exceptions import ResourceNotFoundError
    from azure.search.documents.indexes import SearchIndexClient

    client = SearchIndexClient(search_endpoint, AzureKeyCredential(admin_key))
    if reset:
        try:
            client.delete_index(name)
        except ResourceNotFoundError as exc:
            pending = _deletion_pending(exc)
        else:
            pending = True
        if pending:
            deadline = time.monotonic() + timeout
            while True:
                try:
                    client.get_index(name)
                except ResourceNotFoundError as exc:
                    if not _deletion_pending(exc):
                        break
                if time.monotonic() >= deadline:
                    raise TimeoutError(
                        f"index {name} still present {timeout:g}s after "
                        "deletion — aborting before create")
                time.sleep(poll_seconds)
        delay = 1.0
        for attempt in range(5):
            try:
                client.create_or_update_index(index_schema(name))
                return "recreated"
            except Exception:  # noqa: BLE001 — residual propagation races
                if attempt == 4:
                    raise
                time.sleep(delay)
                delay = min(delay * 2, 15.0)
    try:
        client.get_index(name)
        return "exists"
    except ResourceNotFoundError as exc:
        if _deletion_pending(exc):
            raise RuntimeError(
                f"index {name} is mid-delete server-side — creating into it "
                "is what corrupts the index; wait for the delete to finish "
                "or build under --index-name") from exc
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


def list_index_ids(search_endpoint: str, admin_key: str, index_name: str,
                   page: int = 1000) -> list[str]:
    """Every document id currently in the index, sorted (paginated scan).

    Prune's source of truth: what the index physically holds vs the freshly
    uploaded documents.jsonl. ``id`` must be sortable (schema) — skip-based
    pagination without order_by is unspecified and cannot be trusted to
    neither miss nor repeat ids.
    """
    from azure.core.credentials import AzureKeyCredential
    from azure.search.documents import SearchClient

    client = SearchClient(search_endpoint, index_name, AzureKeyCredential(admin_key))
    ids: set[str] = set()
    skip = 0
    while True:
        page_ids = [r["id"] for r in client.search(
            search_text="*", select=["id"], top=page, skip=skip,
            order_by=["id"])]
        ids.update(page_ids)
        if len(page_ids) < page:
            return sorted(ids)
        skip += page


def delete_documents(search_endpoint: str, admin_key: str, index_name: str,
                     keys: list[str], batch: int = UPLOAD_BATCH
                     ) -> tuple[int, list[dict]]:
    """Delete *keys* from the index in batches; returns (n_deleted, errors)."""
    from azure.core.credentials import AzureKeyCredential
    from azure.search.documents import SearchClient

    client = SearchClient(search_endpoint, index_name, AzureKeyCredential(admin_key))
    n_ok, errors = 0, []
    for start in range(0, len(keys), batch):
        payload = [{"id": k} for k in keys[start:start + batch]]
        for r in client.delete_documents(payload):
            if getattr(r, "succeeded", False):
                n_ok += 1
            else:
                errors.append({"key": getattr(r, "key", None),
                               "error": str(getattr(r, "error_message", r))[:200]})
    return n_ok, errors
