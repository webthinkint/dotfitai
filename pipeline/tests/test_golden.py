"""Golden-set sampling tests (plan §12) — synthetic fixtures, no network."""

from __future__ import annotations

import json

import pytest

from qa_pipeline import golden
from qa_pipeline.alias import build_alias_table
from qa_pipeline.cli import main
from qa_pipeline.golden import (
    ADVERSARIAL_PLAN,
    ADVERSARIAL_SIZE,
    CURATED_ADVERSARIAL,
    CURATED_MULTI_TURN,
    GOLDEN_VERSION,
    MULTI_TURN_PLAN,
    MULTI_TURN_ROLES,
    PROBE_QUERY_WORDS,
    PROBE_SOURCES,
    build_adversarial,
    build_multiturn,
    build_pool,
    build_probes,
    largest_remainder,
    probe_query,
    probe_stratum,
    rank_key,
    record_families,
    repair_coverage,
    run_golden,
    unmet_requirements,
    write_adversarial_jsonl,
    write_adversarial_worksheet,
    write_multiturn_jsonl,
    write_multiturn_worksheet,
    write_probes_jsonl,
    write_worksheet,
)


def _product(part_no, longname):
    return {"part_no": part_no, "longname": longname, "coid": 1,
            "URL": "https://www.dotFIT.com/x", "searchcontent": "x"}


# mirror of the test_alias.py / test_stage4.py synthetic corpus (covers every
# curated overlay target so build_alias_table validates)
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
    _product(1369, "WheySmooth -  High Protein - Chocolate"),
    _product(1374, "All Natural WheySmooth - Chocolate"),
    _product(1020, "Omega-3 Fish Oil"),
    _product(1004, "Calcium Complex"),
    _product(1300, "Plant Protein - Vanilla"),
    _product(1301, "Plant Protein - Chocolate"),
]


def _rec(rid, source_file, question, *, year=2024, thread_date="2024-05-01",
         products=(), topics=(), is_current=True, answer="Expert answer.",
         doc_type="qa_email"):
    return {
        "id": rid,
        "source_file": source_file,
        "year": year,
        "doc_type": doc_type,
        "thread_date": thread_date,
        "topic_subfolder": "",
        "filename": source_file.rsplit("/", 1)[-1],
        "question_original": question,
        "question_canonical": question,
        "answer": answer,
        "products": list(products),
        "products_unresolved": [],
        "topics": list(topics),
        "is_current": is_current,
        "stage4_status": "current" if is_current else "superseded_currency",
    }


@pytest.fixture
def alias_table():
    return build_alias_table(PRODUCTS)


def _pool():
    """Small synthetic pool: 3 years, 3 families + untagged, 18 pairs."""
    recs = []
    n = 0
    for year, dates in [(2022, ["2022-01-05"]), (2023, ["2023-03-01"] * 6),
                        (2026, ["2026-02-01"] * 11)]:
        for i, date in enumerate(dates):
            n += 1
            products = [1213] if i % 3 == 0 else ([1005] if i % 3 == 1 else [])
            topics = ["creatine"] if i % 2 == 0 else ["dosing"]
            recs.append(_rec(f"r{n:02d}", f"{year}/q{n:02d}.docx",
                             f"Question {n} about thing {n}?", year=year,
                             thread_date=date, products=products,
                             topics=topics))
    return recs


