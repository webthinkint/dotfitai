"""The weekly refresh's decisions: what to download, what publishing changes, and
what mail may carry. Dropbox, git and SMTP are not exercised here."""

from __future__ import annotations

import hashlib
from pathlib import Path

import pytest

from qa_pipeline.weekly import (
    CONTENT_HASH_BLOCK, MAX_RETIRED, Changes, RemoteFile, alerts, content_hash,
    diff_records, pii_flag_is_unserved, plan_fetch, render_report, safe_rel,
    split_unserved_pii, summary_lines,
)


def remote(rel: str, digest: str) -> RemoteFile:
    return RemoteFile(rel, digest, f"id:{rel}")


def record(rid: str, *, current: bool = True, answer: str = "Take it with a meal.") -> dict:
    return {"id": rid, "source_file": f"2026/{rid}.docx", "filename": rid,
            "question_canonical": f"Question {rid}?", "answer": answer,
            "is_current": current}


def test_content_hash_is_the_hash_of_the_block_hashes(tmp_path: Path) -> None:
    path = tmp_path / "f.bin"
    first, second = b"a" * CONTENT_HASH_BLOCK, b"b" * 10
    path.write_bytes(first + second)
    expected = hashlib.sha256(
        hashlib.sha256(first).digest() + hashlib.sha256(second).digest()).hexdigest()
    assert content_hash(path) == expected


def test_plan_sorts_files_into_new_changed_and_unchanged() -> None:
    plan = plan_fetch(
        [remote("2026/same.docx", "h1"), remote("2026/edited.docx", "h2-new"),
         remote("2026/fresh.docx", "h3")],
        {"2026/same.docx": "h1", "2026/edited.docx": "h2"})
    assert [f.rel for f in plan.new] == ["2026/fresh.docx"]
    assert [f.rel for f in plan.changed] == ["2026/edited.docx"]
    assert plan.copies_skipped == 0


def test_plan_skips_content_already_held_at_another_path() -> None:
    plan = plan_fetch(
        [remote("2026/a/copy.docx", "h1"), remote("2026/b.docx", "h9"),
         remote("2026/c.docx", "h9")],
        {"2025/original.docx": "h1"})
    assert [f.rel for f in plan.new] == ["2026/b.docx"]
    assert plan.copies_skipped == 2


def test_plan_keeps_the_local_path_when_only_case_differs() -> None:
    plan = plan_fetch([remote("2026/Topic/A.docx", "h2")], {"2026/topic/a.docx": "h1"})
    assert [f.rel for f in plan.changed] == ["2026/topic/a.docx"]
    assert not plan.new


def test_plan_never_removes_a_local_file_missing_from_dropbox() -> None:
    plan = plan_fetch([], {"2026/old.docx": "h1"})
    assert not plan.new and not plan.changed


@pytest.mark.parametrize("rel", ["../escape.docx", "a/../../escape.docx", "/abs.docx", ""])
def test_safe_rel_rejects_paths_that_leave_the_folder(rel: str) -> None:
    with pytest.raises(ValueError):
        safe_rel("2026", rel)


def test_diff_finds_what_customers_would_see_change() -> None:
    before = [record("same"), record("edited"), record("retiring"), record("gone"),
              record("back", current=False), record("still-out", current=False)]
    after = [record("same"), record("edited", answer="Take it before a workout."),
             record("retiring", current=False), record("back"),
             record("still-out", current=False, answer="Reworded."),
             record("fresh"), record("fresh-dup", current=False)]
    changes = diff_records(before, after)
    assert [r["id"] for r in changes.new] == ["fresh"]
    assert changes.new_not_served == 1
    assert [r["id"] for r in changes.changed] == ["edited"]
    assert sorted(r["id"] for r in changes.retired) == ["gone", "retiring"]
    assert [r["id"] for r in changes.returned] == ["back"]


def test_no_differences_is_nothing_to_publish() -> None:
    assert not diff_records([record("a")], [record("a")]).any()


def test_mail_lines_carry_no_record_text() -> None:
    secret = "Synthetic customer sentence"
    rec = {**record("fresh", answer=secret), "question_canonical": secret, "filename": secret}
    changes = Changes(new=[rec], changed=[rec], retired=[rec], returned=[rec])
    reasons = {rec["source_file"]: ["residual_pii_flag"]}
    fetched = {"new": [rec["source_file"]], "changed": [], "copies_skipped": 1}
    mail = "\n".join(alerts(changes, fetched) + summary_lines(changes, reasons, fetched))
    assert secret not in mail and rec["source_file"] not in mail
    assert "Flagged to read: 2" in mail


def test_report_leads_with_flagged_records_and_their_evidence(tmp_path: Path) -> None:
    flagged, plain = record("flagged"), record("plain")
    report = render_report(
        Changes(new=[flagged, plain]), {flagged["source_file"]: ["residual_pii_flag"]},
        {flagged["source_file"]: "Jane Doe"}, {"new": ["2026/flagged.docx"]},
        tmp_path / "skip-thursday", "2026-11-09")
    assert report.index("## Flagged") < report.index("## New, not flagged")
    assert "The model flagged: Jane Doe" in report
    assert str(tmp_path / "skip-thursday") in report


def test_a_pii_flag_leaves_the_reading_list_only_when_provably_unserved(tmp_path: Path) -> None:
    source_text = "Hi, this is Jane Doe asking about creatine."
    rec = record("a")
    assert pii_flag_is_unserved("Jane Doe", rec, source_text)
    # the path counts as source: a name only in the raw filename is not served
    assert pii_flag_is_unserved("Jane", {**rec, "source_file": "2026/Jane question.docx"}, "")
    # one word in served text may be a reworded name, so it stays flagged
    assert not pii_flag_is_unserved("Jane Doe", record("b", answer="Ask Jane first."), source_text)
    # a span the source does not contain may be a paraphrase, so it stays flagged
    assert not pii_flag_is_unserved("the customer's employer", rec, source_text)
    assert not pii_flag_is_unserved("J.", rec, "J. asked.")

    text = tmp_path / "2026" / "a.txt"
    text.parent.mkdir()
    text.write_text(source_text, encoding="utf-8")
    source = rec["source_file"]
    kept, dropped = split_unserved_pii(
        [rec], {source: ["low_confidence", "residual_pii_flag"]}, {source: "Jane Doe"}, tmp_path)
    assert kept[source] == ["low_confidence"]
    assert dropped == 1


def test_alerts_name_a_quiet_week_a_new_folder_and_too_many_retires() -> None:
    retiring = Changes(retired=[record(str(n)) for n in range(MAX_RETIRED + 1)])
    text = "\n".join(alerts(retiring, {"unconfigured_folders": ["2027 FP Q&A"]}))
    assert "No new files" in text
    assert "2027 FP Q&A" in text
    assert "Thursday will stop" in text
    assert alerts(Changes(), {"new": ["2026/a.docx"]}) == []


def test_files_with_no_record_are_named_in_the_report_and_only_counted_in_mail(
        tmp_path: Path) -> None:
    fetched = {"new": ["2026/unreadable.docx"], "no_record": ["2026/unreadable.docx"]}
    report = render_report(Changes(), {}, {}, fetched, tmp_path / "skip", "2026-11-09")
    assert "`2026/unreadable.docx`" in report
    mail = "\n".join(summary_lines(Changes(), {}, fetched))
    assert "New files with no record: 1" in mail
    assert "unreadable" not in mail
