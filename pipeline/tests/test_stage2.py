"""Stage 2 structuring tests (plan §4 Stage 2) — synthetic fixtures, no network."""

from __future__ import annotations

import json

import pytest

from qa_pipeline.alias import build_alias_table
from qa_pipeline.stage2 import (
    AUDIENCE_KEYS,
    build_messages,
    build_product_lookup,
    build_record,
    cache_key,
    containment_score,
    detect_currency_cues,
    deterministic_product_tags,
    fallback_record,
    is_audit_sample,
    mark_review,
    merge_topics,
    normalize_products,
    review_reasons,
    run_stage2,
    summarize,
    validate_llm_output,
)


def _product(part_no, longname):
    return {"part_no": part_no, "longname": longname, "coid": 1,
            "URL": "https://www.dotFIT.com/x", "searchcontent": "x"}


# mirror of the test_alias.py synthetic corpus (covers every curated overlay
# target so build_alias_table validates: MVs, legacy renames, replacements)
PRODUCTS = [
    _product(1005, "Active MV - Multivitamin & Mineral Formula"),
    _product(1007, "Women's MV - Multivitamin & Mineral Formula"),
    _product(1009, "Over 50 MV - Multivitamin & Mineral Formula"),
    _product(1001, "Brain Health"),
    _product(1003, "CollagenComplex"),
    _product(1000, "Antioxidant"),
    _product(1017, "Probiotics"),
    _product(1200, "Creatine Monohydrate - Raspberry Lemonade Drink Mix"),
    _product(1227, "Creatine Monohydrate - Unflavored"),
    _product(1207, "Creatine Complex - Raspberry Lemonade"),
    _product(1213, "AminoFormula - Blue Raspberry"),
    _product(1216, "AminoFormula - Lemonade"),
    _product(1214, "NO7 PreWorkout - Blue Raspberry"),
    _product(1215, "NO7 PreWorkout - Lemonade"),
    _product(1333, "LeanMeal Nutrition Shake - Chocolate (formerly LeanMR)"),
    _product(1334, "LeanMeal Nutrition Shake - Vanilla (formerly LeanMR)"),
    _product(1371, "First String - Chocolate"),
    _product(8001, "Alln1 SuperBlend - Orange Burst"),
    _product(1100, "WeightLoss & LiverSupport"),
]


@pytest.fixture
def alias_table():
    return build_alias_table(PRODUCTS)


@pytest.fixture
def lookup(alias_table):
    return build_product_lookup(alias_table)


@pytest.fixture
def families(alias_table):
    return [f["family"] for f in alias_table["families"]]


def _rec(**kw):
    base = {
        "id": "abc123",
        "source_file": "2024/notes/test.docx",
        "year": 2024,
        "doc_type": "qa_email",
        "thread_date": "2024-03-05",
        "topic_subfolder": "Creatine",
        "filename": "test",
        "question": "Can I stack AF with creatine?",
        "expert_section": "Hi [CUSTOMER],\nYes — AF stacks fine with creatine monohydrate.",
        "customer_section": "Question: Can I stack AF with creatine?",
        "residual_pii_flag": False,
    }
    base.update(kw)
    return base


def _llm(**kw):
    base = {
        "question_canonical": "Can AF be stacked with creatine?",
        "answer": "Hi [CUSTOMER],\nYes — AF stacks fine with creatine monohydrate.",
        "products_mentioned": ["AF"],
        "topics": ["creatine", "stacking"],
        "audience_flags": {k: False for k in AUDIENCE_KEYS},
        "residual_pii_flag": False,
        "residual_pii_evidence": None,
        "confidence": 0.95,
    }
    base.update(kw)
    return base


# --- schema validation --------------------------------------------------------


