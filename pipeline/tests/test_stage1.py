"""Stage 1 parse & classify tests."""

from __future__ import annotations

from qa_pipeline.extract import extract_docx
from qa_pipeline.scrub import scrub_extracted
from qa_pipeline.stage1 import (
    classify_and_parse, parse_sent_date, review_reasons,
)

import locale

import pytest


def _process(paras, tmp_path, name):
    from conftest import build_docx
    path = build_docx(tmp_path / name, paras)
    extraction = extract_docx(path)
    text, rep = scrub_extracted(extraction.lines)
    lines = text.split("\n")
    rel = "2023/" + name.replace("\\", "/")
    return classify_and_parse(lines, rel, scrub_report=rep.to_dict())


class TestSentDate:
    def test_outlook_long_format(self):
        assert parse_sent_date("Sunday, February 26, 2023 5:08 AM") == "2023-02-26"

    def test_us_short(self):
        assert parse_sent_date("3/5/24 2:14 PM") == "2024-03-05"

    def test_iso(self):
        assert parse_sent_date("2025-11-02 09:14") == "2025-11-02"

    def test_garbage(self):
        assert parse_sent_date("whenever") is None


class TestClassify:
    def test_email_thread(self, email_thread_docx, tmp_path):
        paras = (email_thread_docx.parent.parent / "x").exists()  # noop guard
        from conftest import EMAIL_THREAD_PARAS
        rec = _process(EMAIL_THREAD_PARAS, tmp_path, "Fish oil/thread.docx")

        assert rec["doc_type"] == "qa_email"
        assert rec["year"] == 2023
        assert rec["topic_subfolder"] == "Fish oil"
        assert rec["thread_date"] == "2023-02-26"
        assert rec["question"].startswith("How many fish oil capsules")
        assert "Jane Q. Customer" not in rec["customer_section"]
        assert "[CUSTOMER]" in rec["customer_section"]
        assert "Take 1-2 softgels" in rec["expert_section"]
        assert "Copyright" not in rec["expert_section"] + rec["customer_section"]

    def test_expert_note(self, expert_note_docx, tmp_path):
        from conftest import EXPERT_NOTE_PARAS
        rec = _process(EXPERT_NOTE_PARAS, tmp_path, "note.docx")
        assert rec["doc_type"] == "expert_note"
        assert rec["question"] is None
        assert rec["thread_date"] is None
        assert not rec["needs_review"]

    def test_mixed_markers_goes_to_review(self, tmp_path):
        paras = [
            "Some internal note about policies.",
            "From: something without a proper header block",
            "just a line mentioning From: inline",
        ]
        rec = _process(paras, tmp_path, "mixed.docx")
        # 'From:' as a labeled line without Sent/To/contact-followers is 'other'
        assert rec["doc_type"] in {"other", "expert_note"}
        if rec["doc_type"] == "other":
            assert rec["needs_review"]

    def test_residual_pii_flag_propagates(self, tmp_path):
        paras = [
            "Hi John, thanks — as Dr. Smith noted, take with food.",
        ]
        rec = _process(paras, tmp_path, "residual.docx")
        # Dr. Smith -> honorific_plus_name flag -> needs_review
        # (greeting 'Hi John,' is at index 0 as first non-blank line -> also replaced)
        assert rec["residual_pii_flag"] is True
        assert rec["needs_review"] is True


