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

SOCIAL_DOMAINS = (
    "facebook\\.com", "fb\\.me", "instagram\\.com", "linkedin\\.com",
    "tiktok\\.com", "twitter\\.com", r"x\.com", "snapchat\\.com",
    "pinterest\\.com", r"threads\.net", "reddit\\.com",
)
PROFILE_URL_RE = re.compile(
    r"(?<![A-Za-z0-9.-])(?:https?://)?(?:www\.)?(?:" + "|".join(SOCIAL_DOMAINS) + r")/[^\s<>\"']+",
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
# "zane" added with the Stage 2 staff ruling (2026-09-08): the SuppBeast
# co-host gets the same greeting-position treatment as Neal. Zero-diff on this
# corpus — his 7 attestations are all mid-prose or bare-lead ("Zane, I got the
# answer"), never after a Hi/Hey/Dear lead — so it is future-proofing, not a
# regen catch, and it is here so the two staff vocabularies do not drift.
GREETING_NAME_TOKENS = frozenset({"neal", "kat", "spruce", "eve", "zane"})
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
    r"^\s*(?i:Hi|Hey|Hello|Dear|Good\s+(?:morning|afternoon|evening))[^\S\n]+([A-Za-z][\w'’.-]*)", re.MULTILINE
)
GREETING_STOPWORDS = frozenset({
    "there", "team", "everyone", "everybody", "all", "folks", "guys", "gals",
    "buddy", "gentlemen", "ladies", "again", "both", "sir", "madam",
    "doctor", "coach", "thank", "thanks", "from", "and",
    # corpus-attested non-names (owner disposition 2026-09-02: "Hello my
    # friend" is not PII and must not flag)
    "my", "friend",
})

# Customer sign-off de-naming (owner disposition 2026-09-05, review round 2:
# two quoted replies leaked full names as sign-offs — "Thanks,\\n\\nJane
# Smith" and "-- \\nRegards,\\n\\nDr Jane Smith (PhD Org Chem)"). A bare
# closer line followed (past blanks) by ONE bare-name line is a sign-off, not
# prose; the name span is replaced with [NAME]. Only below the quoted header
# (the customer region) — expert sign-offs above it are staff names, not PII.
# In forwarded threads the expert reply also sits below the header, so a
# staff sign-off there redacts to [NAME] too: harmless (sign-offs carry no
# answer content) and still safer than attributing roles by rule.
CLOSER_RE = re.compile(
    r"^\s*(?i:thanks|thank you|many thanks|thanks so much|regards|"
    r"kind regards|best regards|best|sincerely|cheers)\s*[,.!]*\s*$",
)
_SIG_DELIM_RE = re.compile(r"^\s*--\s*$")
_SIGNOFF_NAME_RE = re.compile(
    r"^\s*((?:(?i:mr|mrs|ms|dr)\.?\s+)?)([A-Z][\w'’.-]+"
    r"(?:\s+[A-Z][\w'’.-]+){0,2})(\s*\(.*\))?\s*$",
)

# Inline customer sign-offs (owner disposition 2026-09-06, Stage 2 triage
# round 1: "Respectfully, Kendra Ferguson" and "Thanks, Matt" sit on ONE
# line at end-of-line, which the bare-name-next-line rule cannot see). The
# closer alternation mirrors CLOSER_RE plus "respectfully" (attested in the
# triage sample); the name span is at most TWO capitalized tokens — a third
# token is usually an organization ("Thanks, Diabetic Support Group"), and
# missing it fails safe toward the LLM flag, never toward prose damage.
# Same region gate as _redact_signoffs: customer region only.
# Bare "best" is the one closer that REQUIRES its comma: unlike the others it
# is also an ordinary adjective, and comma-less it ate prose in the first
# regen ("...and Best Plant Protein." plus the heading "Best Scientific
# Combination" both became "Best [NAME]" — review 2026-09-07). A sign-off
# writes "Best, Matt"; a sentence writes "Best Plant Protein". Every other
# closer keeps the optional comma ("Thanks Neal" is attested).
# The misspelled closers are deliberate, not sloppiness (Stage 2 triage round
# 2, 2026-09-08): "Thnak you, <full name>" reached the index because the
# alternation only knew the correct spellings, and a customer typing their own
# sign-off is exactly the moment they mistype. Only forms attested in the
# corpus are listed — an invented variant is a false-positive surface with no
# recall to show for it.
_INLINE_CLOSER_AT = re.compile(
    r"\b(?:(?i:thanks|thank you|many thanks|thanks so much|"
    r"regards|kind regards|best regards|sincerely|cheers|"
    r"respectfully|thnak you|thnaks|thanx|thx|tks|thankyou|thank u|"
    r"tanks)\s*,?|(?i:best)\s*,)\s+",
)
_INLINE_NAME_RE = re.compile(
    r"^((?:(?i:mr|mrs|ms|dr)\.?\s+)?)"
    r"([A-Z][\w'’.-]+(?:\s+[A-Z][\w'’.-]+)?)"
    r"(\s*\(.*\))?\s*$",
)