class TestBuildPool:
    def test_excludes_superseded_and_questionless(self):
        recs = _pool() + [
            _rec("s01", "2024/superseded.docx", "Old question?",
                 is_current=False),
            _rec("e01", "2024/expert.docx", None, doc_type="expert_note"),
            _rec("q01", "2024/noquestion.docx", None),
        ]
        pool, excluded = build_pool(recs)
        ids = {r["id"] for r in pool}
        assert "s01" not in ids and "e01" not in ids and "q01" not in ids
        assert excluded["n_not_current"] == 1
        assert excluded["n_no_question"] == 2
        assert excluded["n_duplicate_question"] == 0

    def test_duplicate_question_keeps_newest_thread(self):
        recs = [
            _rec("old", "2023/old.docx", "Same question?", year=2023,
                 thread_date="2023-01-01"),
            _rec("new", "2026/new.docx", "Same question?", year=2026,
                 thread_date="2026-01-01"),
        ]
        pool, excluded = build_pool(recs)
        assert [r["id"] for r in pool] == ["new"]
        assert excluded["n_duplicate_question"] == 1

    def test_undated_thread_falls_back_to_folder_year(self, alias_table):
        from qa_pipeline.golden import year_of
        rec = _rec("u1", "2025/undated.docx", "Q?", year=2025,
                   thread_date=None)
        assert year_of(rec) == 2025


class TestLargestRemainder:
    def test_exact_proportional_with_remainders(self):
        alloc = largest_remainder({"a": 3, "b": 1}, 7)
        assert alloc == {"a": 5, "b": 2}
        assert sum(alloc.values()) == 7

    def test_caps_and_minimums(self):
        alloc = largest_remainder({"a": 10, "b": 1}, 6,
                                  caps={"a": 2}, minimums={"b": 1})
        assert alloc == {"a": 2, "b": 4}  # a capped, b soaks the rest

    def test_impossible_raises(self):
        with pytest.raises(ValueError):
            largest_remainder({"a": 1}, 3, caps={"a": 1})

    def test_empty_weights_raise(self):
        with pytest.raises(ValueError):
            largest_remainder({}, 3)


class TestRunGolden:
    def test_sizes_splits_and_determinism(self, alias_table):
        pool = _pool()
        items, swaps, summary = run_golden(pool, alias_table, n_sample=10)
        assert len(items) == 10
        assert summary["splits"]["sampled"] == {"dev": 5, "test": 5}
        assert summary["splits"]["total"] == {"dev": 30, "test": 30}
        assert summary["n_items"] == len(items)
        # item numbering is dense and ordered
        assert [i["item_no"] for i in items] == [f"G-{n:03d}" for n in range(1, 11)]

        again = run_golden(_pool(), alias_table, n_sample=10)
        assert [i["id"] for i in again[0]] == [i["id"] for i in items]
        assert again[2] == summary

    def test_input_order_does_not_matter(self, alias_table):
        pool = _pool()
        shuffled = list(reversed(pool))
        a = run_golden(pool, alias_table, n_sample=8)[0]
        b = run_golden(shuffled, alias_table, n_sample=8)[0]
        assert [i["id"] for i in a] == [i["id"] for i in b]

    def test_recent_years_boosted_and_singles_floored(self, alias_table):
        pool = _pool()  # 2022:1, 2023:6, 2026:11
        _, _, summary = run_golden(pool, alias_table, n_sample=10)
        by_year = {r["year"]: r for r in summary["year_allocation"]}
        assert by_year[2022]["sampled"] == 1  # floor keeps the 2022 single
        # 2026 pool share is 61% but weight x2 pushes its sample share up
        assert by_year[2026]["sampled"] >= 6
        assert sum(r["sampled"] for r in by_year.values()) == 10

    def test_pool_smaller_than_sample_takes_everything(self, alias_table):
        pool = _pool()[:3]
        items, _, summary = run_golden(pool, alias_table, n_sample=250)
        assert len(items) == 3
        assert summary["unmet"] == [] or summary["unmet"]

    def test_unknown_part_no_raises(self, alias_table):
        pool = _pool() + [_rec("x1", "2024/x.docx", "Q?", products=[9999])]
        with pytest.raises(ValueError, match="9999"):
            run_golden(pool, alias_table, n_sample=5)

    def test_every_stratum_of_two_lands_in_both_splits(self, alias_table):
        # one family dominates 2026 so its cell gets >=2 items
        pool = [_rec(f"m{i:02d}", f"2026/m{i:02d}.docx", f"Merge question {i}?",
                     year=2026, thread_date="2026-04-01", products=[1005],
                     topics=["multivitamin"]) for i in range(8)]
        items, _, _ = run_golden(pool, alias_table, n_sample=8)
        splits = {(i["family"], i["split"]) for i in items}
        assert ("Active MV", "dev") in splits and ("Active MV", "test") in splits

    def test_empty_pool_raises(self, alias_table):
        with pytest.raises(ValueError, match="empty"):
            run_golden([], alias_table)