class TestValidate:
    def test_good_output_passes(self):
        assert validate_llm_output(_llm())["confidence"] == 0.95

    def test_null_question_ok_for_expert_notes(self):
        assert validate_llm_output(_llm(question_canonical=None))["question_canonical"] is None

    def test_missing_keys_raise(self):
        bad = _llm()
        del bad["answer"]
        with pytest.raises(ValueError, match="missing keys"):
            validate_llm_output(bad)

    def test_extra_keys_raise(self):
        bad = _llm()
        bad["part_nos"] = [1213]  # the LLM must never emit part numbers
        with pytest.raises(ValueError, match="extra keys"):
            validate_llm_output(bad)

    def test_empty_answer_raises(self):
        with pytest.raises(ValueError, match="non-empty string"):
            validate_llm_output(_llm(answer="  "))

    def test_confidence_out_of_range_raises(self):
        with pytest.raises(ValueError, match="0-1"):
            validate_llm_output(_llm(confidence=1.5))

    def test_confidence_bool_rejected(self):
        with pytest.raises(ValueError, match="must be a number"):
            validate_llm_output(_llm(confidence=True))

    def test_audience_flags_shape_enforced(self):
        bad = _llm()
        bad["audience_flags"] = {"minor": False}
        with pytest.raises(ValueError, match="exactly keys"):
            validate_llm_output(bad)
        bad2 = _llm()
        bad2["audience_flags"] = {k: 0 for k in AUDIENCE_KEYS}
        with pytest.raises(ValueError, match="booleans"):
            validate_llm_output(bad2)


# --- product normalization ------------------------------------------------------


class TestNormalizeProducts:
    def test_family_name_maps(self, lookup, families):
        pns, unresolved = normalize_products(["AminoFormula"], lookup, families)
        assert pns == [1213, 1216] and unresolved == []

    def test_alias_token_expands(self, lookup, families):
        pns, _ = normalize_products(["AF"], lookup, families)
        assert pns == [1213, 1216]

    def test_legacy_rename_expands_to_successor(self, lookup, families):
        # a rename is an identity mapping — LeanMR tags LeanMeal part_nos
        pns, _ = normalize_products(["LeanMR"], lookup, families)
        assert pns == [1333, 1334]

    def test_variant_longname_resolves_by_prefix(self, lookup, families):
        pns, _ = normalize_products(["AminoFormula - Blue Raspberry"], lookup, families)
        assert pns == [1213, 1216]

    def test_context_only_never_tags(self, lookup, families):
        for tok in ("MVM", "PP", "mvm"):
            pns, unresolved = normalize_products([tok], lookup, families)
            assert pns == [] and unresolved == [tok]

    def test_replacement_and_discontinued_never_tag(self, lookup, families):
        for tok in ("Recover&Build", "KidsMV", "VeganMV"):
            pns, unresolved = normalize_products([tok], lookup, families)
            assert pns == [] and unresolved == [tok]

    def test_unknown_strings_are_unresolved(self, lookup, families):
        pns, unresolved = normalize_products(["MysteryPowder 9000"], lookup, families)
        assert pns == [] and unresolved == ["MysteryPowder 9000"]


class TestDeterministicTags:
    def test_alias_needs_word_boundaries(self, alias_table, lookup):
        assert deterministic_product_tags("a fine afternoon walk", alias_table, lookup) == []
        assert deterministic_product_tags("stack AF with creatine", alias_table, lookup) == [1213, 1216]

    def test_legacy_tolerant_spacing(self, alias_table, lookup):
        assert deterministic_product_tags("is Lean MR the same?", alias_table, lookup) == [1333, 1334]

    def test_context_only_not_scanned(self, alias_table, lookup):
        assert deterministic_product_tags("take your MVM daily", alias_table, lookup) == []


class TestCurrencyCues:
    def test_renames_replacements_discontinued(self, alias_table):
        text = "I used LeanMR and Recover&Build; can my kid take KidsMV?"
        assert detect_currency_cues(text, alias_table) == ["KidsMV", "LeanMR", "Recover&Build"]

    def test_clean_text_has_no_cues(self, alias_table):
        assert detect_currency_cues("AF stacks fine with creatine.", alias_table) == []


# --- containment + topics ---------------------------------------------------------


