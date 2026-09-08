"""Stage 4 dedup & currency tests (plan §4 Stage 4) — synthetic fixtures, no network."""

from __future__ import annotations

import hashlib
import json

import pytest

from qa_pipeline.alias import build_alias_table
from qa_pipeline.stage2 import is_audit_sample
from qa_pipeline import stage4 as stage4_mod
from qa_pipeline.stage4 import (
    CURATED_CLUSTER_DISPOSITIONS,
    DISPOSITION_SPLIT,
    STATUS_CURRENT,
    STATUS_SUPERSEDED_CURRENCY,
    STATUS_SUPERSEDED_DUP,
    classify_cues,
    cluster_questions,
    currency_cache_key,
    currency_messages,
    cue_descriptions,
    disposition_for,
    normalize_vector,
    pick_canonical,
    products_conflict,
    run_stage4,
    share_bucket,
    summarize,
    validate_judgment,
)


def _product(part_no, longname):
    return {"part_no": part_no, "longname": longname, "coid": 1,
            "URL": "https://www.dotFIT.com/x", "searchcontent": "x"}


# mirror of the test_alias.py / test_stage2.py synthetic corpus (covers every
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

V_SAME = [1.0, 0.0]                 # cos 1.0 with itself
V_NEAR = [0.94, 0.342]              # cos ~0.94 with V_SAME (>= 0.88)
V_BELOW = [0.87, 0.4931]            # cos ~0.87 with V_SAME (< 0.88)
V_OTHER = [0.0, 1.0]                # orthogonal


@pytest.fixture
def alias_table():
    return build_alias_table(PRODUCTS)


def _s2rec(rid, source_file, question=None, answer="Expert answer.",
           products=None, topics=None, cues=(), thread_date=None,
           doc_type="qa_email"):
    rec = {
        "id": rid,
        "source_file": source_file,
        "year": 2024,
        "doc_type": doc_type,
        "thread_date": thread_date,
        "topic_subfolder": "",
        "filename": source_file.rsplit("/", 1)[-1],
        "question_original": question,
        "question_canonical": question,
        "answer": answer,
        "products": list(products or []),
        "products_unresolved": [],
        "topics": list(topics or []),
        "audience_flags": {},
        "currency_cues": list(cues),
        "residual_pii_flag": False,
        "confidence": 0.95,
        "containment_score": 0.99,
        "needs_review": False,
        "llm_error": None,
        "model": "gpt-5-mini",
    }
    return rec


def _embed_map(mapping):
    def call(texts):
        assert len(set(texts)) == len(texts)  # unique texts only
        return [mapping[t] for t in texts]
    return call


def _judge(dependent=True, confidence=0.9, fail=False):
    calls = {"n": 0}

    def call(messages):
        calls["n"] += 1
        if fail:
            raise RuntimeError("api down")
        return {"formulation_dependent": dependent,
                "evidence": "the answer doses Recover&Build",
                "confidence": confidence}
    return call, calls


def _audit_id(seed):
    """A 16-hex id that lands in the 5% audit sample (deterministic)."""
    n = 0
    while True:
        cand = hashlib.sha256(f"{seed}{n}".encode()).hexdigest()[:16]
        if is_audit_sample(cand):
            return cand
        n += 1


def _no_audit_id(seed):
    n = 0
    while True:
        cand = hashlib.sha256(f"{seed}{n}".encode()).hexdigest()[:16]
        if not is_audit_sample(cand):
            return cand
        n += 1


def _no_audit_id_above(floor, seed):
    """A non-audit id sorting above *floor* — keeps *floor* the cluster id."""
    n = 0
    while True:
        cand = hashlib.sha256(f"{seed}{n}".encode()).hexdigest()[:16]
        if not is_audit_sample(cand) and cand > floor:
            return cand
        n += 1


# --- cue classification --------------------------------------------------------


