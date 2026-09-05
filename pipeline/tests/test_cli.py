"""CLI helper tests."""

from __future__ import annotations

import argparse
import json

from qa_pipeline.cli import (
    _errors_path, _prune_orphans, _read_stage0_errors, cmd_stage1,
)


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


class TestStage1ExcludesUnanswerableDocs:
    """Owner disposition 2026-09-02: docs without expert replies and blank
    docs are excluded from documents.jsonl (tallied in summary.json), not
    sent to the review queue — there is nothing left to decide about them."""

    def _build_stage0(self, root):
        text = root / "stage0" / "text" / "2023"
        text.mkdir(parents=True)
        (text / "note.txt").write_text(
            "A plain note about creatine loading.\n", encoding="utf-8")
        (text / "stub.txt").write_text(
            "From: [CUSTOMER]\nEmail: [EMAIL]\nQuestion: anything?\n",
            encoding="utf-8")
        (text / "blank.txt").write_text("\n", encoding="utf-8")

    def test_excluded_tallied_and_never_queued(self, tmp_path, capsys):
        self._build_stage0(tmp_path)
        assert cmd_stage1(argparse.Namespace(out=str(tmp_path), quiet=True)) == 0

        docs = [json.loads(l) for l in
                (tmp_path / "stage1" / "documents.jsonl").read_text(
                    encoding="utf-8").splitlines()]
        assert [d["source_file"] for d in docs] == ["2023/note.docx"]

        summary = json.loads(
            (tmp_path / "stage1" / "summary.json").read_text(encoding="utf-8"))
        assert summary["n_documents"] == 1
        assert summary["n_excluded"] == {
            "no_expert_answer": 1, "empty_document": 1}
        assert [e["source_file"] for e in summary["excluded_files"]] == [
            "2023/blank.docx", "2023/stub.docx"]

        queue = (tmp_path / "stage1" / "review_queue.jsonl").read_text(
            encoding="utf-8").splitlines()
        assert queue == []