class TestCoverageRepair:
    def _pn2fam(self, alias_table):
        from qa_pipeline.golden import part_no_families
        return part_no_families(alias_table)

    def test_missing_faq_family_is_swapped_in(self, alias_table):
        pn2fam = self._pn2fam(alias_table)
        # 6 Active MV docs sampled; 3 AminoFormula docs exist but none made
        # the cut — the FAQ floor (min(3, pool)) must swap them in
        sample0 = [_rec(f"mv{i}", f"2024/mv{i}.docx", f"MV Q{i}?",
                        products=[1005], topics=["multivitamin"])
                   for i in range(6)]
        pool = sample0 + [
            _rec(f"af{i}", f"2024/af{i}.docx", f"AminoFormula Q{i}?",
                 products=[1213], topics=["amino"]) for i in range(3)]
        sample, swaps = repair_coverage(sample0, pool, pn2fam, "seed")
        assert len(sample) == 6  # swaps replace, never grow the sample
        fams = sorted(f for r in sample for f in record_families(r, pn2fam))
        assert fams == ["Active MV"] * 3 + ["AminoFormula"] * 3
        assert len(swaps) == 3
        assert {s["requirement"] for s in swaps} == {"family:AminoFormula"}

    def test_missing_topic_is_swapped_in(self, alias_table):
        pn2fam = self._pn2fam(alias_table)
        sample0 = [_rec(f"p{i}", f"2024/p{i}.docx", f"Q{i}?", products=[1005])
                   for i in range(3)]
        pool = sample0 + [_rec("t1", "2024/t1.docx", "Topic Q?",
                               products=[1005], topics=["creatine"])]
        sample, swaps = repair_coverage(sample0, pool, pn2fam, "seed")
        assert any("creatine" in (r.get("topics") or []) for r in sample)
        assert swaps and swaps[0]["requirement"] == "topic:creatine"

    def test_sole_coverage_is_never_the_victim(self, alias_table):
        pn2fam = self._pn2fam(alias_table)
        sole = _rec("sole", "2024/sole.docx", "Sole creatine Q?",
                    products=[1005], topics=["creatine"])
        others = [_rec(f"o{i}", f"2024/o{i}.docx", f"Other Q {i}?",
                       products=[1005]) for i in range(4)]
        missing = [_rec(f"af{i}", f"2024/af{i}.docx", f"AminoFormula Q{i}?",
                        products=[1213]) for i in range(3)]
        pool = [sole] + others + missing
        sample = [sole, others[0]]
        sample, _ = repair_coverage(sample, pool, pn2fam, "seed")
        ids = {r["id"] for r in sample}
        assert "sole" in ids          # only creatine coverage — protected

    def test_unmet_reported_when_pool_cannot_cover(self, alias_table):
        pn2fam = self._pn2fam(alias_table)
        # every pool doc covers the same requirement set, and the sample is
        # smaller than the FAQ floor: no swap can reduce the deficit
        pool = [_rec(f"p{i}", f"2024/p{i}.docx", f"Q{i}?", products=[1005],
                     topics=["multivitamin"]) for i in range(6)]
        sample, swaps = repair_coverage(pool[:1], pool, pn2fam, "seed")
        assert swaps == []  # no-progress guard rejects equivalent-doc swaps
        unmet = unmet_requirements(sample, pool, pn2fam)
        assert "family:Active MV" in unmet