# Quoted attribution headers (owner disposition 2026-09-06, Stage 2 triage
# round 1: "Neal Spruce <[EMAIL]> wrote:" survives inside quoted replies).
# Role-neutral [NAME] is safe for staff and customers alike, so this runs
# document-wide, unlike the region-gated sign-off rules.
# The display name is matched case-INSENSITIVELY (triage round 2, 2026-09-08:
# a mail client rendered it lowercase and the whole name escaped). Whatever
# sits in the display-name slot of "<addr> wrote:" is a person by
# construction, so the structural anchor carries the rule and the capital is
# not load-bearing; the anchor is also what keeps prose out, since a name
# here must be immediately followed by an address and "wrote:".
_WROTE_RE = re.compile(
    r"^(?P<pre>.*?)(?P<name>[A-Za-z][\w'’.-]+"
    r"(?:\s+[A-Za-z][\w'’.-]+){0,2})\s+"
    r"(?P<mail><[^<>\n]*>|[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+"
    r"(?:\.[A-Za-z0-9\-]+)+)\s+wrote:\s*$",
)

# Web-form body fields ("Question:", "Message:") carry the customer's own
# words, so a name dangling after the last sentence is their sign-off — the
# form has no separate signature block for it to live in. This is the shape
# with NO closer to key on at all (triage round 2, 2026-09-08: a full name
# after a closing quote, "... Have a good week!" <name>), which is why
# _INLINE_CLOSER_AT cannot see it and why the anchor here is the field label
# plus end-of-line instead.
#
# Three guards keep it off prose, and together they take the corpus-wide fire
# count to 20 lines with no false positive (regen scan 2026-09-08):
#   - it must follow sentence-terminal punctuation (optionally a closing quote
#     and an em-dash lead-in), so a trailing product name inside a sentence
#     never qualifies;
#   - at most two capitalized tokens, mirroring _INLINE_NAME_RE — a third is
#     usually an organization;
#   - CLOSER_TOKENS and digits are rejected in _redact_form_field_signoffs.
# A closer may trail the name ("... objection. Thanks. <name> Thanks."), so
# one is allowed after the capture without becoming part of it.
_FORM_FIELD_LABELS = r"(?:Question|Message|Comments?)"
_CLOSER_ALT = (r"(?i:thanks|thank you|thankyou|thanx|thx|regards|best|"
               r"sincerely|cheers|respectfully)")
FORM_FIELD_SIGNOFF_RE = re.compile(
    r"^" + _FORM_FIELD_LABELS + r"\s*:.*[.!?][\"'’”)]*\s+(?:[-–—]\s*)?"
    r"([A-Z][A-Za-z'’.-]+(?:\s+(?!" + _CLOSER_ALT + r"\b)"
    r"[A-Z][A-Za-z'’.-]*\.?)?)"
    r"(?:\s+" + _CLOSER_ALT + r"[.!,]*)?\s*$",
)
# Rejected in the capture: a sign-off tail that is only a closer is not a
# name. Superset of the closer alternation plus the words that trail one
# ("Thank you", "Thanks again", "in advance", "so much").
CLOSER_TOKENS = frozenset({
    "thanks", "thank", "thankyou", "thanx", "thx", "tks", "tanks", "thnak",
    "thnaks", "regards", "best", "sincerely", "cheers", "respectfully",
    "warmly", "you", "u", "again", "advance", "much", "so", "in",
})

