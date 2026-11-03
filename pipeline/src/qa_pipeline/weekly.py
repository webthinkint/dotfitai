"""The weekly Q&A refresh: Monday fetch and report, Thursday publish.

Runs in its own clone (``pipeline/deploy/install-weekly.sh``), so a week's
unpublished outputs never sit in the checkout the service or a person works in.
HEAD there is what was last published, so "what Thursday would publish" is always
the working tree against HEAD. Nothing needs remembering between runs, and a
skipped week simply rolls into the next report.

- **Monday** fetches, runs Stages 0, 1, 2 and 4, writes the report and mails it.
- **Thursday** takes any pipeline fixes from origin, reruns the same stages from
  cache with no fetch, indexes, commits and pushes. Only what Monday fetched can
  go out. ``RUNS/skip-thursday`` skips one week's publish, and the file is removed
  when it is honoured, so a skip cannot be forgotten.

The fetch only adds and updates: it is the one writer to ``original-data/QAs/``
and it never deletes, because Stage 0 and the index prune whatever has no input,
so a mirrored deletion would remove live answers. ``MAX_RETIRED`` stops a publish
that would take out more than a normal week could.

The report holds customer-derived text, so it stays on this machine, gitignored
and readable by its owner alone. The relay is plain SMTP, so mail carries counts
and a path and never a question, answer, filename or error message.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import re
import smtplib
import subprocess
import sys
import traceback
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass, field, replace
from datetime import datetime
from email.message import EmailMessage
from pathlib import Path, PurePosixPath
from typing import Any

from .azure_config import ENV_FILENAME, read_env_file, repo_root
from .io_utils import (
    configure_stdio, doc_id, iter_docx, read_json, read_jsonl, rel_posix,
    write_json, write_text,
)

# Year folder under original-data/QAs/ -> the shared folder that feeds it. The
# token can read everything the account can, so the fetch reads these alone.
QA_FOLDERS = {"2026": "2026 FP Q&A"}
# A newly shared year's folder is reported rather than fetched: it joins QA_FOLDERS.
QA_FOLDER_NAME = re.compile(r"\d{4} FP Q&A")

MAX_RETIRED = 20
SMTP_PORT = 25
CONTENT_HASH_BLOCK = 4 * 1024 * 1024
SERVED_FIELDS = ("question_canonical", "answer", "filename")
PII_REASON = "residual_pii_flag"


# --- deterministic core --------------------------------------------------------

@dataclass(frozen=True)
class RemoteFile:
    rel: str  # relative to original-data/QAs/, POSIX, as a record id hashes it
    content_hash: str
    dropbox_id: str


@dataclass
class FetchPlan:
    new: list[RemoteFile] = field(default_factory=list)
    changed: list[RemoteFile] = field(default_factory=list)
    copies_skipped: int = 0


@dataclass
class Changes:
    new: list[dict[str, Any]] = field(default_factory=list)
    new_not_served: int = 0
    changed: list[dict[str, Any]] = field(default_factory=list)
    retired: list[dict[str, Any]] = field(default_factory=list)
    returned: list[dict[str, Any]] = field(default_factory=list)

    def any(self) -> bool:
        return bool(self.new or self.changed or self.retired or self.returned)


def content_hash(path: Path) -> str:
    """Dropbox's content hash: SHA-256 over the SHA-256 of each 4 MB block."""
    blocks = []
    with path.open("rb") as f:
        while chunk := f.read(CONTENT_HASH_BLOCK):
            blocks.append(hashlib.sha256(chunk).digest())
    return hashlib.sha256(b"".join(blocks)).hexdigest()


def safe_rel(year: str, rel: str) -> str:
    """Join a folder-relative path under its year; a path that escapes raises."""
    parts = PurePosixPath(rel).parts
    if not parts or PurePosixPath(rel).is_absolute() or ".." in parts:
        raise ValueError("Dropbox returned a path outside its folder")
    return f"{year}/{rel}"