class TestWriters:
    def test_worksheet_has_labeling_fields(self, tmp_path, alias_table):
        items, _, _ = run_golden(_pool(), alias_table, n_sample=4)
        path = tmp_path / "worksheet.md"
        write_worksheet(path, items)
        text = path.read_text(encoding="utf-8")
        for item in items:
            assert f"## {item['item_no']} [" in text
        assert text.count("**Points to hit (2–5):**") == 4
        assert "- 5. …" in text               # five blank point slots
        assert "**Forbidden:**" in text
        assert "authority 1" in text and "authority 3" in text
        assert "**Source answer:**" in text
        assert "> Expert answer." in text     # answer blockquoted

    def test_worksheet_escapes_pipes_and_newlines(self, tmp_path, alias_table):
        rec = _rec("pipe", "2024/pipe.docx", "Q | with pipe?",
                   topics=["a|b"], answer="line1\nline2")
        items, _, _ = run_golden([rec], alias_table, n_sample=1)
        path = tmp_path / "worksheet.md"
        write_worksheet(path, items)
        text = path.read_text(encoding="utf-8")
        assert "a\\|b" in text                # pipe escaped, no table break
        assert "> line1\n> line2" in text     # both answer lines quoted

    def test_adversarial_worksheet_shape(self, tmp_path):
        path = tmp_path / "adversarial.md"
        write_adversarial_worksheet(path, build_adversarial())
        text = path.read_text(encoding="utf-8")
        blocks = [ln for ln in text.splitlines() if ln.startswith("### A-")]
        assert len(blocks) == ADVERSARIAL_SIZE == 50
        devs = sum(1 for b in blocks if "[dev]" in b)
        tests = sum(1 for b in blocks if "[test]" in b)
        assert (devs, tests) == (25, 25)
        for plan in ADVERSARIAL_PLAN:
            n_cat = sum(1 for b in blocks if b.endswith(plan["category"]))
            assert n_cat == plan["n"]
        assert text.count("- **Q:** ") == 50
        assert text.count("- Forbidden: ") == 50
        assert "escalation accuracy" in text
        assert "…" not in text          # the scaffold blanks are gone


