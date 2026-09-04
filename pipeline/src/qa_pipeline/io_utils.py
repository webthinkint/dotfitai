"""Cross-platform I/O helpers.

Every read/write in this pipeline goes through here so that output bytes are
identical on Windows (dev) and Linux (production):

- UTF-8 explicitly everywhere (never rely on locale/platform defaults)
- LF line endings (``newline="\\n"``) so files diff cleanly across OSes
- POSIX-style document ids derived from input-relative paths
- deterministic ordering (sorted iteration)
"""

from __future__ import annotations

import hashlib
import json
import sys
from pathlib import Path
from typing import Any, Iterable

UTF8 = "utf-8"


def configure_stdio() -> None:
    """Make console output safe on Windows code pages (e.g. cp1251/cp1252).

    Content we print (filenames, snippets) can contain any Unicode char;
    without this, ``print`` raises UnicodeEncodeError on Windows consoles.
    On Linux this is a no-op in practice.
    """
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding=UTF8, errors="replace")  # type: ignore[union-attr]
        except (AttributeError, ValueError):
            pass


def write_text(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding=UTF8, newline="\n")


def write_json(path: Path, obj: Any) -> None:
    write_text(path, json.dumps(obj, ensure_ascii=False, indent=1, sort_keys=False) + "\n")


def read_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding=UTF8))


def iter_docx(root: Path) -> list[Path]:
    """Deterministically ordered .docx files under *root* (recursive)."""
    return sorted(p for p in root.rglob("*.docx") if p.is_file())


def doc_id(rel_posix: str) -> str:
    """Stable short id from the POSIX-style input-relative path."""
    return hashlib.sha256(rel_posix.encode(UTF8)).hexdigest()[:16]


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def rel_posix(path: Path, root: Path) -> str:
    """POSIX-normalised path of *path* relative to *root* (stable ids on any OS)."""
    return path.resolve().relative_to(root.resolve()).as_posix()


def append_jsonl(path: Path, records: Iterable[dict[str, Any]]) -> None:
    """Append records as JSON Lines. Records must already be sorted by caller."""
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding=UTF8, newline="\n") as f:
        for rec in records:
            f.write(json.dumps(rec, ensure_ascii=False) + "\n")