class TestContainment:
    def test_dosage_boundaries_split(self):
        from qa_pipeline.stage2 import word_tokens
        assert word_tokens("34mg/serving") == ["34", "mg", "serving"]
        assert word_tokens("B12 1.01grams") == ["b", "12", "1", "01", "grams"]

    def test_joined_dosage_transcription_scores_one(self):
        assert containment_score("34 mg/serving", "34mg/serving") == 1.0

    def test_verbatim_scores_one(self):
        src = "Hi [CUSTOMER],\nYes — AF stacks fine with creatine monohydrate."
        assert containment_score(src, src) == 1.0

    def test_cleaned_scores_high(self):
        src = "Take 1-2 softgels daily with a meal."
        assert containment_score("Take 1-2 softgels daily with a meal", src) > 0.9

    def test_invented_content_scores_low(self):
        src = "Take 1-2 softgels daily with a meal."
        ans = ("Take 1-2 softgels daily with a meal. Also cures diabetes and "
               "reverses aging overnight with quantum peptides.")
        assert containment_score(ans, src) < 0.85

    def test_empty_answer_scores_zero(self):
        assert containment_score("", "some source text") == 0.0


class TestMergeTopics:
    def test_union_deduped_sorted(self):
        assert merge_topics(["Creatine", "stacking"], "Creatine / Basics") == [
            "Basics", "Creatine", "stacking"]

    def test_unresolved_mvm_forces_multivitamin(self):
        assert "multivitamin" in merge_topics([], "", unresolved_mvm=True)
        assert "multivitamin" not in merge_topics([], "")


# --- record build -------------------------------------------------------------------


class TestBuildRecord:
    def test_full_shape(self, alias_table, lookup, families):
        r2 = mark_review(build_record(_rec(), _llm(), alias_table, lookup, families, "gpt-5-mini"))
        assert r2["question_original"] == "Can I stack AF with creatine?"
        assert r2["question_canonical"] == "Can AF be stacked with creatine?"
        assert r2["products"] == [1213, 1216]
        assert r2["products_unresolved"] == []
        assert r2["topics"] == ["creatine", "stacking"]  # subfolder hint deduped
        assert r2["audience_flags"] == {k: False for k in AUDIENCE_KEYS}
        assert r2["currency_cues"] == []
        assert r2["residual_pii_flag"] is False
        assert r2["containment_score"] == 1.0
        assert r2["llm_error"] is None and r2["model"] == "gpt-5-mini"

    def test_residual_flag_is_an_or(self, alias_table, lookup, families):
        r2 = build_record(_rec(residual_pii_flag=True), _llm(), alias_table, lookup,
                          families, "m")
        assert r2["residual_pii_flag"] is True
        r2b = build_record(_rec(), _llm(residual_pii_flag=True), alias_table, lookup,
                           families, "m")
        assert r2b["residual_pii_flag"] is True

    def test_expert_note_null_question(self, alias_table, lookup, families):
        rec = _rec(doc_type="expert_note", question=None, customer_section="")
        r2 = build_record(rec, _llm(question_canonical=None, products_mentioned=[]),
                          alias_table, lookup, families, "m")
        assert r2["question_original"] is None and r2["question_canonical"] is None


class TestFallback:
    def test_deterministic_and_queued(self, alias_table, lookup):
        r1 = fallback_record(_rec(), alias_table, lookup, "no_llm_mode")
        r2 = fallback_record(_rec(), alias_table, lookup, "no_llm_mode")
        assert r1 == r2
        assert r1["confidence"] == 0.0 and r1["needs_review"] is True
        assert r1["answer"] == _rec()["expert_section"].strip()
        assert r1["products"] == [1213, 1216]  # deterministic scan still tags
        assert r1["llm_error"] == "no_llm_mode" and r1["model"] == "rule-based"