# Self-introductions (was open item 16, folded into the round-2 triage
# 2026-09-08): the *opening* mirror of the sign-off rules — "My name is Jane
# Doe and I run a studio". Sign-off rules read the end of the field, greeting
# rules read the salutation, and this shape is neither, which is why 10
# `is_current` records carried a self-introduced customer name.
# "my name is" is about as unambiguous an anchor as the corpus offers, so the
# rule runs document-wide and role-neutral like _WROTE_RE; the capitalization
# requirement is what ends the span ("my name is Bart in your superblend"
# stops at Bart), and the two-token cap mirrors _INLINE_NAME_RE.
SELF_INTRO_RE = re.compile(
    r"\b(?i:my name is)\s+((?:(?i:mr|mrs|ms|dr)\.?\s+)?)"
    r"([A-Z][A-Za-z'’-]+(?:\s+[A-Z][A-Za-z'’-]+)?)",
)

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
    # owner disposition 2026-09-05 (review queue round 2): pasted
    # articles/transcripts — study authors and podcast guests, not customers.
    # NOTE: "Williams" is a common surname — a future customer "Dr Williams"
    # would clear silently; veto here if that trade is ever wrong.
    "Crichton", "Jastreboff", "Watto", "Williams", "Kargi",
    "Ornish", "LaFaver", "Leibel",
    # owner disposition 2026-09-06 (Stage 2 triage round 1): quoted study
    # author "Joachim Feldkamp, MD, PhD" carries no honorific prefix, so
    # this entry never fires the Stage 0 rule above — it acts through the
    # Stage 2 prompt's do-not-flag list, which shares this vocabulary.
    "Feldkamp",
})
# Gap inside an honorific span: with a period, horizontal whitespace plus at
# most ONE newline (a wrapped "Dr.\nSmith"); without a period, real separation
# is required — bare "Dr" must not glue onto a word ("DrPH" the degree) and
# two newlines never belong to one reference ("lean Mr\n\nThanks," is the
# LeanMR product plus a closer, not "Mr Thanks" — regen catch 2026-09-05).
_HON_WS_DOT = r"\.(?:[^\S\n]*\n)?[^\S\n]*"
_HON_WS_NL = r"[^\S\n]*\n[^\S\n]*"
_HON_WS_SP = r"[^\S\n]+"
HONORIFIC_NAME_RE = re.compile(
    r"\b(?:Mr|Ms|Mrs|Dr)(?:" + _HON_WS_DOT + "|" + _HON_WS_NL + "|" + _HON_WS_SP + ")" +
    r"([A-Z](?:\w+|\.)?(?:(?:" + _HON_WS_NL + "|" + _HON_WS_SP + r")[A-Z](?:\w+|\.)?){0,2})"
)

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


def _redact_signoffs(lines: list[str], header_idx: int | None,
                       rep: ScrubReport) -> list[str]:
    """``Thanks,\\n\\nJane Smith`` -> ``Thanks,\\n\\n[NAME]`` below the header.

    Exactly one bare-name line (optional honorific, optional credential
    parenthetical — the honorific/credential stay, they are not identifying).
    A standalone ``--`` email delimiter directly above the closer goes with
    it. No header (expert notes) -> untouched: there is no quoted customer
    mail to protect and sign-offs there are staff bylines.
    """
    if header_idx is None:
        return lines
    out = list(lines)
    drop: set[int] = set()
    for i in range(header_idx, len(out)):
        if i in drop or not CLOSER_RE.match(out[i]):
            continue
        j = i + 1
        blanks = 0
        while j < len(out) and not out[j].strip():
            j += 1
            blanks += 1
            if blanks > 3:
                break
        if j >= len(out):
            continue
        m = _SIGNOFF_NAME_RE.match(out[j])
        if not m:
            continue
        out[j] = out[j][:m.start(2)] + "[NAME]" + out[j][m.end(2):]
        _bump(rep.redactions, "signoff_name")
        if i - 1 >= 0 and _SIG_DELIM_RE.match(out[i - 1]):
            drop.add(i - 1)
    if drop:
        _bump(rep.dropped, "signature_delim", len(drop))
        out = [l for k, l in enumerate(out) if k not in drop]
    return out


