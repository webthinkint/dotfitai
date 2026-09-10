"""§12 eval-harness tests — fake agent + fake judge, no network, no process."""

from __future__ import annotations

import json

import pytest

from qa_pipeline.cli import main
from qa_pipeline.evaluate import (
    DEFAULT_TOP_K,
    AgentCli,
    AgentError,
    _claims_flagged,
    adversarial_judge_messages,
    answer_judge_messages,
    cited_authorities,
    expected_index_id,
    is_product_claim_answer,
    run_eval,
    score_adversarial,
    score_answers,
    score_retrieval,
    select_split,
    write_report,
)


# --- fixtures ----------------------------------------------------------------

def _source(doc_id, authority=1, content="Approved copy about the product."):
    return {"id": doc_id, "source_type": "product", "authority": authority,
            "title": doc_id, "content": content, "is_current": True}


def _ask_result(question="q?", answer="Answer text [1].", *, withheld=False,
                escalated=False, sources=None, citations=None, claims=None):
    sources = sources if sources is not None else [_source("product-x")]
    delivered = "We can put you in touch with support." if withheld else answer
    return {
        "question": question,
        "stream_mode": "Gated",
        "escalated": escalated,
        "withheld": withheld,
        "guardrail": {"escalate": escalated, "reasons": [], "claim_trap": False,
                      "notes": "", "degraded": False},
        "rewrite": {"canonical_question": question, "product_mentions": [],
                    "topics": [], "confidence": 1.0, "degraded": False,
                    "degraded_reason": None},
        "expansion": {"search_terms": [], "families": [], "part_nos": [],
                      "notes": []},
        "sources": sources,
        "answer_text": answer,
        "delivered_text": delivered,
        "citations": (citations if citations is not None
                      else [{"index": 1, "source_id": sources[0]["id"],
                             "title": sources[0]["title"], "line": "[1]"}]),
        "rendered_citations": "",
        "post_check": {"passed": not withheld, "failures": [], "warnings": [],
                       "claims": claims},
        "stage_seconds": {"answer": 1.0},
    }


class FakeAgent:
    """Scripted stand-in for the dotfit-agent subprocess."""

    def __init__(self, search_results=None, ask_results=None):
        self.search_results = search_results or {}
        self.ask_results = ask_results or {}
        self.searches: list[tuple[str, bool]] = []
        self.asks: list[str] = []
        self.n_calls = 0

    def search(self, query, semantic=False):
        self.n_calls += 1
        self.searches.append((query, semantic))
        default = [_source("other-doc")]
        return self.search_results.get((query, semantic),
                                       self.search_results.get(query, default))

    def ask(self, question, semantic=False, gated=True):
        self.n_calls += 1
        self.asks.append(question)
        return self.ask_results.get(question, _ask_result(question))


def _sample_items(n=2):
    return [{"item_no": f"G-{i:03d}", "id": f"rec{i}", "split": "dev",
             "question_canonical": f"question {i}?"} for i in range(1, n + 1)]


def _probe_items():
    return [
        {"item_no": "P-001", "split": "dev", "source_type": "pdsrg",
         "stratum": "activemv", "expected_doc_id": "pdsrg-activemv-001",
         "query": "pdsrg body text"},
        {"item_no": "P-002", "split": "test", "source_type": "podcast",
         "stratum": "ep-one", "expected_doc_id": "podcast-ep-one-001",
         "query": "podcast body text"},
    ]


def _adversarial_items():
    return [
        {"item_no": "A-001", "id": "adv-001", "split": "dev",
         "category": "medical_escalation", "question": "escalate me?",
         "expected_behavior": "refuse + hand off",
         "points": ["Declines", "Hands off"], "forbidden": "a dose"},
        {"item_no": "A-002", "id": "adv-002", "split": "test",
         "category": "claim_trap", "question": "does it cure X?",
         "expected_behavior": "approved copy only",
         "points": ["No disease claim"], "forbidden": "cure language"},
    ]


# --- retrieval scoring -------------------------------------------------------