class TestAdversarial:
    """The 50 written items (§12) — the escalation-accuracy metric's input."""

    def test_composition_matches_the_plan(self):
        items = build_adversarial()
        assert len(items) == ADVERSARIAL_SIZE == 50
        for plan in ADVERSARIAL_PLAN:
            n_cat = sum(1 for i in items if i["category"] == plan["category"])
            assert n_cat == plan["n"]
        assert [i["item_no"] for i in items] == [f"A-{n:03d}" for n in range(1, 51)]

    def test_splits_are_even_and_alternate(self):
        items = build_adversarial()
        assert sum(1 for i in items if i["split"] == "dev") == 25
        assert sum(1 for i in items if i["split"] == "test") == 25
        # every category divides across both halves, not just the totals
        for plan in ADVERSARIAL_PLAN:
            rows = [i for i in items if i["category"] == plan["category"]]
            assert {i["split"] for i in rows} == {"dev", "test"}

    def test_every_item_is_gradeable(self):
        for item in build_adversarial():
            assert item["question"].strip()
            assert item["expected_behavior"].strip()
            assert 1 <= len(item["points"]) <= 3, item["item_no"]
            assert all(p.strip() for p in item["points"]), item["item_no"]
            assert item["forbidden"].strip(), item["item_no"]

    def test_forbidden_clauses_name_behavior_not_keywords(self):
        # Open item 23: a rubric clause phrased as a noun ("treat / cure
        # language", "any efficacy claim") reads as a keyword list, and the
        # judge flags the refusal that has to name the claim it refuses.
        # Every clause is a verb the response would have to perform.
        for item in build_adversarial():
            for clause in item["forbidden"].split(";"):
                head = clause.strip().split()[0].lower()
                assert head.endswith("ing"), (item["item_no"], clause)

    def test_questions_are_unique(self):
        questions = [i["question"] for i in build_adversarial()]
        assert len(set(questions)) == len(questions)

    def test_build_raises_when_counts_drift_from_the_plan(self, monkeypatch):
        # A silently short category reports escalation accuracy for a
        # different experiment than §12 specifies.
        short = [i for i in CURATED_ADVERSARIAL
                 if i["category"] != "claim_trap"]
        monkeypatch.setattr("qa_pipeline.golden.CURATED_ADVERSARIAL", short)
        with pytest.raises(ValueError, match="counts disagree"):
            build_adversarial()

    def test_build_raises_on_an_unknown_category(self, monkeypatch):
        monkeypatch.setattr(
            "qa_pipeline.golden.CURATED_ADVERSARIAL",
            [*CURATED_ADVERSARIAL,
             {"category": "made_up", "question": "q", "points": ["p"],
              "forbidden": "f"}])
        with pytest.raises(ValueError, match="not in ADVERSARIAL_PLAN"):
            build_adversarial()

    def test_build_raises_on_a_duplicate_question(self, monkeypatch):
        doubled = [*CURATED_ADVERSARIAL]
        # keep the category counts valid so the duplicate check is what fires
        doubled[1] = {**doubled[1], "question": doubled[0]["question"]}
        monkeypatch.setattr("qa_pipeline.golden.CURATED_ADVERSARIAL", doubled)
        with pytest.raises(ValueError, match="duplicate adversarial"):
            build_adversarial()

    def test_jsonl_is_one_object_per_item(self, tmp_path):
        path = tmp_path / "adversarial.jsonl"
        items = build_adversarial()
        write_adversarial_jsonl(path, items)
        lines = path.read_text(encoding="utf-8").splitlines()
        assert len(lines) == 50
        parsed = [json.loads(ln) for ln in lines]
        assert parsed == items
        assert path.read_bytes().endswith(b"\n")


def _index_docs(n_pdsrg=8, n_podcast=8, stems=("activemv", "wheysmooth"),
                episodes=("ep-one", "ep-two")):
    """Synthetic §9 index rows in the two probe-bearing shapes."""
    docs = []
    for i in range(n_pdsrg):
        stem = stems[i % len(stems)]
        docs.append({
            "id": f"pdsrg-{stem}-{i:03d}",
            "source_type": "pdsrg",
            "title": f"Section {i}",
            # heading path line, blank line, then the body
            "content": f"Guide > {stem} > Section {i}\n\n"
                       + " ".join(f"body{i}word{w}" for w in range(40)),
            "locator": f"{stem} (p. {i})",
        })
    for i in range(n_podcast):
        episode = episodes[i % len(episodes)]
        docs.append({
            "id": f"podcast-{episode}-{i:03d}",
            "source_type": "podcast",
            "title": f"{episode} (00:00–01:00)",
            "content": f"Speaker 1: talk{i} "
                       + " ".join(f"said{i}word{w}" for w in range(40)),
            "locator": "00:00–01:00",
        })
    return docs


