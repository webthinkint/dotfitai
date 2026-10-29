"""Export and import between the assistant's markdown files and the Google Doc.

Each file is one tab, titled with its path under assistant/. A "Read me" tab
first tells the editor how the doc works and records the commit the last export
came from. That commit is the base of a three-way check, so neither direction
overwrites the other side's unsynced edits:

- tab == file             in sync
- tab == base             only the repo changed: export refreshes the tab
- file == base            only the doc changed: import writes the file
- all three differ        both changed: each direction refuses without --force

Text is compared after normalize(). Styles are never read: an editor making
text bold in the doc changes nothing; wrapping it in ** does.
"""

from __future__ import annotations

import difflib
import re
import subprocess
from collections.abc import Callable
from datetime import date
from pathlib import Path

from prompt_doc import docs_api
from prompt_doc.docs_api import Tab, UnsupportedContent, flat_tabs, has_suggestions, text_of
from prompt_doc.style import Span, spans

ASSISTANT = "assistant"
FOLDERS = ("prompt", "references")
READ_ME = "Read me"
STAMP = re.compile(r"Exported from commit ([0-9a-f]{40})")


class SyncError(Exception):
    pass


def content_files(repo: Path) -> dict[str, Path]:
    """Tab title → file: every .md directly in assistant/prompt and
    assistant/references. Variants are include plumbing and stay out."""
    files: dict[str, Path] = {}
    for folder in FOLDERS:
        for path in sorted((repo / ASSISTANT / folder).glob("*.md")):
            files[f"{folder}/{path.name}"] = path
    return files


def normalize(text: str) -> str:
    """Line breaks as \\n (Docs gives a soft line break as \\v), ending in one."""
    text = text.replace("\r\n", "\n").replace("\r", "\n").replace("\v", "\n")
    return text if text.endswith("\n") else text + "\n"


def state(file: str, tab: str, base: str | None) -> str:
    if tab == file:
        return "same"
    if tab == base:
        return "repo-changed"
    if file == base:
        return "doc-changed"
    return "both-changed"


def export(
    repo: Path, docs, doc_id: str, *, all_tabs: bool = False, force: bool = False,
    today: date | None = None, log: Callable[[str], None] = print,
) -> list[str]:
    """Write the files into the doc. Returns the titles of the tabs rewritten.

    A tab is rewritten only if the repo changed it (or with all_tabs), because a
    rewrite also detaches the doc's margin comments on that tab.
    """
    files = content_files(repo)
    dirty = _git(repo, "status", "--porcelain", "--", *(f"{ASSISTANT}/{f}" for f in FOLDERS))
    if dirty.strip() and not force:
        raise SyncError(
            "uncommitted changes under assistant/prompt or assistant/references; "
            "commit them first so the doc records which version it holds, or pass --force\n" + dirty
        )
    head = _git(repo, "rev-parse", "HEAD").strip()

    tabs = flat_tabs(docs.get(doc_id, docs_api.INLINE))
    by_title = {t.title: t for t in tabs}
    base_sha = _stamp(by_title.get(READ_ME))

    rewrite: list[str] = []
    problems: list[str] = []
    for title, path in files.items():
        tab = by_title.get(title)
        if tab is None:
            rewrite.append(title)
            continue
        problem = None
        if has_suggestions(tab):
            problem = f"{title}: has suggestions nobody has accepted or rejected"
        else:
            try:
                tab_text = normalize(text_of(tab))
            except UnsupportedContent as e:
                tab_text, problem = None, str(e)
            if tab_text is not None:
                st = state(normalize(_read(path)), tab_text, _at(repo, base_sha, path))
                if st in ("doc-changed", "both-changed"):
                    problem = f"{title}: edited in the doc and not imported yet"
                elif st == "repo-changed" or all_tabs:
                    rewrite.append(title)
        if problem:
            problems.append(problem)
            if force:
                rewrite.append(title)
    if problems and not force:
        raise SyncError(
            "the doc has edits an export would overwrite. Import them first, or pass --force "
            "to overwrite:\n  " + "\n  ".join(problems)
        )

    structure = _structure_requests(tabs, files)
    if structure:
        docs.batch_update(doc_id, structure)
        by_title = {t.title: t for t in flat_tabs(docs.get(doc_id, docs_api.INLINE))}

    for title in files:
        if title in rewrite:
            text = normalize(_read(files[title]))
            docs.batch_update(doc_id, docs_api.rewrite_requests(by_title[title], text, spans(text)))
            log(f"exported {title}")
    text, read_me_spans = read_me(head, today or date.today())
    docs.batch_update(doc_id, docs_api.rewrite_requests(by_title[READ_ME], text, read_me_spans))
    in_sync = len(files) - len(rewrite)
    log(f"{len(rewrite)} tab(s) rewritten, {in_sync} already in sync; doc stamped with {head[:12]}")
    return rewrite