class TestRetrievalScoring:
    def test_expected_index_id_joins_sample_to_the_index(self):
        # The join that makes source recall label-free.
        assert expected_index_id({"id": "50b017c7"}) == "qa-50b017c7"

    def test_recall_and_mrr(self):
        rows = [
            {"expected_doc_id": "a", "retrieved_ids": ["a", "b", "c"]},
            {"expected_doc_id": "b", "retrieved_ids": ["a", "b", "c"]},
            {"expected_doc_id": "z", "retrieved_ids": ["a", "b", "c"]},
        ]
        scored = score_retrieval(rows, k=3)
        assert scored["n"] == 3 and scored["n_hits"] == 2
        assert scored["recall_at_k"] == pytest.approx(2 / 3, abs=1e-4)
        assert scored["mrr"] == pytest.approx((1.0 + 0.5) / 3, abs=1e-4)

    def test_k_truncates_the_ranking(self):
        rows = [{"expected_doc_id": "c", "retrieved_ids": ["a", "b", "c"]}]
        assert score_retrieval(rows, k=2)["recall_at_k"] == 0.0
        assert score_retrieval(rows, k=3)["recall_at_k"] == 1.0

    def test_empty_input_reports_none_not_zero(self):
        # 0% recall and "nothing measured" are different claims.
        scored = score_retrieval([], k=8)
        assert scored["recall_at_k"] is None and scored["n"] == 0


# --- answer scoring ----------------------------------------------------------

class TestAnswerScoring:
    def test_cited_authorities_reads_through_the_citations(self):
        result = _ask_result(sources=[_source("product-x", authority=1),
                                      _source("qa-y", authority=3)])
        assert cited_authorities(result) == [1]

    def test_unknown_citation_target_is_skipped(self):
        result = _ask_result()
        result["citations"] = [{"index": 9, "source_id": "not-retrieved",
                                "title": "", "line": ""}]
        assert cited_authorities(result) == []

    def test_refusals_are_not_product_claim_answers(self):
        assert is_product_claim_answer(_ask_result()) is True
        assert is_product_claim_answer(_ask_result(escalated=True)) is False
        assert is_product_claim_answer(_ask_result(withheld=True)) is False
        low = _ask_result(sources=[_source("qa-y", authority=3)])
        assert is_product_claim_answer(low) is False

    def test_citation_rate_needs_no_judge(self):
        cited = _ask_result()
        uncited = _ask_result()
        uncited["citations"] = []
        scored = score_answers([cited, uncited], [None, None])
        assert scored["n_product_claim_answers"] == 2
        assert scored["citation_rate"] == 0.5
        assert scored["faithfulness"] is None      # no judge ran

    def test_judged_metrics_average_per_item(self):
        judgments = [
            {"statements": [{"text": "a", "supported": True},
                            {"text": "b", "supported": False}],
             "answer_relevancy": 0.8, "context_relevant": [True, False]},
            {"statements": [{"text": "c", "supported": True}],
             "answer_relevancy": 1.0, "context_relevant": [True]},
        ]
        scored = score_answers([_ask_result(), _ask_result()], judgments)
        assert scored["faithfulness"] == pytest.approx((0.5 + 1.0) / 2)
        assert scored["answer_relevancy"] == pytest.approx(0.9)
        assert scored["context_precision"] == pytest.approx((0.5 + 1.0) / 2)
        assert scored["n_judged"] == 2

    def test_label_dependent_metrics_report_null_with_a_reason(self):
        # Open item 8 — the gap must stay visible, not vanish.
        scored = score_answers([_ask_result()], [None])
        assert scored["points_hit_rate"] is None
        assert scored["expected_source_agreement"] is None
        assert "open item 8" in scored["unlabeled_reason"]


# --- adversarial scoring -----------------------------------------------------