def plan_fetch(remote: list[RemoteFile], local: dict[str, str]) -> FetchPlan:
    """Decide what to download. *local* maps a relative path to its content hash.

    Content already held at another path is a copy and is skipped: its path would
    hash to a new record id for an answer already in the corpus. Dropbox does not
    promise the case of a path's components, so a path differing only by case is
    the local file, which keeps its record id.
    """
    plan = FetchPlan()
    by_case = {rel.casefold(): rel for rel in local}
    known = set(local.values())
    for file in sorted(remote, key=lambda f: f.rel):
        target = by_case.get(file.rel.casefold(), file.rel)
        if target in local:
            if local[target] != file.content_hash:
                plan.changed.append(replace(file, rel=target))
                known.add(file.content_hash)
        elif file.content_hash in known:
            plan.copies_skipped += 1
        else:
            plan.new.append(file)
            known.add(file.content_hash)
    return plan


def diff_records(before: list[dict[str, Any]],
                 after: list[dict[str, Any]]) -> Changes:
    """What publishing *after* over *before* changes in what customers can get."""
    changes = Changes()
    previous = {r["id"]: r for r in before}
    current = {r["id"]: r for r in after}
    for rec in sorted(after, key=lambda r: r["source_file"]):
        old = previous.get(rec["id"])
        if old is None:
            if rec.get("is_current"):
                changes.new.append(rec)
            else:
                changes.new_not_served += 1
        elif rec.get("is_current") and not old.get("is_current"):
            changes.returned.append(rec)
        elif rec.get("is_current") and any(
                rec.get(f) != old.get(f) for f in SERVED_FIELDS):
            changes.changed.append(rec)
    for old in sorted(before, key=lambda r: r["source_file"]):
        if old.get("is_current") and not current.get(old["id"], {}).get("is_current"):
            changes.retired.append(old)
    return changes


def _record_block(rec: dict[str, Any], reasons: dict[str, list[str]],
                  evidence: dict[str, str]) -> list[str]:
    source = rec["source_file"]
    lines = [f"### {rec['id']}", "", f"- File: `{source}`",
             f"- Name served: {rec.get('filename') or '(none)'}"]
    if reasons.get(source):
        lines.append(f"- Flags: {', '.join(reasons[source])}")
    if evidence.get(source):
        lines.append(f"- The model flagged: {evidence[source]}")
    lines += ["", f"**Question:** {rec.get('question_canonical') or '(none)'}", "",
              f"**Answer:** {rec.get('answer') or '(none)'}", ""]
    return lines


def render_report(changes: Changes, reasons: dict[str, list[str]],
                  evidence: dict[str, str], fetched: dict[str, Any],
                  skip_path: Path, date: str) -> str:
    """The Monday report: read the flagged records, the changed and the retiring."""
    flagged = [r for r in changes.new + changes.returned if reasons.get(r["source_file"])]
    plain = [r for r in changes.new if not reasons.get(r["source_file"])]
    lines = [f"# Q&A weekly report, {date}", "",
             "Thursday's run publishes all of this. Fix what is not ok in the "
             "pipeline before then, or skip one week with:", "",
             f"    touch '{skip_path}'", ""]
    lines += alerts(changes, fetched)
    for line in summary_lines(changes, reasons, fetched):
        lines.append(f"- {line}")
    lines.append("")
    if fetched.get("pii_not_served"):
        lines += ["The PII flags left out of this report are not in anything a customer "
                  "sees. The flagged text is still in the Stage 0 and Stage 1 files or the "
                  "path, and publishing commits those to git.", ""]
    if flagged:
        lines += ["## Flagged: read these", ""]
        for rec in flagged:
            lines += _record_block(rec, reasons, evidence)
    if changes.changed:
        lines += ["## Changed answers", ""]
        for rec in changes.changed:
            lines += _record_block(rec, reasons, evidence)
    if changes.retired:
        lines += ["## Leaving search", ""]
        for rec in changes.retired:
            lines += _record_block(rec, reasons, evidence)
    unflagged_returned = [r for r in changes.returned
                          if not reasons.get(r["source_file"])]
    if unflagged_returned:
        lines += ["## Returning to search", ""]
        for rec in unflagged_returned:
            lines += _record_block(rec, reasons, evidence)
    if plain:
        lines += ["## New, not flagged", ""]
        for rec in plain:
            lines.append(f"- `{rec['id']}`: {rec.get('question_canonical') or rec['source_file']}")
        lines.append("")
    if fetched.get("no_record"):
        lines += ["## New files with no record", "",
                  "Stage 0 could not read these, or Stage 1 found no expert answer. "
                  "They go nowhere; open them if one should have.", ""]
        lines += [f"- `{rel}`" for rel in fetched["no_record"]]
    return "\n".join(lines).rstrip() + "\n"