class TestProbes:
    """Retrieval probes over PDSRG/podcast — §12 coverage gap, open item 15."""

    def test_query_drops_the_heading_path_and_speaker_labels(self):
        docs = _index_docs(n_pdsrg=1, n_podcast=1)
        pdsrg_query = probe_query(docs[0])
        assert pdsrg_query is not None
        # the heading path would let BM25 answer on the title field alone
        assert "Guide >" not in pdsrg_query
        assert "Section 0" not in pdsrg_query
        assert pdsrg_query.startswith("body0word0")

        podcast_query = probe_query(docs[1])
        assert podcast_query is not None
        assert "Speaker 1:" not in podcast_query
        assert podcast_query.startswith("talk0")

    def test_query_is_capped_at_the_word_budget(self):
        query = probe_query(_index_docs(n_pdsrg=1, n_podcast=0)[0])
        assert len(query.split(" ")) == PROBE_QUERY_WORDS

    def test_short_chunks_are_not_queryable(self):
        doc = {"id": "pdsrg-x-001", "source_type": "pdsrg",
               "content": "Guide > x > y\n\ntoo short", "title": "y"}
        assert probe_query(doc) is None

    def test_every_stratum_is_probed_at_least_once(self):
        probes, summary = build_probes(_index_docs(), n_per_source=4)
        for source in PROBE_SOURCES:
            strata = {p["stratum"] for p in probes
                      if p["source_type"] == source}
            assert len(strata) == 2       # both stems, both episodes
        assert summary["n_probes"] == len(probes) == 8

    def test_probe_points_at_a_real_document_id(self):
        docs = _index_docs()
        ids = {d["id"] for d in docs}
        probes, _ = build_probes(docs, n_per_source=4)
        assert all(p["expected_doc_id"] in ids for p in probes)
        # a probe never expects a document from the other corpus
        for probe in probes:
            assert probe["expected_doc_id"].startswith(probe["source_type"])

    def test_deterministic_across_input_order(self):
        docs = _index_docs()
        a, sa = build_probes(docs, n_per_source=4)
        b, sb = build_probes(list(reversed(docs)), n_per_source=4)
        assert a == b and sa == sb

    def test_missing_corpus_raises_rather_than_shrinking(self):
        # An index that lost its podcast documents must fail loudly, not
        # quietly emit a probe set that no longer covers them.
        pdsrg_only = [d for d in _index_docs() if d["source_type"] == "pdsrg"]
        with pytest.raises(ValueError, match="cannot probe a corpus"):
            build_probes(pdsrg_only, n_per_source=4)

    def test_unexpected_document_id_shape_raises(self):
        with pytest.raises(ValueError, match="document id shape"):
            probe_stratum({"id": "pdsrg-001"})

    def test_jsonl_round_trips(self, tmp_path):
        probes, _ = build_probes(_index_docs(), n_per_source=4)
        path = tmp_path / "probes.jsonl"
        write_probes_jsonl(path, probes)
        lines = path.read_text(encoding="utf-8").splitlines()
        assert [json.loads(ln) for ln in lines] == probes


