"""prompt-doc export | import. Settings come from the root .env (or the
environment): DOTFIT_PROMPT_DOC_ID and DOTFIT_PROMPT_DOC_CREDENTIALS."""

from __future__ import annotations

import argparse
import os
import re
import sys
from pathlib import Path

from prompt_doc.docs_api import DocsClient, DocsError
from prompt_doc.sync import SyncError, export, import_

DOC_ID = "DOTFIT_PROMPT_DOC_ID"
CREDENTIALS = "DOTFIT_PROMPT_DOC_CREDENTIALS"


def repo_root() -> Path:
    """Repo root derived from this file's location, never from the cwd."""
    return Path(__file__).resolve().parents[3]


def read_env(path: Path) -> dict[str, str]:
    """KEY=VALUE lines; blank lines and # comments skipped, quotes stripped."""
    values: dict[str, str] = {}
    if not path.exists():
        return values
    for raw in path.read_text(encoding="utf-8-sig").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in "'\"":
            value = value[1:-1]
        elif (m := re.search(r"(^|\s)#", value)):
            value = value[: m.start()].rstrip()
        values[key.strip()] = value
    return values


def doc_id_of(value: str) -> str:
    """Accept the doc's URL or its bare ID."""
    m = re.search(r"/document/d/([A-Za-z0-9_-]+)", value)
    return m.group(1) if m else value


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="prompt-doc", description="The assistant's prompt and references in a Google Doc, one tab per file.")
    sub = parser.add_subparsers(dest="command", required=True)
    ex = sub.add_parser("export", help="write the files into the Google Doc")
    ex.add_argument("--all", action="store_true", help="rewrite every tab, not only the ones the repo changed")
    ex.add_argument("--force", action="store_true", help="overwrite unimported doc edits; export a dirty tree")
    im = sub.add_parser("import", help="write the Google Doc's edits into the files")
    im.add_argument("--dry-run", action="store_true", help="print the diff, write nothing")
    im.add_argument("--force", action="store_true", help="take the doc's text where both sides changed")
    args = parser.parse_args(argv)

    repo = repo_root()
    env = {**read_env(repo / ".env"), **{k: v for k, v in os.environ.items() if k in (DOC_ID, CREDENTIALS)}}
    missing = [k for k in (DOC_ID, CREDENTIALS) if not env.get(k)]
    if missing:
        print(f"set {' and '.join(missing)} in .env (see .env.example)", file=sys.stderr)
        return 2
    credentials = Path(env[CREDENTIALS]).expanduser()
    if not credentials.is_absolute():
        credentials = repo / credentials
    if not credentials.is_file():
        print(f"{CREDENTIALS}: no file at that path", file=sys.stderr)
        return 2

    docs = DocsClient(credentials)
    doc_id = doc_id_of(env[DOC_ID])
    try:
        if args.command == "export":
            export(repo, docs, doc_id, all_tabs=args.all, force=args.force)
        else:
            written = import_(repo, docs, doc_id, dry_run=args.dry_run, force=args.force)
            if written and not args.dry_run:
                print("Review with git diff, then check: dotnet run --project src/DotFit.Assistant.Cli -- prompt, and dotnet test (from runtime/)")
    except (SyncError, DocsError) as e:
        print(e, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