def pii_flag_is_unserved(span: str, rec: dict[str, Any], stage0_text: str) -> bool:
    """Whether a PII flag's span is provably outside what customers can see.

    The model flags what it notices in the source, and the source's customer
    section is not served, so a flag can name text no answer carries. That text is
    still there: only the reader's question changes, not what git receives. Anything
    uncertain stays flagged: one word of the span in served text (it may have been
    reworded), or a span that cannot be found in the source (it may be a paraphrase).
    """
    served = " ".join(str(rec.get(f) or "") for f in SERVED_FIELDS).casefold()
    words = [w.strip(".,;:!?()'\"").casefold() for w in span.split()]
    words = [w for w in words if len(w) >= 3]
    if not words or any(w in served for w in words):
        return False
    return span.casefold() in (stage0_text + " " + rec["source_file"]).casefold()


def split_unserved_pii(records: list[dict[str, Any]], reasons: dict[str, list[str]],
                       evidence: dict[str, str], stage0_root: Path,
                       ) -> tuple[dict[str, list[str]], int]:
    """*reasons* without the PII flags on unserved text; -> (reasons, how many)."""
    kept = dict(reasons)
    dropped = 0
    for rec in records:
        source = rec["source_file"]
        if PII_REASON not in kept.get(source, []) or not evidence.get(source):
            continue
        text_path = stage0_root / (source.removesuffix(".docx") + ".txt")
        text = text_path.read_text(encoding="utf-8") if text_path.is_file() else ""
        if pii_flag_is_unserved(evidence[source], rec, text):
            kept[source] = [r for r in kept[source] if r != PII_REASON]
            dropped += 1
    return kept, dropped


def summary_lines(changes: Changes, reasons: dict[str, list[str]],
                  fetched: dict[str, Any]) -> list[str]:
    """Counts only: these lines go into mail."""
    flagged = sum(bool(reasons.get(r["source_file"]))
                  for r in changes.new + changes.returned)
    return [
        f"Files fetched: {len(fetched.get('new', []))} new, "
        f"{len(fetched.get('changed', []))} updated, "
        f"{fetched.get('copies_skipped', 0)} exact copies skipped",
        f"New records going out: {len(changes.new)} "
        f"({changes.new_not_served} more are superseded and stay out)",
        f"Flagged to read: {flagged}",
        f"PII flags not in served text, committed to git only: "
        f"{fetched.get('pii_not_served', 0)}",
        f"Changed answers: {len(changes.changed)}",
        f"Leaving search: {len(changes.retired)}",
        f"Returning to search: {len(changes.returned)}",
        f"New files with no record: {len(fetched.get('no_record', []))}",
    ]


def alerts(changes: Changes, fetched: dict[str, Any]) -> list[str]:
    """Things a person has to act on. Counts and folder names only: these go in mail."""
    lines = []
    if not fetched.get("new") and not fetched.get("changed"):
        lines.append("**No new files.** Check that the source folder is still "
                     "receiving them, or whether a new year's folder has started.")
    if fetched.get("unconfigured_folders"):
        lines.append("**Shared but not fetched:** "
                     f"{', '.join(fetched['unconfigured_folders'])}. Add it to "
                     "QA_FOLDERS in pipeline/src/qa_pipeline/weekly.py.")
    if len(changes.retired) > MAX_RETIRED:
        lines.append(f"**{len(changes.retired)} answers would leave search**, over "
                     f"the limit of {MAX_RETIRED}. Thursday will stop rather than "
                     "publish this.")
    return [line for alert in lines for line in (alert, "")]


def load_evidence(cache_path: Path,
                  records: list[dict[str, Any]]) -> dict[str, str]:
    """``source_file`` -> the model's flagged span, joined on answer text as
    ``scripts/stage2_queue_triage.py`` does. A missing cache gives an empty map."""
    if not cache_path.is_file():
        return {}
    by_answer: dict[str, str] = {}
    for row in read_jsonl(cache_path):
        value = row.get("v") or {}
        span = value.get("residual_pii_evidence")
        if value.get("answer") is not None and span:
            by_answer.setdefault(value["answer"], span)
    return {r["source_file"]: by_answer[r["answer"]] for r in records
            if r.get("answer") in by_answer}