class TestMultiTurnSet:
    """The multi-turn safety items (§12 coverage, open item 19)."""

    def test_composition_matches_its_plan(self):
        items = build_multiturn()

        assert len(items) == sum(p["n"] for p in MULTI_TURN_PLAN)
        for plan in MULTI_TURN_PLAN:
            assert sum(1 for i in items
                       if i["category"] == plan["category"]) == plan["n"]
        assert [i["item_no"] for i in items[:2]] == ["M-001", "M-002"]
        assert {i["split"] for i in items} == {"dev", "test"}
        assert (sum(1 for i in items if i["split"] == "dev")
                == sum(1 for i in items if i["split"] == "test"))

    def test_every_item_is_actually_multi_turn(self):
        # An item whose trigger is in its own question measures the
        # adversarial 50 again — the set exists for the other case.
        for item in build_multiturn():
            assert item["history"], item["item_no"]
            assert {t["role"] for t in item["history"]} <= {"user", "assistant"}
            assert all(t["text"].strip() for t in item["history"])

    def test_each_item_asserts_exactly_one_behavior(self):
        for item in build_multiturn():
            asserted = [f for f in ("expect_escalate", "expect_claim_trap")
                        if item[f] is not None]
            assert len(asserted) == 1, item["item_no"]

    def test_controls_are_present_and_expect_no_escalation(self):
        # Half the value of the set: a guardrail that escalates on any
        # transcript keyword passes every delayed item and refuses "where is
        # my order" for the rest of the session.
        controls = [i for i in build_multiturn()
                    if i["category"] == "no_escalation_control"]
        assert controls
        assert all(i["expect_escalate"] is False for i in controls)

    def test_curated_roles_map_to_the_wire_vocabulary(self):
        # The curation says "customer"; POST /ask and `ask --history` say
        # "user", and the artifact is the wire's.
        assert {r for r, _ in CURATED_MULTI_TURN[0]["history"]} <= set(MULTI_TURN_ROLES)
        assert build_multiturn()[0]["history"][0]["role"] == "user"

    def test_a_single_turn_item_raises(self, monkeypatch):
        item = dict(CURATED_MULTI_TURN[0], history=[])
        monkeypatch.setattr(golden, "CURATED_MULTI_TURN",
                            [item] + CURATED_MULTI_TURN[1:])
        with pytest.raises(ValueError, match="no history"):
            build_multiturn()

    def test_an_item_asserting_both_behaviors_raises(self, monkeypatch):
        # An escalation templates the refusal and never reaches an answer, so
        # a row demanding both can never pass — better to refuse to emit it.
        item = dict(CURATED_MULTI_TURN[0], expect_escalate=True,
                    expect_claim_trap=True)
        monkeypatch.setattr(golden, "CURATED_MULTI_TURN",
                            [item] + CURATED_MULTI_TURN[1:])
        with pytest.raises(ValueError, match="one behavior per item"):
            build_multiturn()

    def test_a_count_that_disagrees_with_the_plan_raises(self, monkeypatch):
        monkeypatch.setattr(golden, "CURATED_MULTI_TURN",
                            CURATED_MULTI_TURN[:-1])
        with pytest.raises(ValueError, match="disagree with MULTI_TURN_PLAN"):
            build_multiturn()

    def test_duplicate_questions_raise(self, monkeypatch):
        duped = dict(CURATED_MULTI_TURN[-1],
                     question=CURATED_MULTI_TURN[0]["question"])
        monkeypatch.setattr(golden, "CURATED_MULTI_TURN",
                            CURATED_MULTI_TURN[:-1] + [duped])
        with pytest.raises(ValueError, match="duplicate multi-turn questions"):
            build_multiturn()

    def test_worksheet_renders_the_conversation_and_the_reason(self, tmp_path):
        # This set cannot be reviewed question by question: whether an item is
        # fair depends on what the transcript already said.
        items = build_multiturn()
        path = tmp_path / "multiturn.md"
        write_multiturn_worksheet(path, items)
        text = path.read_text(encoding="utf-8")

        assert "open item 19" in text
        assert "- Why: " in text
        assert "*user:*" in text
        assert items[0]["question"] in text

    def test_jsonl_is_one_object_per_item_with_its_history(self, tmp_path):
        items = build_multiturn()
        path = tmp_path / "multiturn.jsonl"
        write_multiturn_jsonl(path, items)
        rows = [json.loads(line)
                for line in path.read_text(encoding="utf-8").splitlines()]

        assert len(rows) == len(items)
        assert rows[0]["history"] == items[0]["history"]
        assert rows[0]["expect_escalate"] is True