class TestQaNotes:
    """Free-form Question:/Answer: notes (no email thread) classify as qa_email."""

    def test_question_answer_note(self, tmp_path):
        paras = [
            "Why product containers are not filled all the way",
            "Question: Someone asked why we do not fill our containers all the way.",
            "Do we have a response to let them know why?",
            "Answer: In a powdered product, there is empty space between the",
            "top of the powder and the bottom of the lid. The term is slack-fill.",
        ]
        rec = _process(paras, tmp_path, "slackfill.docx")
        assert rec["doc_type"] == "qa_email"
        assert rec["needs_review"] is False
        assert rec["question"].startswith("Someone asked why we do not fill")
        assert "slack-fill" in rec["expert_section"]
        assert "Question:" in rec["customer_section"]

    def test_summary_answer_label(self, tmp_path):
        paras = [
            "Question: Is there a difference in servings between the two?",
            "Summary: The serving counts differ because of serving weights.",
        ]
        rec = _process(paras, tmp_path, "servings.docx")
        assert rec["doc_type"] == "qa_email"
        assert rec["needs_review"] is False
        assert rec["question"].startswith("Is there a difference in servings")

    def test_message_only_note_answer_inline(self, tmp_path):
        paras = [
            "Gelatin source note",
            "Message: What kind of gelatin is in this product?",
            "The gelatin is derived from a bovine source.",
        ]
        rec = _process(paras, tmp_path, "gelatin.docx")
        assert rec["doc_type"] == "qa_email"
        assert rec["needs_review"] is False
        assert rec["question"].startswith("What kind of gelatin")
        assert "bovine source" in rec["expert_section"]

    def test_no_question_label_stays_expert_note(self, tmp_path):
        paras = [
            "Some internal note about creatine forms.",
            "A plain paragraph with no labeled lines at all.",
        ]
        rec = _process(paras, tmp_path, "plain.docx")
        assert rec["doc_type"] == "expert_note"


FORWARDED_PARAS = [
    # thread header on line 0 -> the reply is written BELOW the contact block
    "From: support@example.org\n"
    "Sent: Tuesday, June 27, 2023 12:10 PM\n"
    "To: Experts <experts@example.org>\n"
    "Subject: Ask the Experts",
    "",
    "From: Jane Q. Customer\n"
    "Email: jane@example.org\n"
    "Category: Supplements\n"
    "Question: Does the lemonade flavor differ from last year?",
    "",
    "Hi Mia,",
    "Thanks for contacting us. The flavor was altered, not the formula.",
    "",
    "Question: an older quoted thread that must not win",
]


class TestForwardedThread:
    """Header-at-line-0 documents put the answer under the quoted enquiry."""

    def test_answer_is_not_swallowed_by_the_customer_section(self, tmp_path):
        rec = _process(FORWARDED_PARAS, tmp_path, "forwarded.docx")
        assert rec["doc_type"] == "qa_email"
        assert "The flavor was altered" in rec["expert_section"]
        assert "The flavor was altered" not in rec["customer_section"]
        assert rec["needs_review"] is False

    def test_question_stops_at_the_blank_line(self, tmp_path):
        """The reply used to be concatenated onto the question text."""
        rec = _process(FORWARDED_PARAS, tmp_path, "forwarded.docx")
        assert rec["question"] == "Does the lemonade flavor differ from last year?"
        assert "Thanks for contacting us" not in rec["question"]


class TestEmptyAnswerGuard:
    """No-answer docs are excluded by the CLI (owner disposition
    2026-09-02), not queued — the record still marks itself needs_review so
    the exclusion is auditable per document."""

    def test_qa_email_without_an_answer_marks_needs_review(self):
        rec = classify_and_parse(
            ["From: [CUSTOMER]", "Email: [EMAIL]", "Question: anything?"],
            "2023/stub.docx", scrub_report={})
        assert rec["doc_type"] == "qa_email"
        assert rec["expert_section"] == ""
        assert rec["needs_review"] is True
        assert "no_expert_answer" not in review_reasons(rec)

    def test_empty_document_marks_needs_review(self):
        rec = classify_and_parse(["", "  "], "2023/blank.docx", scrub_report={})
        assert rec["needs_review"] is True
        assert "empty_document" not in review_reasons(rec)


class TestDateLocaleIndependence:
    """%A/%B/%p resolve through LC_TIME; the parser must not depend on it."""

    @pytest.fixture
    def german_time_locale(self):
        previous = locale.setlocale(locale.LC_TIME)
        for name in ("German_Germany", "de_DE.UTF-8", "de_DE"):
            try:
                locale.setlocale(locale.LC_TIME, name)
                break
            except locale.Error:
                continue
        else:
            pytest.skip("no German locale available on this host")
        yield
        locale.setlocale(locale.LC_TIME, previous)

    def test_english_month_names_parse_under_a_german_locale(self, german_time_locale):
        assert parse_sent_date("Sunday, February 26, 2023 5:08 AM") == "2023-02-26"
        assert parse_sent_date("3/5/24 2:14 PM") == "2024-03-05"

    def test_invalid_calendar_dates_are_rejected(self):
        assert parse_sent_date("2/30/2023") is None
        assert parse_sent_date("Febtember 3, 2024") is None