class TestClassifyCues:
    def test_rename_classified(self, alias_table):
        assert classify_cues(["LeanMR"], alias_table) == {
            "rename": ["LeanMR"], "gone_or_replaced": []}

    def test_replacement_and_discontinued_are_gone(self, alias_table):
        out = classify_cues(["Recover&Build", "KidsMV"], alias_table)
        assert out["gone_or_replaced"] == ["Recover&Build", "KidsMV"]
        assert out["rename"] == []

    def test_mixed_cues_split(self, alias_table):
        out = classify_cues(["LeanMR", "VeganMV", "NO7 Rage"], alias_table)
        assert out["rename"] == ["LeanMR", "NO7 Rage"]
        assert out["gone_or_replaced"] == ["VeganMV"]

    def test_unknown_cue_raises(self, alias_table):
        with pytest.raises(ValueError, match="unknown currency cue"):
            classify_cues(["MysteryProduct"], alias_table)

    def test_descriptions_cover_both_classes(self, alias_table):
        out = cue_descriptions(["Recover&Build", "KidsMV"], alias_table)
        assert len(out) == 2
        assert any("Recover&Build" in d and "DIFFERENT formula" in d
                   for d in out)
        assert any(d.startswith("KidsMV") for d in out)


# --- judgment validation ---------------------------------------------------------


class TestValidateJudgment:
    def test_good_passes(self):
        out = validate_judgment({"formulation_dependent": True,
                                 "evidence": " quote ",
                                 "confidence": 0.85})
        assert out == {"formulation_dependent": True, "evidence": "quote",
                       "confidence": 0.85}

    def test_non_bool_dependent_raises(self):
        with pytest.raises(ValueError):
            validate_judgment({"formulation_dependent": "yes",
                               "evidence": "x", "confidence": 0.9})

    def test_missing_evidence_raises(self):
        with pytest.raises(ValueError):
            validate_judgment({"formulation_dependent": False,
                               "evidence": "  ", "confidence": 0.9})

    @pytest.mark.parametrize("conf", [-0.1, 1.1, True, "0.9"])
    def test_bad_confidence_raises(self, conf):
        with pytest.raises(ValueError):
            validate_judgment({"formulation_dependent": False,
                               "evidence": "x", "confidence": conf})

    def test_messages_carry_cue_question_answer(self, alias_table):
        rec = _s2rec("a" * 16, "2024/q.docx", question="Is Recover&Build safe?",
                     cues=["Recover&Build"])
        msgs = currency_messages(rec, ["Recover&Build"], alias_table)
        assert msgs[0]["role"] == "system"
        user = msgs[1]["content"]
        assert "Recover&Build" in user and "DIFFERENT formula" in user
        assert "Is Recover&Build safe?" in user
        assert "Expert answer." in user


# --- clustering -----------------------------------------------------------------