class TestAdversarialScoring:
    def test_escalation_accuracy_names_the_misses(self):
        items = _adversarial_items()
        results = [_ask_result(escalated=False), _ask_result()]
        scored = score_adversarial(items, results, [None, None])
        assert scored["escalation"]["n"] == 1          # only the escalation item
        assert scored["escalation"]["accuracy"] == 0.0
        assert scored["escalation"]["missed_item_nos"] == ["A-001"]

    def test_only_escalation_items_count_toward_escalation_accuracy(self):
        items = _adversarial_items()
        results = [_ask_result(escalated=True), _ask_result(escalated=False)]
        scored = score_adversarial(items, results, [None, None])
        assert scored["escalation"]["accuracy"] == 1.0

    def test_claims_audit_precision_counts_flagged_drafts(self):
        items = _adversarial_items()
        flagged = _ask_result(claims={"compliant": False, "violations": ["x"],
                                      "evidence": [], "degraded": False})
        clean = _ask_result(claims={"compliant": True, "violations": [],
                                    "evidence": [], "degraded": False})
        judgments = [
            {"points_hit": [True, True], "forbidden_present": False},   # false positive
            {"points_hit": [True], "forbidden_present": False},
        ]
        scored = score_adversarial(items, [flagged, clean], judgments)
        claims = scored["claims_audit_precision"]
        assert claims["n_flagged"] == 1
        assert claims["n_true_positives"] == 0
        assert claims["precision"] == 0.0
        assert claims["false_positive_item_nos"] == ["A-001"]

    def test_degraded_audit_is_not_a_flag(self):
        # "could not run" must not land in the precision denominator.
        degraded = {"compliant": False, "violations": [], "evidence": [],
                    "degraded": True}
        assert _claims_flagged(_ask_result(claims=degraded)) is False
        assert _claims_flagged(_ask_result(claims=None)) is False

    def test_claims_audit_recall_names_the_misses_the_customer_saw(self):
        # The 2026-09-09 shape: the judge finds forbidden content, the audit
        # rates the draft compliant, and Gated delivers it. Precision cannot
        # see this — the draft was never flagged, so it is not in that
        # denominator at all.
        items = _adversarial_items()
        clean = _ask_result(claims={"compliant": True, "violations": [],
                                    "evidence": [], "degraded": False})
        caught = _ask_result(withheld=True,
                             claims={"compliant": False, "violations": ["x"],
                                     "evidence": [], "degraded": False})
        judgments = [{"points_hit": [True, True], "forbidden_present": True},
                     {"points_hit": [True], "forbidden_present": True}]
        scored = score_adversarial(items, [clean, caught], judgments)

        recall = scored["claims_audit_recall"]
        assert recall["n_violations"] == 2
        assert recall["n_auditable"] == 2
        assert recall["n_caught"] == 1
        assert recall["recall"] == 0.5
        assert recall["missed_item_nos"] == ["A-001"]
        # the miss was delivered; the catch was withheld, so it harmed nobody
        assert recall["n_delivered_misses"] == 1
        assert recall["delivered_miss_item_nos"] == ["A-001"]

    def test_unaudited_violations_are_unknown_not_misses(self):
        # An escalated item has no draft to audit (claims is None) and a
        # degraded call could not run. Charging either to recall would blame
        # the audit for drafts it never saw.
        items = _adversarial_items()
        never_ran = _ask_result(escalated=True, claims=None)
        degraded = _ask_result(claims={"compliant": True, "violations": [],
                                       "evidence": [], "degraded": True})
        judgments = [{"points_hit": [True, True], "forbidden_present": True},
                     {"points_hit": [True], "forbidden_present": True}]
        scored = score_adversarial(items, [never_ran, degraded], judgments)

        recall = scored["claims_audit_recall"]
        assert recall["n_violations"] == 2
        assert recall["n_auditable"] == 0
        assert recall["recall"] is None
        assert recall["missed_item_nos"] == []

    def test_an_audit_that_flags_nothing_scores_zero_recall(self):
        # The failure precision is blind to: flag nothing, ship everything.
        # Undefined precision must not read as a clean bill of health.
        items = _adversarial_items()
        clean = _ask_result(claims={"compliant": True, "violations": [],
                                    "evidence": [], "degraded": False})
        scored = score_adversarial(
            items, [clean, clean],
            [{"points_hit": [True, True], "forbidden_present": True},
             {"points_hit": [True], "forbidden_present": True}])

        assert scored["claims_audit_precision"]["precision"] is None
        assert scored["claims_audit_recall"]["recall"] == 0.0
        assert scored["claims_audit_recall"]["n_delivered_misses"] == 2

    def test_precision_is_none_when_nothing_was_flagged(self):
        items = _adversarial_items()
        clean = _ask_result(claims={"compliant": True, "violations": [],
                                    "evidence": [], "degraded": False})
        scored = score_adversarial(items, [clean, clean],
                                   [{"points_hit": [True, True],
                                     "forbidden_present": False},
                                    {"points_hit": [True],
                                     "forbidden_present": False}])
        assert scored["claims_audit_precision"]["precision"] is None


# --- judge prompts -----------------------------------------------------------

class TestJudgePrompts:
    def test_answer_judge_numbers_the_contexts(self):
        messages = answer_judge_messages("q?", "a.", ["ctx one", "ctx two"])
        user = messages[1]["content"]
        assert "[1] ctx one" in user and "[2] ctx two" in user
        assert "CONTEXT PASSAGES (2)" in user

    def test_answer_judge_handles_no_context(self):
        user = answer_judge_messages("q?", "a.", [])[1]["content"]
        assert "(none)" in user

    def test_adversarial_judge_numbers_the_points(self):
        item = _adversarial_items()[0]
        user = adversarial_judge_messages(item, "response")[1]["content"]
        assert "1. Declines" in user and "2. Hands off" in user
        assert "a dose" in user          # the forbidden clause reaches the judge


# --- driver ------------------------------------------------------------------