def load_reasons(qa_root: Path) -> dict[str, list[str]]:
    """``source_file`` -> every review reason from Stages 1, 2 and 4."""
    reasons: dict[str, set[str]] = {}
    for stage in ("stage1", "stage2", "stage4"):
        for row in read_jsonl(qa_root / stage / "review_queue.jsonl"):
            reasons.setdefault(row["source_file"], set()).update(row["reasons"])
    return {source: sorted(found) for source, found in reasons.items()}


# --- Dropbox ------------------------------------------------------------------

class Dropbox:
    """The read-only calls the fetch makes. Errors never carry key material."""

    def __init__(self, env: dict[str, str]) -> None:
        basic = base64.b64encode(
            f"{env['DROPBOX_APP_KEY']}:{env['DROPBOX_APP_SECRET']}".encode()).decode()
        token = self._send(urllib.request.Request(
            "https://api.dropboxapi.com/oauth2/token",
            data=urllib.parse.urlencode({
                "grant_type": "refresh_token",
                "refresh_token": env["DROPBOX_REFRESH_TOKEN"]}).encode(),
            headers={"Authorization": f"Basic {basic}"}))
        self.access_token = json.loads(token)["access_token"]

    @staticmethod
    def _send(request: urllib.request.Request) -> bytes:
        try:
            with urllib.request.urlopen(request, timeout=120) as response:
                return response.read()
        except urllib.error.HTTPError as e:
            raise RuntimeError(f"Dropbox answered HTTP {e.code} for "
                               f"{urllib.parse.urlparse(request.full_url).path}") from None

    def _rpc(self, endpoint: str, body: dict[str, Any]) -> dict[str, Any]:
        return json.loads(self._send(urllib.request.Request(
            f"https://api.dropboxapi.com/2/{endpoint}",
            data=json.dumps(body).encode(),
            headers={"Authorization": f"Bearer {self.access_token}",
                     "Content-Type": "application/json"})))

    def _paged(self, endpoint: str, key: str, body: dict[str, Any]) -> list[dict[str, Any]]:
        page = self._rpc(endpoint, body)
        entries = list(page.get(key, []))
        while page.get("cursor") and page.get("has_more", True):
            page = self._rpc(f"{endpoint}/continue", {"cursor": page["cursor"]})
            entries += page.get(key, [])
        return entries

    def shared_folders(self) -> list[dict[str, Any]]:
        """Every folder shared with the account, once: a mounted one can appear in
        both listings, and the mounted entry, which carries its path, wins."""
        folders = (self._paged("sharing/list_mountable_folders", "entries", {"limit": 200})
                   + self._paged("sharing/list_folders", "entries", {"limit": 200}))
        return list({f["shared_folder_id"]: f for f in folders}.values())

    def list_docx(self, folder: dict[str, Any], year: str) -> list[RemoteFile]:
        """Every .docx under *folder*, Word's ``~$`` lock files aside."""
        entries = self._paged("files/list_folder", "entries", {
            "path": f"ns:{folder['shared_folder_id']}", "recursive": True, "limit": 2000})
        mount = (folder.get("path_lower") or "").lower()
        files = []
        for entry in entries:
            name = entry["name"]
            if (entry[".tag"] != "file" or not name.lower().endswith(".docx")
                    or name.startswith("~$")):
                continue
            path = entry["path_display"]
            if mount and path.lower().startswith(mount + "/"):
                path = path[len(mount):]
            files.append(RemoteFile(safe_rel(year, path.lstrip("/")),
                                    entry["content_hash"], entry["id"]))
        return files

    def download(self, file: RemoteFile, target: Path) -> None:
        """Write *file* to *target* whole or not at all, after checking its hash."""
        data = self._send(urllib.request.Request(
            "https://content.dropboxapi.com/2/files/download", method="POST",
            headers={"Authorization": f"Bearer {self.access_token}",
                     "Dropbox-API-Arg": json.dumps({"path": file.dropbox_id})}))
        target.parent.mkdir(parents=True, exist_ok=True)
        part = target.with_name(target.name + ".part")
        part.write_bytes(data)
        if content_hash(part) != file.content_hash:
            part.unlink()
            raise RuntimeError("a download did not match its Dropbox content hash")
        part.replace(target)


