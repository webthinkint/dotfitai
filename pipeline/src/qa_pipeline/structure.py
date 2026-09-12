"""Shared structural markers for QA corpus documents.

Two email shapes exist (see docs/v1/phase1-knowledge-assistant.md §4):

(a) saved email threads — expert answer on top, quoted customer message below,
    delimited by a thread header (``From:/Sent:/To:/Subject:``) and a contact
    block (``From:/Email:/Category:/Question:`` — labels vary: Name, Phone,
    Message …)
(b) free-form expert notes — no markers at all
(c) free-form Q&A notes — ``Question:`` … ``Answer:``/``Summary:`` labels with
    no email thread at all (or a single labeled question with the answer
    inline); classified as qa_email in Stage 1

Both Stage 0 (signature cutting needs to know where the quoted section starts)
and Stage 1 (section split + classification) need to locate the customer
header, so the detection lives here and is used by both.
"""

from __future__ import annotations

import re

# Labeled lines: thread headers + contact block fields (label set kept generous,
# corpus-observed: From/Email/Category/Question, plan-documented: Name/Phone/Message)
LABEL_RE = re.compile(
    r"^\s*(From|To|Cc|Bcc|Sent|Date|Subject|Name|Email|E-mail|Phone|Mobile|Cell|"
    r"Category|Question|Message|Comments)\s*:\s*(.*)$",
    re.IGNORECASE,
)
LABELS = {m.lower() for m in (
    "From", "To", "Cc", "Bcc", "Sent", "Date", "Subject", "Name", "Email",
    "E-mail", "Phone", "Mobile", "Cell", "Category", "Question", "Message",
    "Comments",
)}


def label_of(line: str) -> tuple[str, str] | None:
    """Return (label_lower, value) if *line* is a labeled header/contact line."""
    m = LABEL_RE.match(line)
    if not m:
        return None
    return m.group(1).lower(), m.group(2).strip()


def _is_thread_header(lines: list[str], i: int) -> bool:
    """``From:`` at *i* followed shortly by Sent/To/Subject = quoted thread header."""
    for j in range(i + 1, min(i + 5, len(lines))):
        lab = label_of(lines[j])
        if lab and lab[0] in {"sent", "to", "subject"}:
            return True
        if lines[j].strip() == "":
            continue
    return False


def _is_contact_block(lines: list[str], i: int) -> bool:
    """A labeled line at *i* that starts a Name/From/Email contact block."""
    lab = label_of(lines[i])
    if lab is None:
        return False
    if lab[0] not in {"from", "name", "email"}:
        return False
    for j in range(i + 1, min(i + 4, len(lines))):
        nxt = label_of(lines[j])
        if nxt and nxt[0] in {"email", "name", "phone", "question", "message",
                              "category", "mobile", "cell", "comments", "e-mail"}:
            return True
    return False


def find_customer_header(lines: list[str]) -> int | None:
    """Index of the first line that begins the quoted customer section.

    Works on raw OR scrubbed text (Stage 0 replaces values with placeholders
    but keeps the labels, so detection survives scrubbing).
    """
    for i, line in enumerate(lines):
        lab = label_of(line)
        if lab is None:
            continue
        if lab[0] in {"from", "name"} and (
            _is_thread_header(lines, i) or _is_contact_block(lines, i)
        ):
            return i
        if lab[0] == "sent":  # some exports have Sent: without a From: line
            return i
    return None


def has_email_markers(lines: list[str]) -> bool:
    """Any thread/contact marker anywhere (used to detect 'mixed' documents)."""
    return any(label_of(line) is not None for line in lines)


# Free-form Question:/Answer: notes (no email thread at all — internal FAQ
# notes written directly in question/answer form). The question side reuses
# the generic label vocabulary; the answer side has its own label set because
# Answer/Summary/Response are not email/contact labels.
QA_QUESTION_LABELS = frozenset({"question", "message", "comments"})
QA_ANSWER_RE = re.compile(r"^\s*(?:Answer|Summary|Response)\s*:\s*", re.IGNORECASE)


def find_qa_split(lines: list[str]) -> tuple[int, int] | None:
    """(question_idx, answer_idx) for free-form 'Question: … / Answer: …' notes.

    The first question-labeled line starts the question section; the first
    Answer/Summary/Response label after it starts the expert answer. None if
    the document has no such pair.
    """
    q_idx = None
    for i, line in enumerate(lines):
        lab = label_of(line)
        if lab and lab[0] in QA_QUESTION_LABELS:
            if q_idx is None:
                q_idx = i
        elif q_idx is not None and QA_ANSWER_RE.match(line):
            return q_idx, i
    return None


def find_forwarded_split(lines: list[str]) -> tuple[int, int] | None:
    """(question_idx, answer_idx) for documents whose thread header is line 0.

    Some saved threads open with their own ``From:/Sent:/To:`` header, so the
    expert's reply is written *below* the quoted contact block rather than
    above it. The quoted enquiry ends at the first question-labeled line; the
    reply starts after the blank line that follows it.

    Returns None when there is no question label or nothing after it.
    """
    q_idx = None
    for i, line in enumerate(lines):
        lab = label_of(line)
        if lab and lab[0] in QA_QUESTION_LABELS:
            q_idx = i       # FIRST label: the enquiry being answered. A later
            break           # one belongs to a quoted earlier thread.
    if q_idx is None:
        return None
    for j in range(q_idx + 1, len(lines)):
        if lines[j].strip():
            continue
        for k in range(j + 1, len(lines)):  # first content after the blank
            if lines[k].strip():
                return q_idx, k
        return None
    return None


def find_question_label(lines: list[str]) -> int | None:
    """Index of the first question-labeled line, or None.

    Used for message-only Q&A notes (a labeled question with the answer
    inline) — pair with a check that no other label kinds appear.
    """
    for i, line in enumerate(lines):
        lab = label_of(line)
        if lab and lab[0] in QA_QUESTION_LABELS:
            return i
    return None
