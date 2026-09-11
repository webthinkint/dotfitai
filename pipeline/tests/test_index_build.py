"""Index builder tests (plan §9) — synthetic fixtures, no network, no real claims."""

from __future__ import annotations

import pytest

from qa_pipeline.embeddings import Embedder
from qa_pipeline.index_build import (
    INDEX_NAME, build_documents, embed_text, ensure_index, index_schema,
    infopage_documents, menu_documents, pdsrg_documents, podcast_documents,
    product_documents, qa_date, qa_documents, read_menu_rows, split_sections,
    upload_documents,
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


def test_menu_documents_divergent_descriptions_raise():
    rows = [
        {"menu_name": "Night Out", "menu_descr": "Planned night.",
         "menu_calories": "1000"},
        {"menu_name": "Night out", "menu_descr": "Something else.",
         "menu_calories": "2000"},
    ]
    with pytest.raises(ValueError, match="different"):
        menu_documents(rows)


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
    assert hh["authority"] == 5 and hh["topics"] == ["menus"]
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


# --- infopage_documents (dotFIT.com info pages, authority 1) ------------------


def _infopage(coid, longname, searchcontent, url="https://www.dotfit.com/example"):
    return {"coid": coid, "longname": longname, "URL": url,
            "searchcontent": searchcontent}


def test_infopage_documents_shape_and_h1_to_description():
    page = _infopage(
        3965, "dotFIT Return/Refund Policy",
        "# dotFIT Return/Refund Policy\r\n\r\nReturns are accepted.\r\n\r\n"
        "## Refund Window\r\n\r\n30 days.")
    docs = infopage_documents([page])
    by_id = {d["id"]: d for d in docs}
    assert set(by_id) == {"infopage-3965-description",
                          "infopage-3965-refund-window"}
    desc, win = by_id["infopage-3965-description"], by_id["infopage-3965-refund-window"]
    # the page-title H1 is dropped; its intro prose is the description section
    assert desc["content"] == "Returns are accepted."
    assert desc["locator"] is None
    # header sections keep their header line and cite it as the locator
    assert win["content"] == "Refund Window\n\n30 days."
    assert win["locator"] == "Refund Window"
    for doc in docs:
        assert doc["source_type"] == "infopage"
        assert doc["authority"] == 1            # same §3 weight as products.json
        assert doc["title"] == "dotFIT Return/Refund Policy"   # PAGE_META display title
        assert doc["topics"] == ["policy"]                      # PAGE_META page class
        assert doc["citation_url"] == "https://www.dotfit.com/example"
        assert doc["products"] == [] and doc["date"] is None
        assert doc["is_current"] is True and doc["product_status"] is None


def test_infopage_documents_unknown_coid_raises():
    page = _infopage(999999, "Ghost Page", "# Ghost Page\r\n\r\nBoo.")
    with pytest.raises(ValueError, match="PAGE_META"):
        infopage_documents([page])


def test_infopage_documents_duplicate_section_slug_raises():
    # "Same" and "same" slug identically — two headers collapsing onto one
    # document id would let merge_or_upload silently keep only the last
    page = _infopage(3965, "dotFIT Return/Refund Policy",
                     "# T\r\n\r\nintro\r\n\r\n## Same\r\n\r\na\r\n\r\n"
                     "## same\r\n\r\nb")
    with pytest.raises(ValueError, match="collapse to section slug"):
        infopage_documents([page])


def test_build_documents_with_infopages_sorted_and_key_safe():
    import re

    page = _infopage(3968, "FAQs", "# FAQs\r\n\r\nShipping is free over $80.")
    docs = build_documents(_chunks(), PRODUCTS, FAMILIES,
                           [{"menu_name": "M", "menu_descr": "d",
                             "menu_calories": "1000"}],
                           _segments(), None, VIDEO_IDS, [page])
    assert [d["id"] for d in docs] == sorted(d["id"] for d in docs)
    assert "infopage-3968-description" in {d["id"] for d in docs}
    assert all(re.fullmatch(r"[A-Za-z0-9_\-=]+", d["id"]) for d in docs)
    # deterministic: same inputs, same bytes
    again = build_documents(_chunks(), PRODUCTS, FAMILIES,
                            [{"menu_name": "M", "menu_descr": "d",
                              "menu_calories": "1000"}],
                            _segments(), None, VIDEO_IDS, [page])
    assert docs == again


# --- build_documents + schema ------------------------------------------------


def _chunks():
    return [{"id": "pdsrg:b:001", "authority": 2, "title": "t", "content": "c",
             "citation_url": "x", "locator": "l", "products": [1],
             "topics": [], "date": None, "is_current": True}]


# Synthetic episode + its own video-id table: the real PODCAST_VIDEO_IDS is a
# curated 47-entry constant and an unknown stem raises by design, so the tests
# inject a table instead of naming a real episode.
VIDEO_IDS = {"e": "testVideoId0"}


def _segments():
    return [{"id": "1-expert-reacts-000", "episode_id": "1-expert-reacts",
             "episode_title": "#1 Expert Reacts", "source_file": "e.mp3",
             "chunk_index": 0, "start": "00:00", "end": "01:36",
             "start_ms": 32000, "end_ms": 96000, "duration_s": 96.0,
             "speakers": [1, 2], "n_words": 200,
             "text": "Speaker 1: Take creatine daily."}]


def test_podcast_documents_shape_and_defaults():
    doc = podcast_documents(_segments(), VIDEO_IDS)[0]
    assert doc["id"] == "podcast-1-expert-reacts-000"  # namespaced, digit-safe
    assert doc["source_type"] == "podcast" and doc["authority"] == 4
    assert doc["title"] == "#1 Expert Reacts (00:00–01:36)"
    assert doc["locator"] == "00:00–01:36"
    assert doc["content"] == "Speaker 1: Take creatine daily."
    # deep-linked to the segment start, so §7.4's "at 14:32" lands there
    assert doc["citation_url"] == "https://www.youtube.com/watch?v=testVideoId0&t=32s"
    assert doc["products"] == [] and doc["topics"] == []
    assert doc["date"] is None and doc["product_status"] is None
    assert doc["is_current"] is True  # null would hide it from filters


def test_index_ids_are_search_key_safe():
    """Regression: AI Search keys allow only [A-Za-z0-9_\-=] — the 2026-09-05
    upload failed wholesale on colon ids (InvalidDocumentKey)."""
    import re

    docs = build_documents(_chunks(), PRODUCTS, FAMILIES,
                           [{"menu_name": "M", "menu_descr": "d",
                             "menu_calories": "1000"}],
                           _segments(), None, VIDEO_IDS)
    bad = [d["id"] for d in docs if not re.fullmatch(r"[A-Za-z0-9_\-=]+", d["id"])]
    assert not bad


def test_build_documents_sorted_and_deterministic():
    args = (_chunks(), PRODUCTS, FAMILIES,
            [{"menu_name": "M", "menu_descr": "d", "menu_calories": "1000"}],
            _segments(), None, VIDEO_IDS)
    d1 = build_documents(*args)
    d2 = build_documents(*args)
    assert d1 == d2
    assert [d["id"] for d in d1] == sorted(d["id"] for d in d1)
    by_source = {}
    for d in d1:
        by_source[d["source_type"]] = by_source.get(d["source_type"], 0) + 1
    assert by_source["podcast"] == 1


def test_build_documents_without_podcast_unchanged():
    args = (_chunks(), PRODUCTS, FAMILIES,
            [{"menu_name": "M", "menu_descr": "d", "menu_calories": "1000"}])
    assert (len(build_documents(*args)) + 1
            == len(build_documents(*args, _segments(), None, VIDEO_IDS)))


# --- qa_documents (Stage 2 canonicals -> §9) ------------------------------------


def _qa_rec(**kw):
    base = {"id": "abc123def4567890", "source_file": "2024/t.docx",
            "doc_type": "qa_email", "thread_date": "2024-03-05",
            "filename": "t",
            "question_canonical": "Can I stack AF with creatine?",
            "answer": "Yes — AF stacks fine.",
            "products": [1213, 1216], "topics": ["creatine"],
            "needs_review": False}
    base.update(kw)
    return base


def test_qa_date_normalizes_to_offset():
    assert qa_date("2024-03-05") == "2024-03-05T00:00:00Z"
    assert qa_date(None) is None
    assert qa_date("whenever") is None
    assert qa_date("2024-03-05T10:00:00Z") is None  # day-precision only in


def test_qa_documents_shape_and_defaults():
    doc = qa_documents([_qa_rec()])[0]
    assert doc["id"] == "qa-abc123def4567890"
    assert doc["source_type"] == "qa" and doc["authority"] == 3
    assert doc["title"] == "Can I stack AF with creatine?"
    assert doc["content"] == "Yes — AF stacks fine."
    assert doc["products"] == ["1213", "1216"]  # part_nos stringified
    assert doc["topics"] == ["creatine"]
    assert doc["date"] == "2024-03-05T00:00:00Z"
    assert doc["is_current"] is True  # Stage 4 flips superseded later
    assert doc["citation_url"] is None  # no verified link (podcast precedent)
    assert doc["locator"] == "t" and doc["product_status"] is None


def test_qa_documents_null_question_falls_back_to_filename():
    doc = qa_documents([_qa_rec(question_canonical=None,
                               filename="Creatine loading note")])[0]
    assert doc["title"] == "Creatine loading note"


def test_qa_documents_empty_answer_skipped():
    assert qa_documents([_qa_rec(answer="  ")]) == []


def test_qa_documents_stage4_superseded_skipped():
    """§4: superseded answers stay in the committed store, never the index."""
    live = _qa_rec()
    dup = _qa_rec(id="dup0000000000000", source_file="2023/dup.docx",
                  is_current=False)
    docs = qa_documents([dup, live])
    assert [d["id"] for d in docs] == ["qa-abc123def4567890"]
    assert docs[0]["is_current"] is True


def test_qa_documents_is_current_defaults_true_for_legacy_input():
    """Stage 2-only input (no Stage 4 stamp) still indexes unchanged."""
    doc = qa_documents([_qa_rec()])[0]  # _qa_rec carries no is_current key
    assert doc["is_current"] is True


def test_qa_documents_oversize_answer_splits_into_fitting_parts():
    from qa_pipeline.index_build import QA_PART_CHARS, embed_text, split_answer_parts
    paras = [f"Paragraph {i} about creatine dosing. " * 200 for i in range(8)]
    answer = "\n\n".join(paras)
    parts = split_answer_parts(answer)
    assert len(parts) > 1
    assert "\n\n".join(parts) == answer  # nothing lost, nothing added
    docs = qa_documents([_qa_rec(answer=answer)])
    assert [d["id"] for d in docs] == \
        [f"qa-abc123def4567890-p{n}" for n in range(1, len(docs) + 1)]
    assert docs[0]["title"].endswith(f"(part 1 of {len(docs)})")
    assert all(len(embed_text(d)) <= QA_PART_CHARS + 2000 for d in docs)
    assert all(d["products"] == ["1213", "1216"] for d in docs)  # metadata shared


def test_qa_documents_single_giant_paragraph_hard_splits():
    from qa_pipeline.index_build import QA_PART_CHARS, split_answer_parts
    parts = split_answer_parts("x" * (QA_PART_CHARS + 5))
    assert len(parts) == 2 and all(len(p) <= QA_PART_CHARS for p in parts)


def test_build_documents_with_qa_sorted_and_key_safe():
    import re
    args = (_chunks(), PRODUCTS, FAMILIES,
            [{"menu_name": "M", "menu_descr": "d",
              "menu_calories": "1000"}], _segments(), [_qa_rec()],
            VIDEO_IDS)
    docs = build_documents(*args)
    assert docs == sorted(docs, key=lambda d: d["id"])
    assert "qa-abc123def4567890" in [d["id"] for d in docs]
    bad = [d["id"] for d in docs
           if not re.fullmatch(r"[A-Za-z0-9_\-=]+", d["id"])]
    assert not bad


def test_embed_text_is_title_plus_content():
    assert embed_text({"title": "T", "content": "C"}) == "T\n\nC"


def test_index_schema_matches_plan_9():
    s = index_schema(INDEX_NAME)
    assert s.name == "kb-main-v2"
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


def test_embedder_checkpoints_cache_per_batch(tmp_path):
    # a crash after batch 1 must not lose its vectors: the cache file is
    # appended per batch, not only on save_cache()
    import json
    api = FakeAPI()
    e = _embedder(tmp_path, api, batch=2)
    e.embed(["a", "b", "c"])
    cached = [json.loads(l) for l in (tmp_path / "cache.jsonl")
              .read_text(encoding="utf-8").splitlines()]
    assert len(cached) == 3


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


class _SlowDeleteIndexes:
    """Deletion is async: get_index keeps answering for a few polls, and the
    first create still races (the 2026-09-07 kb-main incident shape)."""

    def __init__(self, polls_before_gone=3, create_failures=1):
        self.deleted = False
        self.polls_before_gone = polls_before_gone
        self.create_failures = create_failures
        self.create_attempts = 0
        self.calls: list = []

    def delete_index(self, name):
        self.calls.append(("delete", name))
        self.deleted = True

    def get_index(self, name):
        self.calls.append(("get", name))
        if not self.deleted:
            return object()
        if self.polls_before_gone > 0:
            self.polls_before_gone -= 1
            return object()
        from azure.core.exceptions import ResourceNotFoundError
        raise ResourceNotFoundError("not found")

    def create_or_update_index(self, index):
        self.create_attempts += 1
        self.calls.append(("create", index.name))
        if self.create_attempts <= self.create_failures:
            raise RuntimeError("could not be created")
        self.deleted = False


def test_ensure_index_reset_waits_for_deletion_and_retries_create(monkeypatch):
    import azure.search.documents.indexes as ixmod

    fake = _SlowDeleteIndexes(polls_before_gone=3, create_failures=1)
    monkeypatch.setattr(ixmod, "SearchIndexClient", lambda *a, **k: fake)
    assert ensure_index("x", "k", "kb-main", reset=True,
                        poll_seconds=0.0) == "recreated"
    # create only after the get that finally 404'd...
    last_get = max(i for i, c in enumerate(fake.calls) if c[0] == "get")
    first_create = min(i for i, c in enumerate(fake.calls) if c[0] == "create")
    assert last_get < first_create
    # ...and the raced create was retried to success
    assert fake.create_attempts == 2
    # never left in the deleted state without a create attempt succeeding
    assert fake.deleted is False


class _PendingDeleteIndexes:
    """The real async-delete shape: while the delete runs, Azure 404s with
    "is being deleted" — the *same* exception type as a clean miss. Only
    after it completes does the body become "No index ... was found"."""

    def __init__(self, polls_pending=3, delete_raises=False):
        self.polls_pending = polls_pending
        self.deleted = False
        self.delete_raises = delete_raises
        self.create_attempts = 0
        self.calls: list = []

    def _pending(self):
        from azure.core.exceptions import ResourceNotFoundError
        return ResourceNotFoundError(
            "The index with the name 'kb-main' in the service 'x' is being deleted.")

    def _gone(self):
        from azure.core.exceptions import ResourceNotFoundError
        return ResourceNotFoundError(
            "No index with the name 'kb-main' was found in the service 'x'.")

    def delete_index(self, name):
        self.calls.append(("delete", name))
        self.deleted = True
        if self.delete_raises:
            raise self._pending()

    def get_index(self, name):
        self.calls.append(("get", name))
        if not self.deleted:
            return object()
        if self.polls_pending > 0:
            self.polls_pending -= 1
            raise self._pending()
        raise self._gone()

    def create_or_update_index(self, index):
        self.create_attempts += 1
        self.calls.append(("create", index.name))
        self.deleted = False


def test_ensure_index_reset_polls_through_pending_delete_404(monkeypatch):
    """A 404 saying "is being deleted" is not "gone" — creating into it is the
    2026-09-08 incident that lost kb-main."""
    import azure.search.documents.indexes as ixmod

    fake = _PendingDeleteIndexes(polls_pending=3)
    monkeypatch.setattr(ixmod, "SearchIndexClient", lambda *a, **k: fake)
    assert ensure_index("x", "k", "kb-main", reset=True,
                        poll_seconds=0.0) == "recreated"
    # every pending 404 was polled through, and create came only after the
    # body finally said "was found" — i.e. 4 gets before the first create
    gets_before_create = [c for c in fake.calls[:[
        i for i, c in enumerate(fake.calls) if c[0] == "create"][0]]
        if c[0] == "get"]
    assert len(gets_before_create) == 4
    assert fake.create_attempts == 1


def test_ensure_index_reset_polls_when_delete_itself_404s_pending(monkeypatch):
    """delete_index can itself raise the pending 404 (a delete already in
    flight); that must still poll, not fall straight through to create."""
    import azure.search.documents.indexes as ixmod

    fake = _PendingDeleteIndexes(polls_pending=2, delete_raises=True)
    monkeypatch.setattr(ixmod, "SearchIndexClient", lambda *a, **k: fake)
    assert ensure_index("x", "k", "kb-main", reset=True,
                        poll_seconds=0.0) == "recreated"
    first_create = [i for i, c in enumerate(fake.calls) if c[0] == "create"][0]
    assert any(c[0] == "get" for c in fake.calls[:first_create])


def test_ensure_index_reset_times_out_on_a_wedged_delete(monkeypatch):
    """kb-main's actual state: the delete never completes. Time out rather
    than create into a half-deleted index."""
    import azure.search.documents.indexes as ixmod

    fake = _PendingDeleteIndexes(polls_pending=10**9)
    monkeypatch.setattr(ixmod, "SearchIndexClient", lambda *a, **k: fake)
    with pytest.raises(TimeoutError, match="still present"):
        ensure_index("x", "k", "kb-main", reset=True,
                     poll_seconds=0.0, timeout=0.0)
    assert fake.create_attempts == 0


def test_ensure_index_no_reset_refuses_to_create_into_a_pending_delete(monkeypatch):
    """Non-reset path: a pending-delete 404 must raise, not create."""
    import azure.search.documents.indexes as ixmod

    fake = _PendingDeleteIndexes(polls_pending=10**9)
    fake.deleted = True
    monkeypatch.setattr(ixmod, "SearchIndexClient", lambda *a, **k: fake)
    with pytest.raises(RuntimeError, match="mid-delete"):
        ensure_index("x", "k", "kb-main")
    assert fake.create_attempts == 0


class _FakeResult:
    def __init__(self, key, ok):
        self.key = key
        self.succeeded = ok
        self.error_message = "" if ok else "boom"


class _FakeSearch:
    def __init__(self, ids=None):
        self.batches: list = []
        self.ids = list(ids or [])          # what a paginated scan returns
        self.deleted: list = []

    def merge_or_upload_documents(self, docs):
        self.batches.append(docs)
        return [_FakeResult(d["id"], ok=(i % 2 == 0))
                for i, d in enumerate(docs)]

    def search(self, *, search_text, select, top, skip, order_by):
        assert search_text == "*" and select == ["id"] and order_by == ["id"]
        return [{"id": i} for i in self.ids[skip:skip + top]]

    def delete_documents(self, docs):
        self.batches.append(docs)
        self.deleted.extend(d["id"] for d in docs)
        return [_FakeResult(d["id"], True) for d in docs]


def test_list_index_ids_paginates_and_sorts(monkeypatch):
    import azure.search.documents as smod
    import qa_pipeline.index_build as ixmod

    fake = _FakeSearch(ids=[f"d{i:05d}" for i in range(2500, 0, -1)])
    monkeypatch.setattr(smod, "SearchClient", lambda *a, **k: fake)
    out = ixmod.list_index_ids("https://x.search.windows.net", "k", "kb",
                               page=1000)
    assert len(out) == 2500
    assert out == sorted(out)


def test_delete_documents_batches_and_reports(monkeypatch):
    import azure.search.documents as smod
    import qa_pipeline.index_build as ixmod

    fake = _FakeSearch()
    monkeypatch.setattr(smod, "SearchClient", lambda *a, **k: fake)
    n_ok, errors = ixmod.delete_documents(
        "https://x.search.windows.net", "k", "kb",
        [f"k{i}" for i in range(5)], batch=2)
    assert [len(b) for b in fake.batches] == [2, 2, 1]
    assert n_ok == 5 and errors == []
    assert fake.deleted == [f"k{i}" for i in range(5)]


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
