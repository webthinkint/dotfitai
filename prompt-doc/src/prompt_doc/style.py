"""Reading aids for the exported doc: which characters of a markdown file get
which look. Only export uses this. Import reads text and ignores every style,
so nothing here can change a file.

Offsets are Python string offsets into the file text; docs_api turns them into
the API's UTF-16 indexes.
"""

from __future__ import annotations

import re
from dataclasses import dataclass


@dataclass(frozen=True)
class Span:
    start: int
    end: int
    kind: str  # h1..h6, comment, marker, bold, italic, code, include


COMMENT = re.compile(r"<!--.*?-->", re.S)
HEADING = re.compile(r"^(#{1,6}) +\S.*$", re.M)
RULE = re.compile(r"^ {0,3}(?:-{3,}|\*{3,}|_{3,}) *$", re.M)
TABLE_ROW = re.compile(r"^ *\|.*$", re.M)
CODE = re.compile(r"`[^`\n]+`")
INCLUDE = re.compile(r"\{\{[a-z0-9-]+\}\}")
BOLD = re.compile(r"\*\*(?=\S)([^\n]+?)(?<=\S)\*\*")
# Single-star italic only: `_` appears inside names like menu_desc, and a `*`
# followed by a space is a list bullet.
ITALIC = re.compile(r"(?<![*\w])\*(?=[^\s*])([^*\n]+?)(?<=[^\s*])\*(?![*\w])")


def spans(text: str) -> list[Span]:
    """Every styled range in *text*. A comment is styled as a comment and
    nothing else inside it is, since the model never sees it."""
    out: list[Span] = []
    comments = [(m.start(), m.end()) for m in COMMENT.finditer(text)]
    out += [Span(s, e, "comment") for s, e in comments]
    plain = _blank(text, comments)

    for m in HEADING.finditer(plain):
        out.append(Span(m.start(), m.end(), f"h{len(m.group(1))}"))
        out.append(Span(m.start(1), m.end(1), "marker"))
    for m in RULE.finditer(plain):
        out.append(Span(m.start(), m.end(), "marker"))
    for m in TABLE_ROW.finditer(plain):
        out += [
            Span(m.start() + i, m.start() + i + 1, "marker")
            for i, c in enumerate(m.group())
            if c == "|"
        ]

    codes = [(m.start(), m.end()) for m in CODE.finditer(plain)]
    for s, e in codes:
        out += [Span(s, s + 1, "marker"), Span(s + 1, e - 1, "code"), Span(e - 1, e, "marker")]
    plain = _blank(plain, codes)

    out += [Span(m.start(), m.end(), "include") for m in INCLUDE.finditer(plain)]

    bold_markers = []
    for m in BOLD.finditer(plain):
        out.append(Span(m.start(1), m.end(1), "bold"))
        bold_markers += [(m.start(), m.start(1)), (m.end(1), m.end())]
    out += [Span(s, e, "marker") for s, e in bold_markers]
    plain = _blank(plain, bold_markers)

    for m in ITALIC.finditer(plain):
        out += [
            Span(m.start(), m.start(1), "marker"),
            Span(m.start(1), m.end(1), "italic"),
            Span(m.end(1), m.end(), "marker"),
        ]
    return sorted(out, key=lambda s: (s.start, s.end, s.kind))


def _blank(text: str, ranges: list[tuple[int, int]]) -> str:
    """Hide *ranges* from later patterns, keeping offsets and line breaks."""
    chars = list(text)
    for s, e in ranges:
        for i in range(s, e):
            if chars[i] != "\n":
                chars[i] = "\0"
    return "".join(chars)
