"""A fake Google Doc that keeps each tab as plain text, and a throwaway git repo.

The fake applies the requests export sends (tabs, deletes, inserts) and checks
every range against the tab's real UTF-16 length, so an off-by-one in
rewrite_requests fails here instead of against the live API. Styles are only
range-checked: import never reads them.

All file text in these tests is synthetic.
"""

from __future__ import annotations

import subprocess
from pathlib import Path

import pytest

from prompt_doc.docs_api import utf16_offsets


class FakeDocs:
    def __init__(self) -> None:
        self.tabs: list[dict] = [{"id": "t0", "title": "Tab 1", "text": "\n"}]
        self.suggested: set[str] = set()  # titles shown with an open suggestion
        self.batches: list[list[dict]] = []

    def tab(self, title: str) -> dict:
        return next(t for t in self.tabs if t["title"] == title)

    def edit(self, title: str, old: str, new: str) -> None:
        t = self.tab(title)
        assert old in t["text"]
        t["text"] = t["text"].replace(old, new)

    def get(self, doc_id: str, view: str) -> dict:
        return {"tabs": [self._tab_json(t, view) for t in self.tabs]}

    def _tab_json(self, t: dict, view: str) -> dict:
        content = [{"startIndex": 0, "endIndex": 1, "sectionBreak": {}}]
        index = 1
        for line in t["text"].splitlines(keepends=True):
            n = utf16_offsets(line)[-1]
            run = {"content": line}
            if view == "SUGGESTIONS_INLINE" and t["title"] in self.suggested:
                run["suggestedInsertionIds"] = ["s1"]
            content.append({"startIndex": index, "endIndex": index + n,
                            "paragraph": {"elements": [{"startIndex": index, "endIndex": index + n, "textRun": run}]}})
            index += n
        return {"tabProperties": {"tabId": t["id"], "title": t["title"]},
                "documentTab": {"body": {"content": content}}}

    def batch_update(self, doc_id: str, requests: list[dict]) -> dict:
        self.batches.append(requests)
        for r in requests:
            (kind, body), = r.items()
            if kind == "addDocumentTab":
                props = body["tabProperties"]
                new = {"id": f"t{len(self.tabs)}", "title": props["title"], "text": "\n"}
                self.tabs.insert(props.get("index", len(self.tabs)), new)
            elif kind == "updateDocumentTabProperties":
                self._by_id(body["tabProperties"]["tabId"])["title"] = body["tabProperties"]["title"]
            elif kind == "deleteContentRange":
                t, s, e = self._span(body["range"], deletable=True)
                t["text"] = t["text"][:s] + t["text"][e:]
            elif kind == "insertText":
                loc = body["location"]
                t = self._by_id(loc["tabId"])
                s = self._pos(t, loc["index"])
                t["text"] = t["text"][:s] + body["text"] + t["text"][s:]
            elif kind in ("updateTextStyle", "updateParagraphStyle", "deleteParagraphBullets"):
                self._span(body["range"])
            else:
                raise AssertionError(f"unexpected request {kind}")
        return {}

    def _by_id(self, tab_id: str) -> dict:
        return next(t for t in self.tabs if t["id"] == tab_id)

    def _pos(self, t: dict, index: int) -> int:
        offsets = utf16_offsets(t["text"])
        assert index - 1 in offsets, f"index {index} splits a character"
        return offsets.index(index - 1)

    def _span(self, rng: dict, deletable: bool = False) -> tuple[dict, int, int]:
        t = self._by_id(rng["tabId"])
        limit = utf16_offsets(t["text"])[-1] + (0 if deletable else 1)  # the last newline stays
        assert 1 <= rng["startIndex"] < rng["endIndex"] <= limit, (rng, limit)
        return t, self._pos(t, rng["startIndex"]), self._pos(t, rng["endIndex"])


def git(repo: Path, *args: str) -> str:
    return subprocess.run(
        ["git", "-C", str(repo), "-c", "user.name=Test", "-c", "user.email=test@example.com", *args],
        check=True, capture_output=True, text=True,
    ).stdout


@pytest.fixture
def repo(tmp_path: Path) -> Path:
    (tmp_path / "assistant/prompt/variants").mkdir(parents=True)
    (tmp_path / "assistant/references").mkdir()
    (tmp_path / "assistant/prompt/posture.md").write_text(
        "<!-- Why: a synthetic note. -->\n\n## Tone\n\nBe **warm** and *brief*. {{escalation-list}}\n"
    )
    (tmp_path / "assistant/prompt/escalation-list.md").write_text("- See a doctor 🩺 when unsure.\n")
    (tmp_path / "assistant/prompt/variants/default.md").write_text("{{posture}}\n")
    (tmp_path / "assistant/references/guide.md").write_text("# Guide\n\n| a | b |\n|---|---|\n| 1 | 2 |\n")
    git(tmp_path, "init", "-q")
    git(tmp_path, "add", ".")
    git(tmp_path, "commit", "-qm", "init")
    return tmp_path


@pytest.fixture
def docs() -> FakeDocs:
    return FakeDocs()