class TestCli:
    def test_golden_end_to_end_byte_identical(self, tmp_path, monkeypatch):
        products_path = tmp_path / "products.json"
        products_path.write_text(json.dumps(PRODUCTS), encoding="utf-8")
        qa_dir = tmp_path / "stage4"
        qa_dir.mkdir()
        (qa_dir / "documents.jsonl").write_text(
            "".join(json.dumps(r, ensure_ascii=False) + "\n" for r in _pool()),
            encoding="utf-8")
        monkeypatch.chdir(tmp_path)

        rc = main(["golden", "--qa-docs", "stage4/documents.jsonl",
                   "--products", "products.json", "--out", "golden"])
        assert rc == 0
        out = tmp_path / "golden"
        names = sorted(p.name for p in out.iterdir() if p.is_file())
        assert names == ["adversarial.jsonl", "adversarial.md",
                         "multiturn.jsonl", "multiturn.md", "sample.jsonl",
                         "summary.json", "worksheet.md"]
        first = {n: (out / n).read_bytes() for n in names}

        # rerun from a different working directory: byte-identical outputs
        (tmp_path / "elsewhere").mkdir()
        monkeypatch.chdir(tmp_path / "elsewhere")
        rc = main(["golden", "--qa-docs", str(qa_dir / "documents.jsonl"),
                   "--products", str(products_path), "--out",
                   str(tmp_path / "golden2")])
        assert rc == 0
        for n in names:
            assert (tmp_path / "golden2" / n).read_bytes() == first[n], n

    def test_written_only_leaves_the_draw_alone(self, tmp_path, monkeypatch):
        # Re-drawing the 250 is an owner decision (open item 8); rebuilding
        # the written 50/20 after a rubric change (open item 23) is not.
        (tmp_path / "products.json").write_text(json.dumps(PRODUCTS),
                                                encoding="utf-8")
        qa_dir = tmp_path / "stage4"
        qa_dir.mkdir()
        (qa_dir / "documents.jsonl").write_text(
            "".join(json.dumps(r, ensure_ascii=False) + "\n" for r in _pool()),
            encoding="utf-8")
        monkeypatch.chdir(tmp_path)
        assert main(["golden", "--qa-docs", "stage4/documents.jsonl",
                     "--products", "products.json", "--out", "golden"]) == 0
        out = tmp_path / "golden"
        before = {n: (out / n).read_bytes()
                  for n in ("sample.jsonl", "worksheet.md",
                            "adversarial.jsonl")}
        (out / "adversarial.jsonl").write_text("stale\n", encoding="utf-8")

        assert main(["golden", "--out", "golden", "--written-only"]) == 0
        assert (out / "adversarial.jsonl").read_bytes() == before["adversarial.jsonl"]
        assert (out / "sample.jsonl").read_bytes() == before["sample.jsonl"]
        assert (out / "worksheet.md").read_bytes() == before["worksheet.md"]
        summary = json.loads((out / "summary.json").read_text(encoding="utf-8"))
        assert summary["golden_version"] == GOLDEN_VERSION
        assert summary["n_items"] == len(
            (out / "sample.jsonl").read_text(encoding="utf-8").splitlines())

    def test_written_only_needs_an_existing_dir(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        assert main(["golden", "--out", "nope", "--written-only"]) == 2

    def test_missing_inputs_exit_2(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        rc = main(["golden", "--qa-docs", "nope.jsonl", "--products",
                   "products.json", "--out", "golden"])
        assert rc == 2

    def test_probes_are_emitted_when_the_index_build_is_present(
            self, tmp_path, monkeypatch):
        (tmp_path / "products.json").write_text(json.dumps(PRODUCTS),
                                                encoding="utf-8")
        qa_dir = tmp_path / "stage4"
        qa_dir.mkdir()
        (qa_dir / "documents.jsonl").write_text(
            "".join(json.dumps(r, ensure_ascii=False) + "\n" for r in _pool()),
            encoding="utf-8")
        (tmp_path / "index.jsonl").write_text(
            "".join(json.dumps(d, ensure_ascii=False) + "\n"
                    for d in _index_docs()),
            encoding="utf-8")
        monkeypatch.chdir(tmp_path)

        rc = main(["golden", "--qa-docs", "stage4/documents.jsonl",
                   "--products", "products.json",
                   "--index-docs", "index.jsonl", "--out", "golden"])
        assert rc == 0
        out = tmp_path / "golden"
        assert (out / "probes.jsonl").is_file()
        summary = json.loads((out / "summary.json").read_text(encoding="utf-8"))
        assert summary["probes"]["n_probes"] > 0
        assert set(summary["probes"]["n_per_source"]) == set(PROBE_SOURCES)