class TestReviewReasons:
    def test_low_confidence(self):
        r = mark_review(fallback_record(_rec(), build_alias_table(PRODUCTS),
                                        build_product_lookup(build_alias_table(PRODUCTS)),
                                        "x"))
        assert "low_confidence" in review_reasons(r)

    def test_each_signal(self):
        base = {"id": "z" * 16, "confidence": 0.99, "containment_score": 1.0,
                "products_unresolved": [], "residual_pii_flag": False, "llm_error": None}
        assert review_reasons({**base, "llm_error": "boom"}) == ["llm_error"]
        assert "residual_pii_flag" in review_reasons({**base, "residual_pii_flag": True})
        assert "containment_fail" in review_reasons({**base, "containment_score": 0.1})
        assert "low_confidence" in review_reasons({**base, "confidence": 0.2})

    def test_unresolved_is_curation_not_review(self):
        # the LLM names every brand it sees (Gatorade, housekeeping); queuing
        # on that would review-queue ~every document. Unresolved mentions are
        # tallied for the alias worksheet instead (see summarize).
        base = {"id": "z" * 16, "confidence": 0.99, "containment_score": 1.0,
                "products_unresolved": ["Gatorade"], "residual_pii_flag": False,
                "llm_error": None}
        assert review_reasons(base) == []
        s = summarize([{**base, "doc_type": "qa_email", "products": [],
                        "currency_cues": [], "needs_review": False}])
        assert s["unresolved_mentions"] == {"Gatorade": 1}

    def test_audit_sample_is_deterministic(self):
        assert is_audit_sample("abc123", 0.0) is False
        assert is_audit_sample("abc123", 1.0) is True
        assert is_audit_sample("abc123") == is_audit_sample("abc123")


# --- driver -----------------------------------------------------------------------------


def _canned(mapping):
    calls = []

    def call(messages):
        calls.append(messages)
        user = messages[-1]["content"]
        for marker, payload in mapping.items():
            if marker in user:
                if isinstance(payload, Exception):
                    raise payload
                return payload
        return _llm()
    call.calls = calls
    return call


class TestRunStage2:
    def test_llm_path_sorted_and_stats(self, alias_table):
        docs = [_rec(source_file="2024/b.docx", id="b" * 16),
                _rec(source_file="2024/a.docx", id="a" * 16)]
        written = {}
        records, stats = run_stage2(
            docs, alias_table, _canned({}), "gpt-5-mini",
            cache={}, cache_write=lambda k, v: written.__setitem__(k, v),
            cache_key_fn=lambda r: f"key-{r['source_file']}")
        assert [r["source_file"] for r in records] == ["2024/a.docx", "2024/b.docx"]
        assert stats == {"n_llm_calls": 2, "n_cache_hits": 0,
                         "n_fallback": 0, "n_llm_errors": 0}
        assert len(written) == 2

    def test_cache_hits_skip_the_llm(self, alias_table):
        docs = [_rec()]
        first, _ = run_stage2(docs, alias_table, _canned({}), "m",
                              cache={}, cache_write=None, cache_key_fn=lambda r: "k")
        cached = {"k": {k: v for k, v in first[0].items()
                        if k in ("question_canonical", "answer", "products_mentioned",
                                 "topics", "audience_flags", "residual_pii_flag",
                                 "residual_pii_evidence", "confidence")}}
        # rebuild the raw LLM payload shape from the record for the cache
        raw = _llm()
        calls = _canned({})
        records, stats = run_stage2(docs, alias_table, calls, "m",
                                    cache={"k": raw},
                                    cache_write=None, cache_key_fn=lambda r: "k")
        assert stats["n_cache_hits"] == 1 and stats["n_llm_calls"] == 0
        assert calls.calls == [] and records[0]["products"] == [1213, 1216]
        assert cached  # the first run's record carries the same content

    def test_no_llm_mode_is_byte_identical(self, alias_table, tmp_path):
        docs = [_rec(source_file="2024/b.docx", id="b" * 16),
                _rec(source_file="2024/a.docx", id="a" * 16)]
        r1, _ = run_stage2(docs, alias_table, None, "rule-based")
        r2, _ = run_stage2(list(reversed(docs)), alias_table, None, "rule-based")
        p1, p2 = tmp_path / "a.jsonl", tmp_path / "b.jsonl"
        for path, recs in ((p1, r1), (p2, r2)):
            with path.open("w", encoding="utf-8", newline="\n") as f:
                for r in recs:
                    f.write(json.dumps(r, ensure_ascii=False) + "\n")
        assert p1.read_bytes() == p2.read_bytes()

    def test_llm_error_falls_back_loudly(self, alias_table):
        docs = [_rec()]
        records, stats = run_stage2(
            docs, alias_table, _canned({"Expert answer": RuntimeError("boom")}),
            "m")
        assert records[0]["needs_review"] is True
        assert records[0]["llm_error"].startswith("RuntimeError: boom")
        assert stats["n_llm_errors"] == 1 and stats["n_fallback"] == 1

    def test_oversize_source_falls_back(self, alias_table):
        import qa_pipeline.stage2 as s2mod
        docs = [_rec(expert_section="x" * (s2mod.MAX_SOURCE_CHARS + 1))]
        records, stats = run_stage2(docs, alias_table, _canned({}), "m")
        assert records[0]["llm_error"] == "source_too_long"
        assert stats["n_fallback"] == 1

    def test_summarize_counts(self, alias_table):
        docs = [_rec(), _rec(source_file="2024/note.docx", id="d" * 16,
                             doc_type="expert_note", question=None,
                             customer_section="")]
        records, _ = run_stage2(docs, alias_table, _canned({}), "m")
        s = summarize(records)
        assert s["n_documents"] == 2 and s["with_products"] == 2
        assert s["products_indexed"] == [1213, 1216]
        assert s["by_doc_type"] == {"expert_note": 1, "qa_email": 1}


