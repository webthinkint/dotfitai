"""Stage 0 — deterministic PII scrub (plan §4, Stage 0).

Runs on raw .docx files, locally. No LLM, no network. Produces:

- scrubbed plain text (one paragraph/table-row per line)
- a per-file JSON report (what was redacted, what was dropped, flags)

Guarantees:
- nothing unscrubbed is written to the output tree
- files that fail to parse are reported as errors (manual queue), never crash
- output is deterministic: same input bytes -> identical output bytes
- redaction is conservative: what cannot be redacted safely is *flagged* for
  human review (``residual_pii_flag``) rather than guessed at

Placeholder vocabulary (kept stable — Stage 1 and reviewers depend on it):
  [EMAIL] [PHONE] [SSN] [CARD] [ADDRESS] [PROFILE-URL] [CUSTOMER] [NAME]
  [SIGNATURE]

``[CUSTOMER]`` marks the enquirer (contact-block identity, the expert's
opening greeting). ``[NAME]`` marks a person whose role we cannot assert —
mail recipients, greetings inside quoted replies — and must not be read as
"this was a customer".
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field

from .extract import ExtractResult
from .structure import find_customer_header, label_of

# ---------------------------------------------------------------- patterns

EMAIL_RE = re.compile(r"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+")

# US/CA phone numbers with separators: 877-436-8348, 805.435.1414,
# (805) 435-1414, +1 805 435 1414 — deliberately NOT matching bare 10-digit
# runs (order/dosage numbers would false-positive).
PHONE_RE = re.compile(
    r"(?<![\w.-])(?:\+?1[\s.-]?)?(?:\(\d{3}\)|\d{3})[\s.-]\d{3}[\s.-]\d{4}(?![\w.-])"
)

SSN_RE = re.compile(r"(?<![\w.-])\d{3}-\d{2}-\d{4}(?![\w.-])")

# 12-16 digits in 4-digit groups (credit/debit card shapes). The lookarounds
# keep it away from supplement dosages and phone-shaped numbers.
CARD_RE = re.compile(r"(?<![\w.-])\d{4}([ -]?\d{4}){2,3}(?![\w.-])")

# US/CA postal addresses (plan §4 Stage 0 step 3): house number + 1-4
# capitalized tokens + a street-type suffix, with an optional
# ", City, ST ZIP" tail. Conservative by construction — the leading house
# number and the suffix vocabulary keep it away from prose and dosages.
STREET_TYPES = (
    "Street", "Avenue", "Boulevard", "Parkway", "Highway", "Drive", "Court",
    "Place", "Lane", "Road", "Suite", "Ave", "Blvd", "Pkwy", "Hwy", "Rd",
    "Ste", "Apt", "Ln", "Ct", "Pl", "St", "Dr",
)
ADDRESS_RE = re.compile(
    r"(?<![\w.-])\d{1,6}\s+(?:[A-Z][\w'’.-]*\s+){1,4}"
    r"(?:" + "|".join(STREET_TYPES) + r")\b\.?"
    r"(?:\s*,?\s*(?:[A-Z][\w'’.-]*\s*){1,3},?\s*[A-Z][A-Za-z]\.?\s*\d{5}(?:-\d{4})?)?"
)

SOCIAL_HOSTS = (
    "facebook", "fb\\.me", "instagram", "linkedin", "tiktok", "twitter",
    r"x\.com", "snapchat", "pinterest", r"threads\.net", "reddit",
)
PROFILE_URL_RE = re.compile(
    r"(?:https?://)?(?:www\.)?(?:" + "|".join(SOCIAL_HOSTS) + r")\.com/[^\s<>\"']+",
    re.IGNORECASE,
)

# Greeting de-naming. The name must be followed by a terminator so that a
# capitalized ordinary word cannot be eaten ("Hello Thank you for your reply."
# has no terminator after "Thank" and is left alone). Names may be
# multi-word or "and"-joined ("Hi Kat and Neal", "Hi Jane Q. Customer,").
GREETING_LEAD_RE = re.compile(
    r"^(\s*(?i:Hi|Hey|Hello|Dear|Good\s+(?:morning|afternoon|evening))\s+)"
    # and-joined names may be lowercase ("Hey neal and Kat"); a bare
    # multi-word name must be capitalized throughout ("Jane Q. Customer"),
    # which keeps ordinary prose ("Hi from the team,") out.
    r"([A-Za-z][\w'’.-]*(?:\s+and\s+[A-Za-z][\w'’.-]*)*"
    r"|[A-Z][\w'’.-]*(?:\s+[A-Z][\w'’.-]*){1,3})"
    r"(?=\s*(?:[,!:;()–—…-]|$))"
)

# Owner disposition 2026-09-02: every corpus greeting residual was a real
# name in a shape the terminator rule cannot redact (no terminator after the
# name, honorific prefix, lowercase, slash-joined pairs). Instead of guessing
# on arbitrary tokens (which would eat prose — "Hello my friend"), names in
# this curated, corpus-attested vocabulary are redacted without a terminator
# ("Hey <name> and happy Sunday" -> "Hey [NAME] and happy Sunday"). An
# unknown name in the same position is still flagged, never guessed at.
GREETING_NAME_TOKENS = frozenset({"neal", "kat", "spruce", "eve"})
_NAME_ALT = r"(?:" + "|".join(sorted(GREETING_NAME_TOKENS)) + r")\b"
GREETING_LOOSE_RE = re.compile(
    # group 1 = the lead; group 2 = optional honorific + the name span, so
    # the honorific is replaced together with the name ("Hi Mr. Spruce and I
    # hope" -> "Hi [NAME] and I hope")
    r"^(\s*(?i:Hi|Hey|Hello|Dear|Good\s+(?:morning|afternoon|evening))\s+)"
    rf"((?:(?i:miss|mrs|mr|ms|dr)\.?\s+)?{_NAME_ALT}(?:(?:\s+and\s+|/){_NAME_ALT})*)",
    re.IGNORECASE,
)
# Anything still greeting-shaped after both passes (no terminator, name not in
# the vocabulary, so redacting would risk prose) -> flag for human review.
# Horizontal whitespace only: a greeting alone on a line must not reach across
# the newline and flag the first word of the next line ("Hello\nhow are you").
GREETING_RESIDUAL_RE = re.compile(
    r"^\s*(?i:Hi|Hey|Hello|Dear)[^\S\n]+([A-Za-z][\w'’.-]*)", re.MULTILINE
)
GREETING_STOPWORDS = frozenset({
    "there", "team", "everyone", "everybody", "all", "folks", "guys", "gals",
    "buddy", "gentlemen", "ladies", "again", "both", "sir", "madam",
    "doctor", "coach", "thank", "thanks", "from", "and",
    # corpus-attested non-names (owner disposition 2026-09-02: "Hello my
    # friend" is not PII and must not flag)
    "my", "friend",
})

COPYRIGHT_RE = re.compile(r"^\s*Copyright\s+\d{4}(?:[-–]\d{2,4})?\s+dotFIT", re.IGNORECASE)
DISCLAIMER_RE = re.compile(r"^\s*Disclaimer\s*:", re.IGNORECASE)

TITLE_RE = re.compile(
    r"^\s*(?:CEO|COO|CSO|CFO|CTO|President|Vice President|VP|Chairman|Founder|"
    r"Director|Manager|Head of \w+|Nutritionist|Dietitian|Coach|Trainer)\s*(?:,|\.)?\s*$",
    re.IGNORECASE,
)
SIG_PHONE_RE = re.compile(
    r"^\s*(?:Office|Tel|Phone|Fax|Direct|Mobile|Cell)?[\s.:]*(?:\+?1[\s.-]?)?"
    r"(?:\(\d{3}\)|\d{3})[\s.-]?\d{3}[\s.-]?\d{4}\s*$"
)
BARE_NAME_RE = re.compile(r"^\s*[A-Z][\w'’.-]+(?:\s+[A-Z][\w'’.-]+){0,2}\s*$")
# "Porter, Abby" — Outlook renders some recipients surname-first.
LAST_FIRST_RE = re.compile(r"^\s*[A-Z][\w'’.-]+\s*,\s*[A-Z][\w'’.-]+\s*$")

# Residual honorific+name check (see scrub_extracted step 6): names the corpus
# owner reviewed and approved (2026-09-02 dispositions) — public figures quoted
# in pasted articles/studies and dotFIT staff who answer questions (Neal
# Spruce). Matched against the SURNAME position (the last token of the
# captured name), so the corpus's "Dr. Hirsch" and "Dr. Jules Hirsch" both
# clear while an unrelated person who merely shares a first name does not.
# "Charlotte" used to live here to absorb a street address ("... McAlpine Park
# Dr., Charlotte, NC"); ADDRESS_RE now redacts that outright.
ACCEPTED_HONORIFIC_NAMES = frozenset({
    "Hazen", "Freeman", "Gardner", "Hirsch", "Nabel", "Sacks", "Katz",
    "Djalilian", "Paauw", "LeWine", "Davidson", "Gonzalez", "Axe",
    "Streichhan", "Spruce",
})
HONORIFIC_NAME_RE = re.compile(r"\b(?:Mr\.|Ms\.|Mrs\.|Dr\.)\s+([A-Z]\w+(?:\s+[A-Z]\w+){0,2})")

# Recipient labels: display names are people, addresses are handled by
# EMAIL_RE. Role mailboxes are kept verbatim — they carry no PII and they are
# the overwhelming majority of the corpus's To: lines.
RECIPIENT_LABELS = {"to", "cc", "bcc"}
ROLE_MAILBOXES = frozenset({
    "experts", "orders", "support", "info", "sales", "help", "admin",
    "customerservice", "customer service", "webmaster", "noreply",
})

# Signature backward-scan window above the quoted header.
MAX_SIGNATURE_LINES = 8

PATTERN_RULES: tuple[tuple[re.Pattern[str], str], ...] = (
    (EMAIL_RE, "[EMAIL]"),
    (PHONE_RE, "[PHONE]"),
    (SSN_RE, "[SSN]"),
    (CARD_RE, "[CARD]"),
    (ADDRESS_RE, "[ADDRESS]"),
    (PROFILE_URL_RE, "[PROFILE-URL]"),
)

REDACT_VALUE_LABELS = {"from", "name"}          # personal identifiers
EMAIL_VALUE_LABELS = {"email", "e-mail"}
PHONE_VALUE_LABELS = {"phone", "mobile", "cell"}


@dataclass
class ScrubReport:
    ok: bool
    error: str | None = None
    redactions: dict[str, int] = field(default_factory=dict)
    dropped: dict[str, int] = field(default_factory=dict)
    flags: list[str] = field(default_factory=list)
    residual_pii_flag: bool = False
    header_detected: bool = False
    signature_cut: bool = False
    greeting_name_redacted: bool = False
    n_lines_in: int = 0
    n_lines_out: int = 0

    def to_dict(self) -> dict:
        return {
            "ok": self.ok,
            "error": self.error,
            "redactions": self.redactions,
            "dropped": self.dropped,
            "flags": self.flags,
            "residual_pii_flag": self.residual_pii_flag,
            "header_detected": self.header_detected,
            "signature_cut": self.signature_cut,
            "greeting_name_redacted": self.greeting_name_redacted,
            "n_lines_in": self.n_lines_in,
            "n_lines_out": self.n_lines_out,
        }


# ---------------------------------------------------------------- scrubbing

def _bump(d: dict[str, int], key: str, n: int = 1) -> None:
    """Add *n* occurrences to counter *key* (counts are per occurrence)."""
    d[key] = d.get(key, 0) + n


def _redact_recipient_value(value: str, rep: ScrubReport) -> str:
    """``Neal Spruce <n@x.com>; Porter, Abby`` -> ``[NAME] <n@x.com>; [NAME]``.

    Role mailboxes (``Experts``) are kept: they are not personal data and they
    are how Stage 1 recognises the corpus's own support inbox.
    """
    out: list[str] = []
    for part in value.split(";"):
        stripped = part.strip()
        if not stripped:
            continue
        head, sep, tail = stripped.partition("<")
        display = head.strip().strip('"').strip("'")
        if sep:                                   # "Display <addr>"
            if display and display.casefold() not in ROLE_MAILBOXES:
                _bump(rep.redactions, "recipient_name")
                display = "[NAME]"
            out.append(f"{display} <{tail}".strip())
            continue
        if "@" in stripped:                       # bare address -> EMAIL_RE
            out.append(stripped)
            continue
        if stripped.casefold() in ROLE_MAILBOXES:
            out.append(stripped)
            continue
        if BARE_NAME_RE.match(stripped) or LAST_FIRST_RE.match(stripped):
            _bump(rep.redactions, "recipient_name")
            out.append("[NAME]")
            continue
        out.append(stripped)
    return "; ".join(out)


def _redact_label_line(line: str, rep: ScrubReport) -> str:
    """Structural redaction of labeled header/contact lines.

    Values are replaced but labels survive (``From: [CUSTOMER]``) so that
    Stage 1 can still split sections on the scrubbed text.
    """
    lab = label_of(line)
    if lab is None:
        return line
    label, value = lab
    if label in EMAIL_VALUE_LABELS:
        _bump(rep.redactions, "email_field")
        return f"{label.capitalize()}: [EMAIL]"
    if label in PHONE_VALUE_LABELS:
        _bump(rep.redactions, "phone_field")
        return f"{label.capitalize()}: [PHONE]"
    if label in REDACT_VALUE_LABELS:
        if EMAIL_RE.search(value):
            _bump(rep.redactions, "email_field")
            return f"{label.capitalize()}: [EMAIL]"
        if value:
            _bump(rep.redactions, "customer_name_field")
            return f"{label.capitalize()}: [CUSTOMER]"
    if label in RECIPIENT_LABELS and value:
        new = _redact_recipient_value(value, rep)
        if new != value:
            return f"{label.capitalize()}: {new}"
    return line  # Sent/Subject/Category/Question/Message: content, kept


def _cut_signature(lines: list[str], header_idx: int) -> tuple[list[str], bool]:
    """Drop the expert's signature block directly above the quoted header.

    Bounded backward scan; requires a title/phone anchor line to trigger so we
    never cut real content. Returns (new_lines, cut_done).
    """
    if header_idx <= 0:
        return lines, False
    drop: set[int] = set()
    seen_anchor = False
    start = max(-1, header_idx - 1 - MAX_SIGNATURE_LINES)
    for i in range(header_idx - 1, start, -1):
        s = lines[i].strip()
        if s == "":
            drop.add(i)
            continue
        if TITLE_RE.match(s) or SIG_PHONE_RE.match(s):
            drop.add(i)
            seen_anchor = True
            continue
        if seen_anchor and BARE_NAME_RE.match(s) and len(s) <= 40:
            drop.add(i)  # the bare name line
            continue
        break
    if not seen_anchor:
        return lines, False
    return [l for i, l in enumerate(lines) if i not in drop], True


def _redact_greetings(lines: list[str], rep: ScrubReport,
                      primary_idx: int | None) -> list[str]:
    """``Hi Edward,`` -> ``Hi [CUSTOMER],`` on the expert's opening line and
    ``Hi [NAME],`` on every other greeting in the document.

    Greetings occur below quoted thread headers too (a forwarded enquiry puts
    the reply — and its greeting — under the header), so every line is
    checked, not just the first. Two passes: the terminator-delimited rule
    first, then the curated-vocabulary rule for names with no terminator
    (GREETING_NAME_TOKENS — see its comment for why the vocabulary exists).
    """
    for i, line in enumerate(lines):
        m = GREETING_LEAD_RE.match(line)
        if m and m.group(2).split()[0].casefold() not in GREETING_STOPWORDS:
            placeholder = "[CUSTOMER]" if i == primary_idx else "[NAME]"
            lines[i] = line[:m.start(2)] + placeholder + line[m.end(2):]
            if placeholder == "[CUSTOMER]":
                rep.greeting_name_redacted = True
                _bump(rep.redactions, "greeting_name")
            else:
                _bump(rep.redactions, "greeting_name_quoted")
            continue
        loose = GREETING_LOOSE_RE.match(line)
        if loose and loose.group(2):
            placeholder = "[CUSTOMER]" if i == primary_idx else "[NAME]"
            lines[i] = (line[:loose.end(1)] + placeholder
                        + line[loose.end(2):])
            if placeholder == "[CUSTOMER]":
                rep.greeting_name_redacted = True
                _bump(rep.redactions, "greeting_name")
            else:
                _bump(rep.redactions, "greeting_name_quoted")
    return lines


def _pattern_scrub(text: str, rep: ScrubReport) -> str:
    for regex, placeholder in PATTERN_RULES:
        n = len(regex.findall(text))
        if n:
            _bump(rep.redactions,
                  placeholder.strip("[]").lower().replace("-", "_"), n)
            text = regex.sub(placeholder, text)
    return text


def _first_nonblank(lines: list[str], stop: int | None) -> int | None:
    """Index of the first non-blank line before *stop* (the expert region)."""
    for i, line in enumerate(lines[:stop] if stop is not None else lines):
        if line.strip():
            return i
    return None


def scrub_extracted(lines: list[str]) -> tuple[str, ScrubReport]:
    """Scrub extracted lines -> (scrubbed_text, report). Pure, deterministic."""
    rep = ScrubReport(ok=True)
    rep.n_lines_in = len(lines)

    # 1. drop boilerplate lines first (they would break the signature scan)
    out: list[str] = []
    for line in lines:
        if COPYRIGHT_RE.match(line):
            _bump(rep.dropped, "copyright_footer")
            continue
        if DISCLAIMER_RE.match(line):
            _bump(rep.dropped, "disclaimer_line")
            continue
        out.append(line)
    lines = out

    # 2. signature cut (bounded backward scan above the quoted header)
    header_idx = find_customer_header(lines)
    rep.header_detected = header_idx is not None
    if header_idx is not None:
        lines, cut = _cut_signature(lines, header_idx)
        if cut:
            _bump(rep.dropped, "signature_lines")
            rep.signature_cut = True
            header_idx = find_customer_header(lines)  # re-locate after cut

    # 3. structural redaction of labeled lines
    lines = [_redact_label_line(line, rep) for line in lines]

    # 4. greeting de-naming (whole document; the expert's opening greeting is
    #    the only one we can attribute to the enquirer)
    primary_idx = _first_nonblank(lines, header_idx)
    lines = _redact_greetings(lines, rep, primary_idx)

    # 5. pattern redaction over everything that remains
    text = "\n".join(lines)
    text = _pattern_scrub(text, rep)

    # 6. residual checks worth a human look (not auto-redacted — too risky)
    if any(
        m.group(1).split()[-1] not in ACCEPTED_HONORIFIC_NAMES
        for m in HONORIFIC_NAME_RE.finditer(text)
    ):
        rep.flags.append("honorific_plus_name")
        rep.residual_pii_flag = True
    if any(m.group(1).casefold() not in GREETING_STOPWORDS
           for m in GREETING_RESIDUAL_RE.finditer(text)):
        rep.flags.append("greeting_name_residual")
        rep.residual_pii_flag = True
    if rep.greeting_name_redacted:
        rep.flags.append("greeting_name_replaced")

    rep.n_lines_out = len(text.split("\n"))
    return text, rep


def scrub_docx(extraction: ExtractResult) -> tuple[str | None, ScrubReport]:
    """Full Stage 0 for one document: extract -> scrub. Returns (text, report);
    text is None for unparseable files (report.error explains why)."""
    if not extraction.ok:
        rep = ScrubReport(ok=False, error=extraction.error)
        return None, rep
    return scrub_extracted(extraction.lines)