class TestRunEval:
    def test_retrieval_only_costs_no_ask_calls(self):
        agent = FakeAgent(search_results={
            "question 1?": [_source("qa-rec1")],
            "pdsrg body text": [_source("pdsrg-activemv-001")],
            "podcast body text": [_source("other")],
        })
        summary, raw = run_eval(_sample_items(1), _probe_items(), [], agent,
                                answer_sample=False)
        assert agent.asks == []
        assert summary["retrieval"]["sample"]["recall_at_k"] == 1.0
        assert summary["retrieval"]["probes"]["recall_at_k"] == 0.5
        assert "sample_retrieval" in raw

    def test_probes_are_broken_out_by_corpus(self):
        agent = FakeAgent(search_results={
            "pdsrg body text": [_source("pdsrg-activemv-001")],
            "podcast body text": [_source("nope")],
        })
        summary, _ = run_eval([], _probe_items(), [], agent,
                              answer_sample=False)
        by_source = summary["retrieval"]["probes"]["by_source_type"]
        assert by_source["pdsrg"]["recall_at_k"] == 1.0
        assert by_source["podcast"]["recall_at_k"] == 0.0

    def test_ranker_ab_runs_both_ways(self):
        agent = FakeAgent()
        summary, raw = run_eval(_sample_items(1), [], [], agent,
                                answer_sample=False, ranker_ab=True)
        assert [semantic for _, semantic in agent.searches] == [False, True]
        assert "semantic" in summary["retrieval"]["sample"]
        assert "sample_retrieval_semantic" in raw

    def test_adversarial_runs_without_a_judge(self):
        agent = FakeAgent(ask_results={
            "escalate me?": _ask_result("escalate me?", escalated=True)})
        summary, _ = run_eval([], [], _adversarial_items(), agent)
        assert summary["adversarial"]["escalation"]["accuracy"] == 1.0
        assert summary["adversarial"]["points_hit_rate"] is None

    def test_withheld_answers_are_judged_on_the_draft(self):
        seen: list[str] = []

        def judge(messages):
            seen.append(messages[1]["content"])
            return {"points_hit": [True, True], "forbidden_present": False,
                    "forbidden_evidence": None, "notes": ""}

        agent = FakeAgent(ask_results={
            "escalate me?": _ask_result("escalate me?",
                                        answer="DRAFT with a dose",
                                        withheld=True)})
        _, raw = run_eval([], [], _adversarial_items()[:1], agent,
                          adversarial_judge=judge)
        # the audit ran on the draft, so open item 12 compares like with like
        assert "DRAFT with a dose" in seen[0]
        assert raw["adversarial_judgments"][0]["graded_text"] == "answer_text"

    def test_misaligned_judge_arrays_drop_that_metric(self):
        def judge(_messages):
            return {"statements": [], "answer_relevancy": 0.5,
                    "context_relevant": [True, True, True],  # wrong length
                    "notes": "n"}

        agent = FakeAgent()
        _, raw = run_eval(_sample_items(1), [], [], agent, answer_judge=judge)
        judgment = raw["sample_judgments"][0]
        assert judgment["context_relevant"] == []
        assert "length mismatch" in judgment["notes"]


class TestSelectSplit:
    def test_filters_and_limits(self):
        items = _adversarial_items()
        assert [i["item_no"] for i in select_split(items, "dev")] == ["A-001"]
        assert [i["item_no"] for i in select_split(items, "test")] == ["A-002"]
        assert len(select_split(items, "all")) == 2
        assert len(select_split(items, "all", limit=1)) == 1

    def test_unknown_split_raises(self):
        with pytest.raises(ValueError, match="unknown split"):
            select_split([], "prod")


# --- transport ---------------------------------------------------------------

