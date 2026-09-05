"""Index builder tests (plan §9) — synthetic fixtures, no network, no real claims."""

from __future__ import annotations

import pytest

from qa_pipeline.embeddings import Embedder
from qa_pipeline.index_build import (
    INDEX_NAME, build_documents, embed_text, ensure_index, index_schema,
    menu_documents, pdsrg_documents, product_documents, read_menu_rows,
    split_sections, upload_documents,
)


def _product(part_no, longname, searchcontent, url="https://www.dotfit.com/x"):
    return {"part_no": part_no, "longname": longname, "coid": 1,
            "URL": url, "searchcontent": searchcontent}


CANON = ("### Description\r\n\r\nSupports recovery.\r\n\r\n"
         "## Supplement Facts\r\n\r\n| a | b |\r\n\r\n"
         "### Specifications\r\n\r\n\r\n")
VARIANT_SAME = ("### Description\r\n\r\nSupports recovery.\r\n\r\n"
                "## Supplement Facts\r\n\r\n| a | b |\r\n")
VARIANT_DIFF = ("### Description\r\n\r\nSupports recovery.\r\n\r\n"
                "## Supplement Facts\r\n\r\n| a | c |\r\n\r\n"
                "### Flavor Notes\r\n\r\nTastes fine.\r\n")

PRODUCTS = [
    _product(1333, "LeanMeal Nutrition Shake - Chocolate", CANON),
    _product(1334, "LeanMeal Nutrition Shake - Vanilla", VARIANT_SAME),
    _product(1335, "LeanMeal Nutrition Shake - Banana", VARIANT_DIFF),
]

FAMILIES = [{"family": "LeanMeal Nutrition Shake", "canonical_part_no": 1333,
             "part_nos": [1333, 1334, 1335], "n_variants": 3}]


# --- split_sections ---------------------------------------------------------


def test_split_sections_slug_map_empty_dropped_table_kept():
    secs = split_sections(CANON)
    assert [s for s, _ in secs] == ["description", "supplement_facts"]
    assert secs[0][1] == "Description\n\nSupports recovery."
    assert secs[1][1] == "Supplement Facts\n\n| a | b |"  # table verbatim


def test_split_sections_preamble_and_unknown_header():
    secs = split_sections("Intro prose.\r\n### Product Information\r\nBody.")
    assert secs == [("description", "Intro prose."),
                    ("product-information", "Product Information\n\nBody.")]


# --- product_documents (§5 family grouping) --------------------------------


def test_product_documents_family_and_variant_rules():
    docs = product_documents(PRODUCTS, FAMILIES)
    ids = [d["id"] for d in docs]

    # canonical SKU's sections are the family documents
    fam = next(d for d in docs if d["id"] == "product-1333-description")
    assert fam["title"] == "LeanMeal Nutrition Shake"
    assert fam["products"] == ["1333", "1334", "1335"]
    assert fam["authority"] == 1 and fam["is_current"] is True
    assert fam["citation_url"] == "https://www.dotfit.com/x"
    assert fam["date"] is None and fam["product_status"] is None

    # identical variant sections contribute nothing (vanilla)
    assert not [i for i in ids if i.startswith("product-1334-")]

    # genuinely distinct variant sections are kept, tagged to the variant
    banana_facts = next(d for d in docs if d["id"] == "product-1335-supplement_facts")
    assert banana_facts["products"] == ["1335"]
    assert banana_facts["title"] == "LeanMeal Nutrition Shake - Banana"
    assert banana_facts["locator"] == "Banana"
    assert "product-1335-flavor-notes" in ids
    # family part_nos stay strings throughout (query filters are strings)
    assert all(isinstance(pn, str) for d in docs for pn in d["products"])


def test_product_documents_missing_canonical_raises():
    with pytest.raises(ValueError, match="missing from products.json"):
        product_documents(PRODUCTS, [{"family": "Ghost", "canonical_part_no": 9999,
                                      "part_nos": [9999], "n_variants": 1}])


# --- pdsrg / menu documents -------------------------------------------------


def test_pdsrg_documents_passthrough_with_string_part_nos():
    chunk = {"id": "pdsrg:activemv:001", "authority": 2, "title": "Intro",
             "content": "body", "citation_url": "pdsrg/ActiveMV.pdf#page=1",
             "locator": "Active MV (p. 1)", "products": [1005], "topics": ["MVM"],
             "date": None, "is_current": True, "product_status": "discontinued"}
    doc = pdsrg_documents([chunk])[0]
    assert doc["id"] == "pdsrg-activemv-001"       # colons mapped to dashes
    assert doc["products"] == ["1005"]          # int part_no -> string
    assert doc["authority"] == 2 and doc["source_type"] == "pdsrg"
    assert doc["product_status"] == "discontinued"
    assert doc["is_current"] is True