class TestPromptContract:
    """Triage round 1 (2026-09-06) prompt fixes are pinned: [NAME] (never
    [CUSTOMER]) for residual names, silent staff redaction, verbatim public
    figures, transcription-not-summary for expert notes."""

    def test_redact_to_name_never_customer(self):
        from qa_pipeline.stage2 import SYSTEM_PROMPT
        assert "replace it with [NAME]" in SYSTEM_PROMPT
        assert "replace it with [CUSTOMER]" not in SYSTEM_PROMPT

    def test_silent_staff_and_public_figures_listed(self):
        from qa_pipeline.stage2 import (
            ACCEPTED_HONORIFIC_NAMES, SILENT_STAFF_NAMES, SYSTEM_PROMPT,
        )
        for name in ("Berlinda", "Chad", "Spruce", "Barefield"):
            assert name in SILENT_STAFF_NAMES
            assert name in SYSTEM_PROMPT
        # customers/users per the same triage stay flaggable, never silent
        for name in ("James", "Paul", "Steve", "Neil", "Matt"):
            assert name not in SILENT_STAFF_NAMES
        for surname in ("Hirsch", "Feldkamp", "Williams"):
            assert surname in ACCEPTED_HONORIFIC_NAMES
        assert "never flag" in SYSTEM_PROMPT

    def test_evidence_span_only_and_transcribe(self):
        from qa_pipeline.stage2 import SYSTEM_PROMPT
        assert "ONLY the name span" in SYSTEM_PROMPT
        assert "never summarize or shorten" in SYSTEM_PROMPT

    def test_prompt_version_bumped(self):
        from qa_pipeline.stage2 import PROMPT_VERSION
        assert PROMPT_VERSION == "1.1.0"


class TestMessages:
    def test_families_listed_and_note_shape(self, families):
        msgs = build_messages(_rec(), families)
        assert msgs[0]["role"] == "system" and "transcription" in msgs[0]["content"]
        assert "AminoFormula" in msgs[1]["content"]
        msgs_note = build_messages(
            _rec(doc_type="expert_note", question=None, customer_section=""), families)
        assert "may be null" in msgs_note[1]["content"]

    def test_cache_key_stable_and_sensitive(self):
        r, other = _rec(), _rec(expert_section="different text")
        assert cache_key("d", "v", r) == cache_key("d", "v", dict(r))
        assert cache_key("d", "v", r) != cache_key("d", "v", other)
        assert cache_key("d", "v", r) != cache_key("other", "v", r)
