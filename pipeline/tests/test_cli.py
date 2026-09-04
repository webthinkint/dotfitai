"""CLI helper tests."""

from __future__ import annotations

import json

from qa_pipeline.cli import _errors_path, _prune_orphans, _read_stage0_errors


def _write_errors(root, payload):
    path = _errors_path(root)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload), encoding="utf-8", newline="\n")
    return path


class TestStage0Errors:
    """The queue reads a committed artifact, not a gitignored runs/ manifest.

    The previous implementation globbed ``runs/`` — which ``.gitignore``
    excludes — so a fresh clone or the prod host rebuilt an empty queue and
    silently dropped the parse errors. ``errors.json`` is rewritten wholesale
    by every unfiltered run, so superseded corpus states cannot leak either.
    """

    def test_reads_recorded_errors(self, tmp_path):
        _write_errors(tmp_path, {"filtered": False, "files": [
            {"source_file": "2026/new.docx", "error": "BadZipFile: not a zip"}]})
        got = _read_stage0_errors(tmp_path)
        assert [f["source_file"] for f in got["files"]] == ["2026/new.docx"]

    def test_missing_file_is_empty_not_an_error(self, tmp_path):
        assert _read_stage0_errors(tmp_path) == {"filtered": False, "files": []}

    def test_filtered_flag_survives(self, tmp_path):
        _write_errors(tmp_path, {"filtered": True, "files": []})
        assert _read_stage0_errors(tmp_path)["filtered"] is True


class TestPruneOrphans:
    """A shrinking corpus must not leave ghost outputs behind: stage1 walks
    the output tree, so a stale .txt would keep producing a record forever
    and a byte-identical rerun could never detect it."""

    def test_removes_only_unexpected_files(self, tmp_path):
        keep = tmp_path / "2023" / "keep.txt"
        drop = tmp_path / "2023" / "gone.txt"
        keep.parent.mkdir(parents=True)
        keep.write_text("x", encoding="utf-8", newline="\n")
        drop.write_text("x", encoding="utf-8", newline="\n")

        assert _prune_orphans(tmp_path, {keep}) == 1
        assert keep.exists()
        assert not drop.exists()

    def test_removes_emptied_directories(self, tmp_path):
        stale = tmp_path / "2022" / "old.txt"
        stale.parent.mkdir(parents=True)
        stale.write_text("x", encoding="utf-8", newline="\n")

        assert _prune_orphans(tmp_path, set()) == 1
        assert not (tmp_path / "2022").exists()

    def test_missing_root_is_a_noop(self, tmp_path):
        assert _prune_orphans(tmp_path / "nope", set()) == 0