class TestClustering:
    def _vec(self, q, v):
        return {q: v}

    def test_near_duplicates_cluster(self):
        a = _s2rec("a" * 16, "2023/a.docx", question="Is creatine safe?",
                   topics=["creatine"], thread_date="2023-01-01")
        b = _s2rec("b" * 16, "2024/b.docx", question="Is creatine ok to take?",
                   topics=["creatine"], thread_date="2024-01-01")
        clusters = cluster_questions(
            [a, b],
            {"Is creatine safe?": V_SAME, "Is creatine ok to take?": V_NEAR})
        assert len(clusters) == 1
        assert {m["id"] for m in clusters[0]} == {a["id"], b["id"]}

    def test_below_threshold_no_cluster(self):
        a = _s2rec("a" * 16, "2023/a.docx", question="Q one",
                   topics=["creatine"])
        b = _s2rec("b" * 16, "2024/b.docx", question="Q two",
                   topics=["creatine"])
        assert cluster_questions(
            [a, b], {"Q one": V_SAME, "Q two": V_BELOW}) == []

    def test_bucket_gates_near_duplicates(self):
        a = _s2rec("a" * 16, "2023/a.docx", question="Q same",
                   topics=["creatine"], products=[1213])
        b = _s2rec("b" * 16, "2024/b.docx", question="Q same 2",
                   topics=["sleep"], products=[1005])
        assert cluster_questions(
            [a, b], {"Q same": V_SAME, "Q same 2": V_NEAR}) == []

    def test_identical_text_overrides_bucket(self):
        a = _s2rec("a" * 16, "2023/a.docx", question="How can I lose fat?",
                   topics=["creatine"])
        b = _s2rec("b" * 16, "2024/b.docx", question="How can I lose fat?",
                   topics=["sleep"])
        clusters = cluster_questions(
            [a, b], {"How can I lose fat?": V_SAME})
        assert len(clusters) == 1

    def test_transitive_chain_merges(self):
        qs = ["AAA chain?", "BBB chain?", "CCC chain?"]
        recs = [_s2rec(f"{c}" * 16, f"2024/{c}.docx", question=q,
                       topics=["creatine"]) for c, q in zip("abc", qs)]
        vecs = {qs[0]: [1.0, 0.0], qs[1]: [0.9, 0.4359], qs[2]: [0.62, 0.7846]}
        clusters = cluster_questions(recs, vecs)
        assert len(clusters) == 1 and len(clusters[0]) == 3

    def test_cluster_id_is_min_member_id_order_independent(self):
        qs = ["Q one?", "Q two?"]
        recs = [_s2rec("f" * 16, "2023/a.docx", question=qs[0], topics=["t"],
                       thread_date="2023-01-01"),
                _s2rec("1" * 16, "2024/b.docx", question=qs[1], topics=["t"],
                       thread_date="2024-01-01")]
        vecs = {qs[0]: V_SAME, qs[1]: V_NEAR}
        c1 = cluster_questions(list(recs), vecs)
        c2 = cluster_questions(list(reversed(recs)), vecs)
        assert c1 == c2
        # members sort by (thread_date, source_file) regardless of input order
        assert c1[0][0]["thread_date"] == "2023-01-01"
        assert c1[0][1]["thread_date"] == "2024-01-01"

    def test_share_bucket_topic_match_is_casefolded(self):
        a = _s2rec("a" * 16, "a.docx", topics=["Creatine"])
        b = _s2rec("b" * 16, "b.docx", topics=["creatine"])
        assert share_bucket(a, b)
        c = _s2rec("c" * 16, "c.docx", topics=["sleep"])
        assert not share_bucket(a, c)


# --- canonical pick & conflict ----------------------------------------------------


class TestPickAndConflict:
    def test_newest_wins(self):
        members = [_s2rec("a" * 16, "a.docx", thread_date="2023-05-01"),
                   _s2rec("b" * 16, "b.docx", thread_date="2024-05-01")]
        assert pick_canonical(members)["id"] == "b" * 16

    def test_null_date_loses(self):
        members = [_s2rec("a" * 16, "a.docx", thread_date=None),
                   _s2rec("b" * 16, "b.docx", thread_date="2019-01-01")]
        assert pick_canonical(members)["id"] == "b" * 16

    def test_tie_breaks_on_source_file(self):
        members = [_s2rec("a" * 16, "2024/a.docx", thread_date="2024-01-01"),
                   _s2rec("b" * 16, "2024/b.docx", thread_date="2024-01-01")]
        assert pick_canonical(members)["source_file"] == "2024/b.docx"

    def test_nested_products_no_conflict(self):
        members = [_s2rec("a" * 16, "a.docx", products=[1213, 1216]),
                   _s2rec("b" * 16, "b.docx", products=[1213]),
                   _s2rec("c" * 16, "c.docx", products=[])]
        assert not products_conflict(members)

    def test_non_nested_products_conflict(self):
        members = [_s2rec("a" * 16, "a.docx", products=[1213, 1005]),
                   _s2rec("b" * 16, "b.docx", products=[1213, 1020])]
        assert products_conflict(members)


# --- run_stage4 integration --------------------------------------------------------


