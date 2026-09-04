"""Shared fixtures: synthetic .docx builders.

PII in fixtures is fabricated (example.com, 555 numbers) — real corpus data
never enters the repo.
"""

from __future__ import annotations

import pytest
from docx import Document
from pathlib import Path


def build_docx(path: Path, paragraphs: list[str]) -> Path:
    doc = Document()
    for text in paragraphs:
        doc.add_paragraph(text)
    path.parent.mkdir(parents=True, exist_ok=True)
    doc.save(path)
    return path


EMAIL_THREAD_PARAS = [
    "Hi Jane,",
    "Thanks for contacting us. Take 1-2 softgels daily with a meal.",
    "Call us at 877-555-3418 with any questions, or write to help@example.org.",
    "",
    "Alice Expert",
    "CEO",
    "Office. 555.123.4567",
    "Fax.     555.987.6543",
    "Disclaimer: Internet communications are not secure.",
    # real corpus: header block = ONE paragraph with soft line breaks
    "From: support@dotfit.com <support@dotfit.com> \n"
    "Sent: Sunday, February 26, 2023 5:08 AM\n"
    "To: Experts <experts@dotfit.com>\n"
    "Subject: dotFIT - Ask the Experts",
    # real corpus: contact block likewise one paragraph
    "From: Jane Q. Customer\n"
    "Email: JANE.CUSTOMER@EXAMPLE.COM\n"
    "Phone: (310) 555-0192\n"
    "Category: General\n"
    "Question: How many fish oil capsules should I take daily? "
    "You can also reach me at jane.customer@example.com.",
    "",
    "Copyright 2008-2023 dotFIT All rights reserved.",
]

EXPERT_NOTE_PARAS = [
    "Practitioner Product Status by Rules",
    "Efficacy – dosages & forms match clinical trials",
    "Safety – shown in trials and history",
    "Third-party testing: https://example-lab.org/report-2023",
]


@pytest.fixture
def email_thread_docx(tmp_path: Path) -> Path:
    return build_docx(tmp_path / "QAs" / "2023" / "Fish oil" / "thread.docx", EMAIL_THREAD_PARAS)


@pytest.fixture
def expert_note_docx(tmp_path: Path) -> Path:
    return build_docx(tmp_path / "QAs" / "2024" / "note.docx", EXPERT_NOTE_PARAS)