def test_menu_documents_case_variant_names_merge_deterministically():
    rows = [
        {"menu_name": "Night Out", "menu_descr": "Planned night.",
         "menu_calories": "4500"},
        {"menu_name": "Night out", "menu_descr": "Planned night.",
         "menu_calories": "1000"},
        {"menu_name": "Night out", "menu_descr": "Planned night.",
         "menu_calories": "2000"},
    ]
    docs = menu_documents(rows)
    assert len(docs) == 1                     # one doc despite spelling drift
    d = docs[0]
    assert d["id"] == "menu_desc-night-out-description"
    assert d["title"] == "Night out"          # dominant spelling (2 of 3 rows)
    assert "1,000 to 4,500 calories across 3 levels" in d["content"]


def test_menu_documents_one_per_type_with_calorie_range():
    rows = [
        {"menu_name": "Heart Healthy", "menu_descr": "Heart healthy.",
         "menu_calories": "1000"},
        {"menu_name": "Heart Healthy", "menu_descr": "Heart healthy.",
         "menu_calories": "1500"},
        {"menu_name": "Plant Forward", "menu_descr": "Plants.",
         "menu_calories": "1200"},
    ]
    docs = menu_documents(rows)
    assert [d["source_type"] for d in docs] == ["menu_desc", "menu_desc"]
    hh = next(d for d in docs if d["title"] == "Heart Healthy")
    assert "1,000 to 1,500 calories across 2 levels" in hh["content"]
    assert hh["authority"] is None and hh["topics"] == ["menus"]
    assert hh["is_current"] is True and hh["products"] == []


def test_read_menu_rows_bom_and_cp1252(tmp_path):
    path = tmp_path / "menus.csv"
    path.write_bytes("\ufeff\"menu_name\";\"menu_descr\";\"menu_calories\"\n"
                     "\"Heart Healthy\";\"d\";\"1000\"\n".encode("utf-8"))
    rows = read_menu_rows(path)
    assert rows == [{"menu_name": "Heart Healthy", "menu_descr": "d",
                     "menu_calories": "1000"}]
    # the real export is cp1252 (en-dash 0x96 in descriptions)
    path.write_bytes(b'"menu_name";"menu_descr";"menu_calories"\n'
                     b'"Heart Healthy";"range 1000\x962000";"1000"\n')
    rows = read_menu_rows(path)
    assert rows[0]["menu_descr"] == "range 1000–2000"


# --- build_documents + schema ------------------------------------------------


def _chunks():
    return [{"id": "pdsrg:b:001", "authority": 2, "title": "t", "content": "c",
             "citation_url": "x", "locator": "l", "products": [1],
             "topics": [], "date": None, "is_current": True}]


def test_index_ids_are_search_key_safe():
    """Regression: AI Search keys allow only [A-Za-z0-9_\-=] — the 2026-09-05
    upload failed wholesale on colon ids (InvalidDocumentKey)."""
    import re

    docs = build_documents(_chunks(), PRODUCTS, FAMILIES,
                           [{"menu_name": "M", "menu_descr": "d",
                             "menu_calories": "1000"}])
    bad = [d["id"] for d in docs if not re.fullmatch(r"[A-Za-z0-9_\-=]+", d["id"])]
    assert not bad


def test_build_documents_sorted_and_deterministic():
    args = (_chunks(), PRODUCTS, FAMILIES,
            [{"menu_name": "M", "menu_descr": "d", "menu_calories": "1000"}])
    d1 = build_documents(*args)
    d2 = build_documents(*args)
    assert d1 == d2
    assert [d["id"] for d in d1] == sorted(d["id"] for d in d1)


def test_embed_text_is_title_plus_content():
    assert embed_text({"title": "T", "content": "C"}) == "T\n\nC"


def test_index_schema_matches_plan_9():
    s = index_schema(INDEX_NAME)
    assert s.name == "kb-main"
    fields = {f.name: f for f in s.fields}
    assert set(fields) == {"id", "source_type", "authority", "title", "content",
                           "content_vector", "citation_url", "locator",
                           "products", "topics", "date", "is_current",
                           "product_status"}
    v = fields["content_vector"]
    assert v.vector_search_dimensions == 3072
    assert v.stored is False                      # no stored vector copies
    assert v.vector_search_profile_name == "profile-sq"
    assert s.vector_search.profiles[0].compression_name == "sq-int8"
    assert s.vector_search.compressions[0].compression_name == "sq-int8"
    assert s.vector_search.algorithms[0].parameters.metric == "cosine"
    assert s.semantic_search.configurations[0].name == "sem-default"
    assert fields["id"].key is True
    assert fields["is_current"].filterable is True


# --- Embedder (fake API, cache roundtrips) ----------------------------------


class _Resp:
    def __init__(self, n):
        self.data = [type("D", (), {"embedding": [float(i)] * 4, "index": i})()
                     for i in range(n)]