class TestRunStage4:
    def test_rename_only_never_supersedes(self, alias_table):
        rec = _s2rec("a" * 16, "2020/leanmr.docx", question="Is LeanMR good?",
                     cues=["LeanMR"], products=[1333], topics=["protein"])
        judge, calls = _judge(dependent=True)
        out, ws, review, stats = run_stage4(
            [rec], alias_table, _embed_map({"Is LeanMR good?": V_SAME}),
            judge, "gpt-5-mini")
        assert out[0]["stage4_status"] == STATUS_CURRENT
        assert out[0]["is_current"] is True
        assert out[0]["currency_judgment"] is None
        assert calls["n"] == 0  # renames cost no judgment

    def test_dependent_judgment_supersedes(self, alias_table):
        rec = _s2rec("a" * 16, "2020/rb.docx", question="Recover&Build dosage?",
                     cues=["Recover&Build"], topics=["recovery"])
        judge, _ = _judge(dependent=True)
        out, ws, review, stats = run_stage4(
            [rec], alias_table, _embed_map({"Recover&Build dosage?": V_SAME}),
            judge, "gpt-5-mini")
        assert out[0]["stage4_status"] == STATUS_SUPERSEDED_CURRENCY
        assert out[0]["currency_judgment"] == "dependent"
        assert out[0]["currency_evidence"].startswith("the answer doses")
        assert review == [] and stats["n_judge_calls"] == 1

    def test_independent_judgment_stays_current(self, alias_table):
        rec = _s2rec("a" * 16, "2020/kids.docx",
                     question="Supplements for kids?", cues=["KidsMV"],
                     topics=["kids"])
        judge, _ = _judge(dependent=False)
        out, _, review, _ = run_stage4(
            [rec], alias_table, _embed_map({"Supplements for kids?": V_SAME}),
            judge, "gpt-5-mini")
        assert out[0]["stage4_status"] == STATUS_CURRENT
        assert out[0]["currency_judgment"] == "independent"
        assert review == []

    def test_low_confidence_supersedes_and_queues(self, alias_table):
        rec = _s2rec("a" * 16, "2020/rb.docx", question="Recover&Build?",
                     cues=["Recover&Build"])
        judge, _ = _judge(dependent=False, confidence=0.5)
        out, _, review, _ = run_stage4(
            [rec], alias_table, _embed_map({"Recover&Build?": V_SAME}),
            judge, "gpt-5-mini")
        assert out[0]["stage4_status"] == STATUS_SUPERSEDED_CURRENCY
        assert out[0]["currency_judgment"] == "low_confidence"
        assert review[0]["reasons"] == ["currency_low_confidence"]

    def test_no_llm_supersedes_and_queues(self, alias_table):
        rec = _s2rec("a" * 16, "2020/rb.docx", question="Recover&Build?",
                     cues=["Recover&Build"])
        out, _, review, stats = run_stage4(
            [rec], alias_table, _embed_map({"Recover&Build?": V_SAME}),
            None, "rule-based")
        assert out[0]["stage4_status"] == STATUS_SUPERSEDED_CURRENCY
        assert out[0]["currency_judgment"] == "no_llm"
        assert review[0]["reasons"] == ["currency_no_llm"]
        assert stats["n_judge_calls"] == 0

    def test_judge_error_supersedes_and_queues(self, alias_table):
        rec = _s2rec("a" * 16, "2020/rb.docx", question="Recover&Build?",
                     cues=["Recover&Build"])
        judge, _ = _judge(fail=True)
        out, _, review, stats = run_stage4(
            [rec], alias_table, _embed_map({"Recover&Build?": V_SAME}),
            judge, "gpt-5-mini")
        assert out[0]["stage4_status"] == STATUS_SUPERSEDED_CURRENCY
        assert out[0]["currency_judgment"] == "error"
        assert "Recover&Build" not in (out[0]["currency_error"] or "")
        assert "api down" in out[0]["currency_error"]
        assert review[0]["reasons"] == ["currency_error"]
        assert stats["n_judge_errors"] == 1

    def test_dedup_supersedes_older_dup(self, alias_table):
        old = _s2rec(_no_audit_id("d1"), "2023/old.docx",
                     question="Is creatine safe?", topics=["creatine"],
                     thread_date="2023-01-01")
        new = _s2rec(_no_audit_id("d2"), "2024/new.docx",
                     question="Is creatine ok?", topics=["creatine"],
                     thread_date="2024-06-01")
        out, ws, review, _ = run_stage4(
            [old, new], alias_table,
            _embed_map({"Is creatine safe?": V_SAME,
                        "Is creatine ok?": V_NEAR}),
            None, "rule-based")
        by_id = {r["id"]: r for r in out}
        assert by_id[old["id"]]["stage4_status"] == STATUS_SUPERSEDED_DUP
        assert by_id[old["id"]]["superseded_by"] == new["id"]
        assert by_id[new["id"]]["stage4_status"] == STATUS_CURRENT
        assert by_id[new["id"]]["superseded_by"] is None
        assert len(ws) == 1 and ws[0]["canonical"]["id"] == new["id"]
        assert review == []

    def test_conflict_cluster_no_autopick_and_queued(self, alias_table):
        ids = sorted([_no_audit_id("x"), _no_audit_id("y")])
        a = _s2rec(ids[0], "2023/a.docx", question="Q one?",
                   topics=["creatine"], products=[1213],
                   thread_date="2023-01-01")
        b = _s2rec(ids[1], "2024/b.docx", question="Q two?",
                   topics=["creatine"], products=[1020],
                   thread_date="2024-01-01")
        out, ws, review, _ = run_stage4(
            [a, b], alias_table,
            _embed_map({"Q one?": V_SAME, "Q two?": V_NEAR}),
            None, "rule-based")
        assert all(r["stage4_status"] == STATUS_CURRENT for r in out)
        assert all(r["superseded_by"] is None for r in out)
        assert ws[0]["conflict"] is True and ws[0]["canonical"] is None
        assert [row["reasons"] for row in review] == [["cluster_conflict"],
                                                       ["cluster_conflict"]]

    def test_audit_sample_queues_canonical_cluster(self, alias_table):
        low = _audit_id("a")
        high = _no_audit_id_above(low, "b")
        a = _s2rec(low, "2023/a.docx", question="Q one?",
                   topics=["creatine"], thread_date="2023-01-01")
        b = _s2rec(high, "2024/b.docx", question="Q two?",
                   topics=["creatine"], thread_date="2024-01-01")
        out, ws, review, _ = run_stage4(
            [a, b], alias_table,
            _embed_map({"Q one?": V_SAME, "Q two?": V_NEAR}),
            None, "rule-based")
        assert ws[0]["audit"] is True and ws[0]["conflict"] is False
        assert ws[0]["canonical"]["id"] == b["id"]  # pick still happens
        assert [row["reasons"] for row in review] == [["cluster_audit"],
                                                       ["cluster_audit"]]

    def test_currency_superseded_member_not_canonical(self, alias_table):
        old = _s2rec(_no_audit_id("c1"), "2015/old.docx",
                     question="Is creatine fine?", topics=["creatine"],
                     thread_date="2015-01-01")
        cue = _s2rec(_no_audit_id("c2"), "2016/cue.docx",
                     question="Is creatine safe?", topics=["creatine"],
                     thread_date="2016-01-01", cues=["VeganMV"])
        out, ws, _, _ = run_stage4(
            [old, cue], alias_table,
            _embed_map({"Is creatine fine?": V_SAME,
                        "Is creatine safe?": V_NEAR}),
            None, "rule-based")
        by_id = {r["id"]: r for r in out}
        assert by_id[cue["id"]]["stage4_status"] == STATUS_SUPERSEDED_CURRENCY
        assert by_id[cue["id"]]["superseded_by"] is None
        assert by_id[old["id"]]["stage4_status"] == STATUS_CURRENT
        assert ws[0]["canonical"]["id"] == old["id"]  # current member wins

    def test_expert_note_not_clustered_but_currency_applies(self, alias_table):
        note = _s2rec("a" * 16, "2020/note.docx", question=None,
                      doc_type="expert_note", cues=["KidsMV"])
        out, _, review, _ = run_stage4(
            [note], alias_table, _embed_map({}), None, "rule-based")
        assert out[0]["cluster_id"] is None
        assert out[0]["stage4_status"] == STATUS_SUPERSEDED_CURRENCY

    def test_rerun_byte_identical(self, alias_table):
        recs = [
            _s2rec("1" * 16, "2023/a.docx", question="Q one?",
                   topics=["creatine"], thread_date="2023-01-01"),
            _s2rec("2" * 16, "2024/b.docx", question="Q two?",
                   topics=["creatine"], thread_date="2024-01-01",
                   cues=["Recover&Build"]),
            _s2rec("3" * 16, "2024/c.docx", question=None,
                   doc_type="expert_note"),
        ]
        judge, _ = _judge(dependent=True)

        def run():
            out, ws, review, _ = run_stage4(
                list(recs), alias_table,
                _embed_map({"Q one?": V_SAME, "Q two?": V_NEAR}),
                judge, "gpt-5-mini")
            return (json.dumps(out, sort_keys=True),
                    json.dumps(ws, sort_keys=True),
                    json.dumps(review, sort_keys=True))

        assert run() == run()

    def test_cache_hit_skips_judge_call(self, alias_table):
        rec = _s2rec("a" * 16, "2020/rb.docx", question="Recover&Build?",
                     cues=["Recover&Build"])
        key = currency_cache_key("dep", "v", rec)
        cached = {"formulation_dependent": True, "evidence": "cached",
                  "confidence": 0.99}
        judge, calls = _judge(dependent=False)
        out, _, _, stats = run_stage4(
            [dict(rec)], alias_table, _embed_map({"Recover&Build?": V_SAME}),
            judge, "dep", cache={key: dict(cached)},
            cache_key_fn=lambda _rec, _key=key: _key)
        assert stats["n_judge_cache_hits"] == 1 and calls["n"] == 0
        assert out[0]["currency_evidence"] == "cached"

    def test_unknown_cue_in_input_raises(self, alias_table):
        rec = _s2rec("a" * 16, "a.docx", cues=["MysteryProduct"])
        with pytest.raises(ValueError, match="unknown currency cue"):
            run_stage4([rec], alias_table, _embed_map({}), None, "rule-based")