def _redact_inline_signoffs(lines: list[str], header_idx: int | None,
                              rep: ScrubReport) -> list[str]:
    """``Thanks, Matt`` / ``Respectfully, Kendra Ferguson`` -> ``[NAME]``.

    Same-region inline form of the sign-off rule: closer + name on ONE line
    at end-of-line, below the quoted header only. At most two name tokens —
    a third capitalized token is usually an organization, and missing it
    fails safe toward the LLM flag. Stopword-led tails (``Thanks, all``)
    are kept. Honorific/credential survive, as in _redact_signoffs.
    """
    if header_idx is None:
        return lines
    out = list(lines)
    for i in range(header_idx, len(out)):
        # every closer on the line is a candidate split ("Thank you in
        # advance! Respectfully, NAME" must reach the second one); the
        # last name-shaped tail wins — closest to end-of-line is the sign-off
        hit = None
        for cm in _INLINE_CLOSER_AT.finditer(out[i]):
            nm = _INLINE_NAME_RE.match(out[i][cm.end():])
            if nm and nm.group(2) and nm.group(2).split()[0].casefold() \
                    not in GREETING_STOPWORDS:
                hit = (cm.end(), nm)
        if hit is None:
            continue
        pos, nm = hit
        out[i] = (out[i][:pos] + nm.group(1) + "[NAME]"
                  + (nm.group(3) or ""))
        _bump(rep.redactions, "signoff_name_inline")
    return out


def _redact_form_field_signoffs(lines: list[str],
                                  rep: ScrubReport) -> list[str]:
    """``Question: ... good week!" Jane Doe`` -> ``... good week!" [NAME]``.

    The closer-less sign-off: a name dangling at the end of a web-form body
    field. Not region-gated — the ``Question:``/``Message:`` label *is* the
    region, since those fields only ever hold the enquirer's own words.
    Candidates made only of closer words, or carrying a digit (``N07 Rage``),
    are prose or product and are left alone.
    """
    out = []
    for line in lines:
        m = FORM_FIELD_SIGNOFF_RE.match(line)
        if m:
            cand = m.group(1)
            tokens = [t.strip(".,!?'’\"").casefold() for t in cand.split()]
            if (not any(ch.isdigit() for ch in cand)
                    and not any(t in CLOSER_TOKENS or t in GREETING_STOPWORDS
                                for t in tokens)):
                line = line[:m.start(1)] + "[NAME]" + line[m.end(1):]
                _bump(rep.redactions, "signoff_name_form_field")
        out.append(line)
    return out


def _redact_self_introductions(lines: list[str],
                                 rep: ScrubReport) -> list[str]:
    """``My name is Jane Doe and ...`` -> ``My name is [NAME] and ...``.

    The honorific and any credential survive, as everywhere else. Public
    figures clear at the SURNAME position, the same test the honorific
    residual check uses — a clinician quoted in a pasted article introduces
    himself in the quote, and the owner ruled those are quoted verbatim.
    """
    out = []
    for line in lines:
        pos = 0
        pieces = []
        for m in SELF_INTRO_RE.finditer(line):
            name = m.group(2)
            tokens = name.split()
            if (tokens[0].casefold() in GREETING_STOPWORDS
                    or tokens[-1] in ACCEPTED_HONORIFIC_NAMES):
                continue
            pieces.append(line[pos:m.start(2)])
            pieces.append("[NAME]")
            pos = m.end(2)
            _bump(rep.redactions, "self_introduction_name")
        if pieces:
            line = "".join(pieces) + line[pos:]
        out.append(line)
    return out


def _redact_wrote_headers(lines: list[str], rep: ScrubReport) -> list[str]:
    """``Neal Spruce <neal@x.com> wrote:`` -> ``[NAME] <[EMAIL]> wrote:``.

    Quoted attribution headers survive inside replies; the display name is a
    person either way, so role-neutral [NAME] applies document-wide (the raw
    address becomes [EMAIL] in the pattern pass that follows).
    """
    out = []
    for line in lines:
        m = _WROTE_RE.match(line)
        if m and m.group("name").split()[0].casefold() not in GREETING_STOPWORDS:
            line = line[:m.start("name")] + "[NAME]" + line[m.end("name"):]
            _bump(rep.redactions, "wrote_header_name")
        out.append(line)
    return out


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

    # 4b. customer sign-off de-naming (quoted region only — see _redact_signoffs)
    lines = _redact_signoffs(lines, header_idx, rep)

    # 4c. inline sign-offs ("Thanks, Matt" — same region gate), closer-less
    # web-form sign-offs, and quoted "X wrote:" attribution headers (both
    # role-neutral and document-wide, anchored structurally rather than by
    # region). Inline runs first so its [NAME] already occupies the tail the
    # form-field rule would otherwise re-examine.
    lines = _redact_inline_signoffs(lines, header_idx, rep)
    lines = _redact_form_field_signoffs(lines, rep)
    lines = _redact_self_introductions(lines, rep)
    lines = _redact_wrote_headers(lines, rep)

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