# --- the steps ----------------------------------------------------------------

def _qa_root() -> Path:
    return repo_root() / "pipeline-output" / "qa"


def _runs() -> Path:
    return _qa_root() / "runs"


def _env() -> dict[str, str]:
    return read_env_file(repo_root() / ENV_FILENAME)


def _git(*args: str) -> str:
    return subprocess.run(["git", *args], cwd=repo_root(), check=True,
                          capture_output=True, text=True, encoding="utf-8").stdout


def _pipeline(*args: str) -> None:
    subprocess.run([sys.executable, "-m", "qa_pipeline", *args],
                   cwd=repo_root(), check=True)


def send_mail(subject: str, body: str) -> None:
    env = _env()
    message = EmailMessage()
    message["From"], message["To"] = env["EMAIL_FROM"], env["EMAIL_TO"]
    message["Subject"] = subject
    message.set_content(body)
    with smtplib.SMTP(env["EMAIL_SMTP"], SMTP_PORT, timeout=30) as smtp:
        smtp.send_message(message)


def sync_checkout() -> None:
    """Drop the last run's derived outputs and take origin's master."""
    dirty = _git("status", "--porcelain", "--", ".", ":(exclude)pipeline-output")
    if dirty:
        raise RuntimeError("the job checkout has changes outside pipeline-output")
    _git("fetch", "origin")
    _git("restore", "pipeline-output")
    _git("clean", "-fdq", "pipeline-output")
    _git("merge", "--ff-only", "origin/master")


def fetch() -> dict[str, Any]:
    """Bring new and updated .docx files down; -> what happened, for the report."""
    client = Dropbox(_env())
    shared = client.shared_folders()
    remote: list[RemoteFile] = []
    for year, name in QA_FOLDERS.items():
        matches = [f for f in shared if f["name"] == name]
        if len(matches) != 1:
            raise RuntimeError(f"expected one shared folder named {name!r}, "
                               f"found {len(matches)}")
        remote += client.list_docx(matches[0], year)

    qa_input = repo_root() / "original-data" / "QAs"
    local = {rel_posix(p, qa_input): content_hash(p) for p in iter_docx(qa_input)}
    plan = plan_fetch(remote, local)
    for file in plan.new + plan.changed:
        client.download(file, qa_input / file.rel)
    fetched = {
        "new": [f.rel for f in plan.new],
        "changed": [f.rel for f in plan.changed],
        "copies_skipped": plan.copies_skipped,
        "unconfigured_folders": sorted(
            {f["name"] for f in shared if QA_FOLDER_NAME.fullmatch(f["name"])}
            - set(QA_FOLDERS.values())),
    }
    write_json(_runs() / "fetch-latest.json", fetched)
    print(f"fetch done: {len(plan.new)} new, {len(plan.changed)} updated, "
          f"{plan.copies_skipped} copies skipped")
    return fetched


def process(*, strict: bool) -> None:
    """Stages 0, 1, 2 and 4. Strict stops on a model error instead of queueing it:
    Stage 4 treats a failed judgment as superseded, which would prune an answer."""
    fail = ["--fail-on-error"] if strict else []
    _pipeline("run", "--quiet")
    _pipeline("stage2", "--quiet", "--workers", "8", *fail)
    _pipeline("stage4", "--quiet", *fail)


def current_changes() -> Changes:
    """The working tree's Stage 4 records against the last published ones."""
    head = _git("show", "HEAD:pipeline-output/qa/stage4/documents.jsonl")
    before = [json.loads(line) for line in head.splitlines() if line.strip()]
    return diff_records(before, read_jsonl(_qa_root() / "stage4" / "documents.jsonl"))