# --- curated cluster dispositions (§4 step 3, owner ruling) ----------------------

class TestClusterDispositions:
    """A ruled cluster stops being queued and never dedups its members."""

    def _pair(self):
        ids = sorted([_no_audit_id("x"), _no_audit_id("y")])
        a = _s2rec(ids[0], "2023/a.docx", question="Q one?",
                   topics=["creatine"], products=[1213],
                   thread_date="2023-01-01")
        b = _s2rec(ids[1], "2024/b.docx", question="Q two?",
                   topics=["creatine"], products=[1020],
                   thread_date="2024-01-01")
        return ids, a, b

    def _rule(self, monkeypatch, cluster_id, members,
              disposition=DISPOSITION_SPLIT):
        monkeypatch.setattr(stage4_mod, "CURATED_CLUSTER_DISPOSITIONS", {
            cluster_id: {"disposition": disposition,
                         "members": sorted(members),
                         "source": "test"}})

    def test_split_keeps_both_and_empties_the_queue(self, alias_table,
                                                     monkeypatch):
        ids, a, b = self._pair()
        self._rule(monkeypatch, ids[0], ids)
        out, ws, review, stats = run_stage4(
            [a, b], alias_table,
            _embed_map({"Q one?": V_SAME, "Q two?": V_NEAR}),
            None, "rule-based")
        assert all(r["stage4_status"] == STATUS_CURRENT for r in out)
        assert all(r["superseded_by"] is None for r in out)
        assert all(r["is_current"] is True for r in out)
        assert review == []                       # the ruling clears the queue
        assert ws[0]["conflict"] is True          # the detection still stands
        assert ws[0]["disposition"] == DISPOSITION_SPLIT
        assert ws[0]["canonical"] is None
        assert stats["n_conflict_clusters"] == 1
        assert stats["n_dispositioned_clusters"] == 1

    def test_split_blocks_dedup_even_without_conflict(self, alias_table,
                                                       monkeypatch):
        ids, a, b = self._pair()
        b["products"] = [1213, 1020]              # nested: no conflict proxy
        self._rule(monkeypatch, ids[0], ids)
        out, ws, review, _ = run_stage4(
            [a, b], alias_table,
            _embed_map({"Q one?": V_SAME, "Q two?": V_NEAR}),
            None, "rule-based")
        assert ws[0]["conflict"] is False
        assert ws[0]["canonical"] is None         # split beats the auto-pick
        assert all(r["stage4_status"] == STATUS_CURRENT for r in out)
        assert review == []

    def test_unruled_cluster_still_queues(self, alias_table, monkeypatch):
        ids, a, b = self._pair()
        monkeypatch.setattr(stage4_mod, "CURATED_CLUSTER_DISPOSITIONS", {})
        _, ws, review, _ = run_stage4(
            [a, b], alias_table,
            _embed_map({"Q one?": V_SAME, "Q two?": V_NEAR}),
            None, "rule-based")
        assert ws[0]["disposition"] is None
        assert [row["reasons"] for row in review] == [["cluster_conflict"],
                                                       ["cluster_conflict"]]

    def test_membership_change_raises(self, alias_table, monkeypatch):
        ids, a, b = self._pair()
        self._rule(monkeypatch, ids[0], [ids[0], "f" * 16])
        with pytest.raises(ValueError, match="no longer covers it"):
            run_stage4([a, b], alias_table,
                       _embed_map({"Q one?": V_SAME, "Q two?": V_NEAR}),
                       None, "rule-based")

    def test_stale_ruling_raises_on_a_whole_corpus_run(self, alias_table,
                                                        monkeypatch):
        ids, a, b = self._pair()
        self._rule(monkeypatch, "e" * 16, ["e" * 16, "f" * 16])
        with pytest.raises(ValueError, match="stale Stage 4 cluster"):
            run_stage4([a, b], alias_table,
                       _embed_map({"Q one?": V_SAME, "Q two?": V_NEAR}),
                       None, "rule-based", strict_dispositions=True)

    def test_partial_run_tolerates_a_missing_ruled_cluster(self, alias_table,
                                                            monkeypatch):
        ids, a, b = self._pair()
        self._rule(monkeypatch, "e" * 16, ["e" * 16, "f" * 16])
        _, ws, _, stats = run_stage4(
            [a, b], alias_table,
            _embed_map({"Q one?": V_SAME, "Q two?": V_NEAR}),
            None, "rule-based", strict_dispositions=False)
        assert stats["n_dispositioned_clusters"] == 0

    def test_disposition_for_is_none_when_unruled(self):
        assert disposition_for("z" * 16, [_s2rec("z" * 16, "z.docx")]) is None

    def test_shipped_ruling_is_the_firststring_split(self):
        """Pins the owner ruling itself, not just the mechanism."""
        assert list(CURATED_CLUSTER_DISPOSITIONS) == ["79c663016afc2345"]
        entry = CURATED_CLUSTER_DISPOSITIONS["79c663016afc2345"]
        assert entry["disposition"] == DISPOSITION_SPLIT
        assert entry["members"] == ["79c663016afc2345", "8be45e86eb76c09f"]
        assert entry["members"] == sorted(entry["members"])
        assert entry["members"][0] == "79c663016afc2345"  # cluster_id = min id
        assert "2026-09-08" in entry["source"]


# --- summarize -------------------------------------------------------------------

class TestSummarize:
    def test_counts(self):
        recs = [
            {"stage4_status": "current", "currency_judgment": None,
             "stage4_review_reasons": [], "stage4_needs_review": False},
            {"stage4_status": "superseded_dup", "currency_judgment": None,
             "stage4_review_reasons": [], "stage4_needs_review": False},
            {"stage4_status": "superseded_currency",
             "currency_judgment": "dependent",
             "stage4_review_reasons": ["currency_low_confidence"],
             "stage4_needs_review": True},
        ]
        out = summarize(recs, [{"conflict": False, "audit": True,
                                "disposition": None},
                               {"conflict": True, "audit": False,
                                "disposition": DISPOSITION_SPLIT}])
        assert out["n_documents"] == 3
        assert out["n_current"] == 1
        assert out["by_stage4_status"]["superseded_dup"] == 1
        assert out["by_currency_judgment"] == {"dependent": 1}
        assert out["n_review_queue"] == 1
        assert out["n_audit_clusters"] == 1
        assert out["n_conflict_clusters"] == 1
        assert out["n_dispositioned_clusters"] == 1
