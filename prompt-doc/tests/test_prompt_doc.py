from __future__ import annotations

import pytest

from prompt_doc.cli import doc_id_of
from prompt_doc.docs_api import Tab, UnsupportedContent, rewrite_requests, text_of
from prompt_doc.style import Span, spans
from prompt_doc.sync import READ_ME, SyncError, export, import_, normalize, state

from conftest import git

DOC = "doc"


def quiet(_: str) -> None:
    pass


def styled(text: str) -> dict[str, list[str]]:
    out: dict[str, list[str]] = {}
    for s in spans(text):
        out.setdefault(s.kind, []).append(text[s.start : s.end])
    return out


# --- style --------------------------------------------------------------------

def test_spans_mark_markdown():
    got = styled("## Tone\n\nBe **warm**, *brief* and use `read_reference`. {{escalation-list}}\n---\n| a |\n")
    assert got["h2"] == ["## Tone"]
    assert got["bold"] == ["warm"]
    assert got["italic"] == ["brief"]
    assert got["code"] == ["read_reference"]
    assert got["include"] == ["{{escalation-list}}"]
    assert got["marker"] == ["##", "**", "**", "*", "*", "`", "`", "---", "|", "|"]


def test_nothing_inside_a_comment_is_styled_but_the_comment():
    got = styled("<!--\n## not a heading **nor bold**\n-->\n* a bullet, not italic*\n")
    assert got == {"comment": ["<!--\n## not a heading **nor bold**\n-->"]}


# --- docs_api -----------------------------------------------------------------

def test_rewrite_counts_utf16():
    tab = Tab("t", "x", [{"endIndex": 1}, {"endIndex": 5}])
    text = "🩺 **a**\n"
    reqs = rewrite_requests(tab, text, spans(text))
    assert reqs[0] == {"deleteContentRange": {"range": {"startIndex": 1, "endIndex": 4, "tabId": "t"}}}
    assert reqs[1]["insertText"]["text"] == "🩺 **a**"
    bold = next(r for r in reqs if r.get("updateTextStyle", {}).get("fields") == "bold")
    assert bold["updateTextStyle"]["range"] == {"startIndex": 6, "endIndex": 7, "tabId": "t"}


def test_text_of_refuses_what_a_file_cannot_hold():
    tab = Tab("t", "prompt/x.md", [{"startIndex": 0, "endIndex": 1, "sectionBreak": {}},
                                   {"startIndex": 1, "endIndex": 9, "table": {}}])
    with pytest.raises(UnsupportedContent, match="table"):
        text_of(tab)


def test_normalize_and_state():
    assert normalize("a\r\nb\vc") == "a\nb\nc\n"
    assert state("f", "f", None) == "same"
    assert state("f", "b", "b") == "repo-changed"
    assert state("b", "t", "b") == "doc-changed"
    assert state("f", "t", "b") == "both-changed"
    assert state("f", "t", None) == "both-changed"


def test_doc_id_of_url():
    assert doc_id_of("https://docs.google.com/document/d/abc_D-1/edit?tab=t.0") == "abc_D-1"
    assert doc_id_of("abc_D-1") == "abc_D-1"


# --- sync ---------------------------------------------------------------------

def test_export_builds_the_doc(repo, docs):
    export(repo, docs, DOC, log=quiet)
    assert [t["title"] for t in docs.tabs] == [
        READ_ME, "prompt/escalation-list.md", "prompt/posture.md", "references/guide.md"]
    for t in docs.tabs[1:]:
        assert t["text"] == (repo / "assistant" / t["title"]).read_text()
    head = git(repo, "rev-parse", "HEAD").strip()
    assert f"Exported from commit {head}" in docs.tab(READ_ME)["text"]


def test_round_trip_imports_only_the_edit(repo, docs):
    export(repo, docs, DOC, log=quiet)
    docs.edit("prompt/posture.md", "**warm**", "**kind**")
    written = import_(repo, docs, DOC, log=quiet)
    assert [p.name for p in written] == ["posture.md"]
    assert "Be **kind** and" in (repo / "assistant/prompt/posture.md").read_text()
    assert git(repo, "status", "--porcelain").strip() == "M assistant/prompt/posture.md"


def test_soft_line_break_imports_as_newline(repo, docs):
    export(repo, docs, DOC, log=quiet)
    docs.edit("prompt/escalation-list.md", "doctor", "doctor\v")
    import_(repo, docs, DOC, log=quiet)
    assert (repo / "assistant/prompt/escalation-list.md").read_text() == "- See a doctor\n 🩺 when unsure.\n"


def test_export_refuses_to_overwrite_unimported_edits(repo, docs):
    export(repo, docs, DOC, log=quiet)
    docs.edit("prompt/posture.md", "warm", "kind")
    with pytest.raises(SyncError, match="prompt/posture.md"):
        export(repo, docs, DOC, log=quiet)


def test_export_refuses_open_suggestions(repo, docs):
    export(repo, docs, DOC, log=quiet)
    docs.suggested.add("references/guide.md")
    with pytest.raises(SyncError, match="suggestions"):
        export(repo, docs, DOC, log=quiet)


def test_export_rewrites_only_what_the_repo_changed(repo, docs):
    export(repo, docs, DOC, log=quiet)
    (repo / "assistant/references/guide.md").write_text("# Guide\n\nNew line.\n")
    git(repo, "commit", "-qam", "edit guide")
    assert export(repo, docs, DOC, log=quiet) == ["references/guide.md"]
    assert docs.tab("references/guide.md")["text"] == "# Guide\n\nNew line.\n"


def test_import_skips_both_sides_changed_unless_forced(repo, docs):
    export(repo, docs, DOC, log=quiet)
    docs.edit("prompt/posture.md", "warm", "kind")
    (repo / "assistant/prompt/posture.md").write_text("Repo edit.\n")
    git(repo, "commit", "-qam", "repo edit")
    assert import_(repo, docs, DOC, log=quiet) == []
    assert import_(repo, docs, DOC, force=True, log=quiet)
    assert "kind" in (repo / "assistant/prompt/posture.md").read_text()


def test_import_leaves_a_stale_tab_alone(repo, docs):
    export(repo, docs, DOC, log=quiet)
    (repo / "assistant/prompt/posture.md").write_text("Repo edit.\n")
    git(repo, "commit", "-qam", "repo edit")
    assert import_(repo, docs, DOC, log=quiet) == []
    assert (repo / "assistant/prompt/posture.md").read_text() == "Repo edit.\n"


def test_export_after_import_and_commit_is_in_sync(repo, docs):
    export(repo, docs, DOC, log=quiet)
    docs.edit("prompt/posture.md", "warm", "kind")
    import_(repo, docs, DOC, log=quiet)
    git(repo, "commit", "-qam", "import")
    assert export(repo, docs, DOC, log=quiet) == []


def test_export_refuses_a_dirty_tree(repo, docs):
    (repo / "assistant/prompt/posture.md").write_text("Uncommitted.\n")
    with pytest.raises(SyncError, match="uncommitted"):
        export(repo, docs, DOC, log=quiet)
