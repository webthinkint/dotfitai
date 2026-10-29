"""The Google Docs side: the REST client, a tab's text, and the requests that
rewrite a tab.

Indexes are UTF-16 code units, as the API counts them. A tab's body starts with
a section break at index 0 and always ends with a newline the API will not
delete, so a tab holding a file is the file's text with that last newline
standing in for the file's own.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from prompt_doc.style import Span

API = "https://docs.googleapis.com/v1/documents"
SCOPES = ["https://www.googleapis.com/auth/documents"]

# documents.get views. Inline shows open suggestions as text, with the indexes a
# write uses; the preview without suggestions is the text as if every open
# suggestion were rejected, which is what import takes.
INLINE = "SUGGESTIONS_INLINE"
WITHOUT_SUGGESTIONS = "PREVIEW_WITHOUT_SUGGESTIONS"


class DocsError(Exception):
    pass


class DocsClient:
    """documents.get and documents.batchUpdate as a service account."""

    def __init__(self, credentials_file: Path):
        from google.auth.transport.requests import AuthorizedSession
        from google.oauth2 import service_account

        creds = service_account.Credentials.from_service_account_file(
            str(credentials_file), scopes=SCOPES
        )
        self._session = AuthorizedSession(creds)

    def get(self, doc_id: str, view: str) -> dict:
        r = self._session.get(
            f"{API}/{doc_id}",
            params={"includeTabsContent": "true", "suggestionsViewMode": view},
            timeout=60,
        )
        return _json(r)

    def batch_update(self, doc_id: str, requests: list[dict]) -> dict:
        r = self._session.post(
            f"{API}/{doc_id}:batchUpdate", json={"requests": requests}, timeout=120
        )
        return _json(r)


def _json(r) -> dict:
    if r.ok:
        return r.json()
    try:
        message = r.json()["error"]["message"]
    except Exception:
        message = r.text[:300]
    raise DocsError(f"Google Docs API {r.status_code}: {message}")


@dataclass
class Tab:
    id: str
    title: str
    content: list[dict]

    @property
    def end(self) -> int:
        return self.content[-1]["endIndex"]


def flat_tabs(doc: dict) -> list[Tab]:
    """Every tab, child tabs after their parent, in the doc's order."""
    out: list[Tab] = []

    def walk(tabs: list[dict]) -> None:
        for t in tabs:
            props = t["tabProperties"]
            out.append(Tab(props["tabId"], props.get("title", ""), t["documentTab"]["body"]["content"]))
            walk(t.get("childTabs", []))

    walk(doc.get("tabs", []))
    return out


class UnsupportedContent(Exception):
    """A tab holds something a markdown file cannot: a table, an image, a chip."""


def text_of(tab: Tab) -> str:
    parts: list[str] = []
    for element in tab.content:
        if "sectionBreak" in element:
            continue
        if "paragraph" not in element:
            kind = next(k for k in element if k not in ("startIndex", "endIndex"))
            raise UnsupportedContent(f"{tab.title}: has a {kind}, which a text file cannot hold")
        for pe in element["paragraph"]["elements"]:
            if "textRun" in pe:
                parts.append(pe["textRun"]["content"])
                continue
            kind = next(k for k in pe if k not in ("startIndex", "endIndex"))
            raise UnsupportedContent(f"{tab.title}: has a {kind}, which a text file cannot hold")
    return "".join(parts)


def has_suggestions(tab: Tab) -> bool:
    """True if the tab has suggested edits nobody has accepted or rejected.
    Needs a tab read in the INLINE view."""
    for element in tab.content:
        for pe in element.get("paragraph", {}).get("elements", []):
            run = pe.get("textRun", {})
            if run.get("suggestedInsertionIds") or run.get("suggestedDeletionIds"):
                return True
    return False


def utf16_offsets(text: str) -> list[int]:
    """offsets[i] is the UTF-16 length of text[:i]."""
    offsets = [0]
    for c in text:
        offsets.append(offsets[-1] + (2 if ord(c) > 0xFFFF else 1))
    return offsets


def _rgb(hex_color: str) -> dict:
    r, g, b = (int(hex_color[i : i + 2], 16) / 255 for i in (0, 2, 4))
    return {"color": {"rgbColor": {"red": r, "green": g, "blue": b}}}


TEXT_STYLES: dict[str, tuple[dict, str]] = {
    "comment": ({"foregroundColor": _rgb("188038")}, "foregroundColor"),
    "marker": ({"foregroundColor": _rgb("9aa0a6")}, "foregroundColor"),
    "include": ({"foregroundColor": _rgb("9334e6"), "bold": True}, "foregroundColor,bold"),
    "bold": ({"bold": True}, "bold"),
    "italic": ({"italic": True}, "italic"),
    "code": ({"weightedFontFamily": {"fontFamily": "Roboto Mono"}}, "weightedFontFamily"),
}


def rewrite_requests(tab: Tab, text: str, spans: list[Span]) -> list[dict]:
    """Replace the tab's content with *text* (ending in a newline) and style it.

    Styles are reset first, so nothing an editor applied survives a rewrite and
    every look in the tab comes from *spans*.
    """
    body = text[:-1] if text.endswith("\n") else text
    requests: list[dict] = []
    if tab.end - 1 > 1:
        requests.append({"deleteContentRange": {"range": _range(tab, 1, tab.end - 1)}})
    if not body:
        return requests
    requests.append({"insertText": {"location": {"index": 1, "tabId": tab.id}, "text": body}})

    u = utf16_offsets(body)
    whole = _range(tab, 1, 1 + u[-1])
    requests += [
        {"updateParagraphStyle": {"range": whole, "paragraphStyle": {"namedStyleType": "NORMAL_TEXT"}, "fields": "namedStyleType"}},
        {"deleteParagraphBullets": {"range": whole}},
        {"updateTextStyle": {"range": whole, "textStyle": {}, "fields": "*"}},
    ]
    for span in spans:
        start, end = u[min(span.start, len(body))], u[min(span.end, len(body))]
        if end <= start:
            continue
        rng = _range(tab, 1 + start, 1 + end)
        if span.kind.startswith("h"):
            requests.append({"updateParagraphStyle": {
                "range": rng,
                "paragraphStyle": {"namedStyleType": f"HEADING_{span.kind[1:]}"},
                "fields": "namedStyleType",
            }})
        else:
            style, fields = TEXT_STYLES[span.kind]
            requests.append({"updateTextStyle": {"range": rng, "textStyle": style, "fields": fields}})
    return requests


def _range(tab: Tab, start: int, end: int) -> dict:
    return {"startIndex": start, "endIndex": end, "tabId": tab.id}