def report() -> None:
    """Write the report beside the run manifests and mail its counts."""
    changes = current_changes()
    fetch_path = _runs() / "fetch-latest.json"
    fetched = read_json(fetch_path) if fetch_path.is_file() else {}
    after = read_jsonl(_qa_root() / "stage4" / "documents.jsonl")
    ids = {r["id"] for r in after}
    fetched["no_record"] = [rel for rel in fetched.get("new", []) if doc_id(rel) not in ids]
    reasons = load_reasons(_qa_root())
    evidence = load_evidence(_qa_root() / "stage2" / "runs" / "stage2_cache.jsonl", after)
    reasons, fetched["pii_not_served"] = split_unserved_pii(
        changes.new + changes.returned, reasons, evidence, _qa_root() / "stage0" / "text")

    runs = _runs().resolve()
    date = datetime.now().strftime("%Y-%m-%d")
    path = runs / f"weekly-report-{date}.md"
    path.touch(mode=0o600)
    write_text(path, render_report(changes, reasons, evidence, fetched,
                                   runs / "skip-thursday", date))
    print(f"report: {path}")

    body = alerts(changes, fetched) + summary_lines(changes, reasons, fetched)
    send_mail(f"dotFIT Q&A weekly report, {date}",
              "\n".join(body + ["", f"Report: {path}",
                                "Thursday's run publishes this unless it is skipped."]))


def publish(*, allow_retires: bool) -> None:
    """Index, commit and push what the week's records have become."""
    changes = current_changes()
    if len(changes.retired) > MAX_RETIRED and not allow_retires:
        raise RuntimeError(f"{len(changes.retired)} answers would leave search, "
                           f"over the limit of {MAX_RETIRED}")
    if changes.any():
        _pipeline("index", "--quiet", "--fail-on-error")
    _git("add", "pipeline-output")
    if not _git("status", "--porcelain", "--", "pipeline-output"):
        send_mail("dotFIT Q&A weekly: nothing to publish",
                  "Thursday's run found nothing new to publish.")
        return
    counts = (f"{len(changes.new)} new, {len(changes.changed)} changed, "
              f"{len(changes.retired)} leaving search, "
              f"{len(changes.returned)} returning.")
    _git("commit", "-m", f"Publish the week's Q&A records\n\n{counts}")
    _git("push", "origin", "HEAD:master")
    send_mail("dotFIT Q&A weekly: published", f"Published: {counts}")


def monday(_: argparse.Namespace) -> None:
    sync_checkout()
    fetch()
    process(strict=False)
    report()


def thursday(args: argparse.Namespace) -> None:
    skip = _runs() / "skip-thursday"
    if skip.exists():
        skip.unlink()
        send_mail("dotFIT Q&A weekly: publish skipped",
                  "This week's publish was skipped. Next Monday's report includes "
                  "everything still waiting.")
        return
    sync_checkout()
    process(strict=True)
    publish(allow_retires=args.allow_retires)


def main(argv: list[str] | None = None) -> int:
    configure_stdio()
    parser = argparse.ArgumentParser(prog="qa-weekly", description=__doc__.split("\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("monday", help="sync, fetch, process, report").set_defaults(run=monday)
    th = sub.add_parser("thursday", help="sync, process from cache, index, commit, push")
    th.add_argument("--allow-retires", action="store_true",
                    help=f"publish even if over {MAX_RETIRED} answers leave search")
    th.set_defaults(run=thursday)
    sub.add_parser("fetch", help="only the fetch").set_defaults(run=lambda _: fetch())
    sub.add_parser("process", help="only Stages 0, 1, 2 and 4").set_defaults(
        run=lambda _: process(strict=False))
    sub.add_parser("report", help="only the report and its mail").set_defaults(
        run=lambda _: report())
    args = parser.parse_args(argv)
    try:
        args.run(args)
    except Exception as e:  # noqa: BLE001 — mail the failure, the journal keeps the detail
        traceback.print_exc()
        where = ("qa-pipeline " + " ".join(e.cmd[3:])
                 if isinstance(e, subprocess.CalledProcessError)
                 and e.cmd[1:3] == ["-m", "qa_pipeline"] else type(e).__name__)
        try:
            send_mail(f"dotFIT Q&A weekly: {args.command} FAILED",
                      f"{args.command} stopped at: {where}\nNothing further ran. "
                      f"Logs: journalctl --user -u dotfit-qa-{args.command} -n 100")
        except OSError:
            traceback.print_exc()
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