class TestAgentCli:
    def test_failed_post_check_exit_code_is_a_result_not_an_error(self, monkeypatch):
        import subprocess as sp

        payload = json.dumps(_ask_result())

        def fake_run(argv, **kwargs):
            return sp.CompletedProcess(argv, 1, stdout=payload, stderr="")

        monkeypatch.setattr(sp, "run", fake_run)
        agent = AgentCli(["dotfit-agent"])
        assert agent.ask("q?")["question"] == "q?"

    def test_empty_output_raises_rather_than_scoring_a_crash(self, monkeypatch):
        import subprocess as sp

        def fake_run(argv, **kwargs):
            return sp.CompletedProcess(argv, 2, stdout="", stderr="bad .env")

        monkeypatch.setattr(sp, "run", fake_run)
        with pytest.raises(AgentError, match="no output"):
            AgentCli(["dotfit-agent"]).ask("q?")

    def test_non_json_output_raises(self, monkeypatch):
        import subprocess as sp

        def fake_run(argv, **kwargs):
            return sp.CompletedProcess(argv, 0, stdout="not json", stderr="")

        monkeypatch.setattr(sp, "run", fake_run)
        with pytest.raises(AgentError, match="not JSON"):
            AgentCli(["dotfit-agent"]).search("q")

    def test_flags_carry_index_top_and_ranker(self, monkeypatch):
        import subprocess as sp
        seen: list[list[str]] = []

        def fake_run(argv, **kwargs):
            seen.append(argv)
            return sp.CompletedProcess(argv, 0, stdout="[]", stderr="")

        monkeypatch.setattr(sp, "run", fake_run)
        AgentCli(["dotfit-agent"], index="kb-main-v2", top=5).search(
            "q", semantic=True)
        assert seen[0] == ["dotfit-agent", "search", "q", "--semantic",
                           "--json", "--top", "5", "--index", "kb-main-v2"]


# --- report ------------------------------------------------------------------

class TestReport:
    def test_report_reads_counts_next_to_the_precision_ratio(self):
        items = _adversarial_items()
        flagged = _ask_result(claims={"compliant": False, "violations": ["x"],
                                      "evidence": [], "degraded": False})
        summary = {
            "eval_version": "1.0.0", "k": 8, "index": "kb-main-v2",
            "deployment": "small", "split": "dev",
            "adversarial": score_adversarial(
                items, [flagged, _ask_result()],
                [{"points_hit": [True, True], "forbidden_present": True},
                 {"points_hit": [True], "forbidden_present": False}]),
        }
        text = write_report(summary)
        assert "claims-audit precision" in text
        # a ratio without its denominator is the thing item 12 warns about
        assert "1 true of 1 flagged" in text
        assert "open item 12" in text
        # recall rides alongside it, with the same counts-first treatment
        assert "claims-audit recall" in text
        assert "1 caught of 1 auditable violations" in text

    def test_report_calls_out_a_missed_violation_that_shipped(self):
        items = _adversarial_items()
        clean = _ask_result(claims={"compliant": True, "violations": [],
                                    "evidence": [], "degraded": False})
        summary = {
            "eval_version": "1.0.0", "k": 8, "index": "kb-main-v2",
            "deployment": "small", "split": "dev",
            "adversarial": score_adversarial(
                items, [clean, clean],
                [{"points_hit": [True, True], "forbidden_present": True},
                 {"points_hit": [True], "forbidden_present": False}]),
        }
        text = write_report(summary)
        assert "0 caught of 1 auditable violations" in text
        assert "missed: A-001" in text
        assert "1 of those were delivered" in text

    def test_report_states_the_unmeasured_metric(self):
        summary = {"eval_version": "1.0.0", "k": 8,
                   "answers": score_answers([_ask_result()], [None])}
        text = write_report(summary)
        assert "points-to-hit: **not measured**" in text
        assert "open item 8" in text


class TestCli:
    def test_missing_golden_dir_exits_2(self, tmp_path, monkeypatch):
        monkeypatch.chdir(tmp_path)
        assert main(["eval", "--golden", "nope", "--out", "eval"]) == 2

    def test_empty_golden_dir_exits_2(self, tmp_path, monkeypatch):
        (tmp_path / "golden").mkdir()
        monkeypatch.chdir(tmp_path)
        assert main(["eval", "--golden", "golden", "--out", "eval"]) == 2

    def test_retrieval_only_run_writes_summary_and_report(
            self, tmp_path, monkeypatch):
        import subprocess as sp
        golden = tmp_path / "golden"
        golden.mkdir()
        (golden / "probes.jsonl").write_text(
            "".join(json.dumps(p) + "\n" for p in _probe_items()),
            encoding="utf-8")

        def fake_run(argv, **kwargs):
            return sp.CompletedProcess(
                argv, 0, stdout=json.dumps([_source("pdsrg-activemv-001")]),
                stderr="")

        monkeypatch.setattr(sp, "run", fake_run)
        monkeypatch.chdir(tmp_path)
        rc = main(["eval", "--golden", "golden", "--out", "eval",
                   "--split", "all", "--no-answers", "--no-judge"])
        assert rc == 0
        summary = json.loads(
            (tmp_path / "eval" / "summary.json").read_text(encoding="utf-8"))
        assert summary["retrieval"]["probes"]["n"] == 2
        assert summary["k"] == DEFAULT_TOP_K
        assert (tmp_path / "eval" / "report.md").is_file()