class FakeAPI:
    """Embedding vectors encoding the *call* they came from (so batching and
    alignment are observable), stable across instances for cache tests."""

    def __init__(self):
        self.calls: list[list[str]] = []

    def create(self, *, model, input):
        marker = float(len(self.calls) + 1)
        self.calls.append(list(input))
        resp = _Resp(len(input))
        for d in resp.data:
            d.embedding = [marker] * 4
        return resp


def _embedder(tmp_path, client, deployment="dep", batch=16):
    return Embedder("https://example-aoai.openai.azure.com", "k", deployment,
                    "2025-04-01-preview", cache_path=tmp_path / "cache.jsonl",
                    client=client, batch_size=batch)


def test_embedder_batch_alignment_and_cache_roundtrip(tmp_path):
    api = FakeAPI()
    e = _embedder(tmp_path, api, batch=2)
    vecs = e.embed(["a", "b", "c"])
    assert api.calls == [["a", "b"], ["c"]]          # batching respected
    assert [v[0] for v in vecs] == [1.0, 1.0, 2.0]   # call 2 -> text "c"
    e.save_cache()

    e2 = _embedder(tmp_path, FakeAPI())
    assert e2.embed(["a", "b", "c"]) == vecs
    assert e2.n_api_calls == 0 and e2.n_cache_hits == 3


def test_embedder_cache_key_includes_deployment(tmp_path):
    e1 = _embedder(tmp_path, FakeAPI(), deployment="dep1")
    e1.embed(["x"])
    e1.save_cache()
    e2 = _embedder(tmp_path, FakeAPI(), deployment="dep2")
    e2.embed(["x"])
    assert e2.n_cache_hits == 0 and e2.n_api_calls == 1


def test_embedder_oversize_raises_before_any_api_call(tmp_path):
    api = FakeAPI()
    e = _embedder(tmp_path, api)
    with pytest.raises(ValueError, match="exceeds"):
        e.embed(["x" * (Embedder.MAX_INPUT_CHARS + 1)])
    assert api.calls == []


# --- Search wiring (fake SDK clients; serverless-safe, no list calls) ------


class _FakeIndexes:
    def __init__(self, present):
        self.present = set(present)
        self.calls: list = []

    def get_index(self, name):
        self.calls.append(("get", name))
        if name not in self.present:
            from azure.core.exceptions import ResourceNotFoundError
            raise ResourceNotFoundError("not found")
        return object()

    def create_or_update_index(self, index):
        self.calls.append(("create", index.name))
        self.present.add(index.name)

    def delete_index(self, name):
        self.calls.append(("delete", name))
        self.present.discard(name)


def test_ensure_index_get_based_create_exists_and_reset(monkeypatch):
    import azure.search.documents.indexes as ixmod

    fake = _FakeIndexes(set())
    monkeypatch.setattr(ixmod, "SearchIndexClient", lambda *a, **k: fake)
    assert ensure_index("https://x.search.windows.net", "k", "kb-main") == "created"
    assert ("create", "kb-main") in fake.calls
    assert "get" in [c[0] for c in fake.calls]      # existence probe...
    assert "list" not in str(fake.calls)              # ...never a list call

    fake2 = _FakeIndexes({"kb-main"})
    monkeypatch.setattr(ixmod, "SearchIndexClient", lambda *a, **k: fake2)
    assert ensure_index("https://x.search.windows.net", "k", "kb-main") == "exists"
    assert ("create", "kb-main") not in fake2.calls

    fake3 = _FakeIndexes({"kb-main"})
    monkeypatch.setattr(ixmod, "SearchIndexClient", lambda *a, **k: fake3)
    assert ensure_index("x", "k", "kb-main", reset=True) == "recreated"
    assert ("delete", "kb-main") in fake3.calls
    assert ("create", "kb-main") in fake3.calls


class _FakeResult:
    def __init__(self, key, ok):
        self.key = key
        self.succeeded = ok
        self.error_message = "" if ok else "boom"


class _FakeSearch:
    def __init__(self):
        self.batches: list = []

    def merge_or_upload_documents(self, docs):
        self.batches.append(docs)
        return [_FakeResult(d["id"], ok=(i % 2 == 0))
                for i, d in enumerate(docs)]


def test_upload_documents_batches_payload_and_error_count(monkeypatch):
    import azure.search.documents as smod

    fake = _FakeSearch()
    monkeypatch.setattr(smod, "SearchClient", lambda *a, **k: fake)
    docs = [{"id": f"d{i}", "content": "c"} for i in range(3)]
    n_ok, errors = upload_documents("https://x.search.windows.net", "k",
                                     "kb-main", docs,
                                     [[1.0], [2.0], [3.0]], batch=2)
    assert [len(b) for b in fake.batches] == [2, 1]   # batched
    assert fake.batches[0][1]["content_vector"] == [2.0]  # vectors attached
    assert n_ok == 2 and len(errors) == 1 and errors[0]["key"] == "d1"