def import_(
    repo: Path, docs, doc_id: str, *, dry_run: bool = False, force: bool = False,
    log: Callable[[str], None] = print,
) -> list[Path]:
    """Write the doc's edits into the files. Returns the files written (or, on
    a dry run, the files that would be). Never commits."""
    files = content_files(repo)
    pending = {t.title for t in flat_tabs(docs.get(doc_id, docs_api.INLINE)) if has_suggestions(t)}
    tabs = flat_tabs(docs.get(doc_id, docs_api.WITHOUT_SUGGESTIONS))
    by_title = {t.title: t for t in tabs}
    base_sha = _stamp(by_title.get(READ_ME))
    if base_sha is None:
        log("warning: the Read me tab has no export stamp, so every difference counts as a conflict")

    written: list[Path] = []
    for title, path in files.items():
        tab = by_title.get(title)
        if tab is None:
            log(f"{title}: no tab in the doc, skipped")
            continue
        try:
            tab_text = normalize(text_of(tab))
        except UnsupportedContent as e:
            log(f"{e}; skipped")
            continue
        if title in pending:
            log(f"{title}: has open suggestions; only accepted text is imported")
        file_text = normalize(_read(path))
        st = state(file_text, tab_text, _at(repo, base_sha, path))
        if st == "same":
            continue
        if st == "repo-changed":
            log(f"{title}: changed in the repo since the export, not in the doc; skipped (export to refresh)")
            continue
        if st == "both-changed" and not force:
            log(f"{title}: changed in both the repo and the doc since the export; skipped (--force takes the doc's)")
            continue
        if dry_run:
            log("".join(difflib.unified_diff(
                file_text.splitlines(keepends=True), tab_text.splitlines(keepends=True),
                f"a/{ASSISTANT}/{title}", f"b/{ASSISTANT}/{title}",
            )))
        else:
            path.write_bytes(tab_text.encode("utf-8"))
            log(f"imported {title}")
        written.append(path)

    for t in tabs:
        if t.title != READ_ME and t.title not in files:
            log(f"tab {t.title!r} matches no file; ignored")
    log(f"{len(written)} file(s) {'would change' if dry_run else 'written'}")
    return written


def read_me(sha: str, today: date) -> tuple[str, list[Span]]:
    title = "dotFIT Assistant: prompt and references"
    lines = [
        title,
        "Each tab is one file the assistant reads, named after its place in the repository. "
        "Edit the text in place; a developer imports the changes, checks them and puts them live.",
        "The colours and sizes are a reading aid only. They are redone on every export and "
        "ignored on import, so making text bold or bigger here changes nothing:",
        "• Grey symbols are markdown and do the formatting: ## starts a heading, **two stars** "
        "make bold, a line starting with - is a list item. Type them to format text.",
        "• Green text between <!-- and --> is a note for people. The assistant never sees it: "
        "it is the place to say why a rule is there.",
        "• Purple {{name}} pulls in another part or text the assistant fills in itself. "
        "Keep it exactly as written.",
        "Type plain text only: tables, images, smart chips and drawings cannot go back into a file.",
        "To propose a change for review, use Suggesting mode. Only accepted suggestions are imported.",
        "Margin comments are kept until a developer re-exports a tab with changes from the repository.",
        "",
        f"Exported from commit {sha} on {today.isoformat()}.",
    ]
    return "\n".join(lines) + "\n", [Span(0, len(title), "h1")]


def _structure_requests(tabs: list[Tab], files: dict[str, Path]) -> list[dict]:
    """Add the Read me tab and a tab for each file that has none. A new doc's
    empty first tab becomes the Read me tab."""
    titles = {t.title for t in tabs}
    requests: list[dict] = []
    if READ_ME not in titles:
        first = tabs[0]
        try:
            empty = first.title not in files and text_of(first) == "\n"
        except UnsupportedContent:
            empty = False
        if empty:
            requests.append({"updateDocumentTabProperties": {
                "tabProperties": {"tabId": first.id, "title": READ_ME}, "fields": "title",
            }})
        else:
            requests.append({"addDocumentTab": {"tabProperties": {"title": READ_ME, "index": 0}}})
    for title in files:
        if title not in titles:
            requests.append({"addDocumentTab": {"tabProperties": {"title": title}}})
    return requests


def _stamp(tab: Tab | None) -> str | None:
    if tab is None:
        return None
    try:
        m = STAMP.search(text_of(tab))
    except UnsupportedContent:
        return None
    return m.group(1) if m else None


def _at(repo: Path, sha: str | None, path: Path) -> str | None:
    """The file as of commit *sha*, or None if unknown here."""
    if sha is None:
        return None
    rel = path.relative_to(repo).as_posix()
    r = subprocess.run(["git", "-C", str(repo), "show", f"{sha}:{rel}"], capture_output=True)
    return normalize(r.stdout.decode("utf-8")) if r.returncode == 0 else None


def _git(repo: Path, *args: str) -> str:
    r = subprocess.run(["git", "-C", str(repo), *args], capture_output=True, text=True)
    if r.returncode != 0:
        raise SyncError(f"git {' '.join(args)} failed: {r.stderr.strip()}")
    return r.stdout


def _read(path: Path) -> str:
    return path.read_bytes().decode("utf-8")
