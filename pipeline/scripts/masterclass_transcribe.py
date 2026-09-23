"""Batch ASR for the dotFIT Masterclass corpus (data/Masterclass/videos).

Sister sweep to scripts/asr_pilot.py --all (the Suppbeast Podcast §7 run):
same Azure fast-transcription call (reused verbatim — locales, diarization,
phrase list, profanity masking, retries), pointed at the Masterclass webinar
audio instead. Triggered by the 2026-09-22 mp3-vs-PDF probe: the audio is the
only source of the live Q&A and practical dosing guidance; analysis of the
transcripts happens later, this sweep just produces them.

Conventions inherited from the podcast sweep, unchanged:

- Raw transcripts are the provenance record: ``transcripts/*.json`` is what
  the API returned, verbatim-ASR, and the ``.txt`` renderings stay verbatim
  too. Corrections (if any are ever attested) apply later at a chunking
  chokepoint, never here.
- Filenames carry the YouTube video id in ``[…]``; ``podcast.slugify`` NFKC-
  folds the full-width characters and keeps the id in the slug, so a future
  ``citation_url`` can be rebuilt from the artifact name alone.
- Resumable: a slug with an existing ``.json`` is skipped (``--force``
  re-transcribes). One bad file must not kill the sweep; failures are
  collected and reported, exit 1 if any.

Outputs:
    processed/masterclass/transcripts/<slug>.json   raw API payload
    processed/masterclass/transcripts/<slug>.txt    readable Speaker/mm:ss

Usage (from pipeline/):
    uv run scripts/masterclass_transcribe.py            # all, resumable
    uv run scripts/masterclass_transcribe.py --workers 2 --force
Cost: one Speech call per ~1h episode (~48h of audio in total).
"""

from __future__ import annotations

import argparse
import json
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from asr_pilot import render_readable, transcribe  # noqa: E402

from qa_pipeline.azure_config import (  # noqa: E402
    AzureConfigError, REQUIRE_SPEECH, load_azure_config,
)
from qa_pipeline.podcast import ms_to_mmss, slugify  # noqa: E402


def repo_root() -> Path:
    return Path(__file__).resolve().parents[2]


def audio_dir() -> Path:
    return repo_root() / "data" / "Masterclass" / "videos"


def transcribe_one(audio: Path, out_dir: Path, endpoint: str, key: str,
                   max_speakers: int, lock: threading.Lock,
                   force: bool) -> str:
    """One episode -> .json + .txt; returns a one-line outcome."""
    slug = slugify(audio.name)
    raw_path = out_dir / f"{slug}.json"
    txt_path = out_dir / f"{slug}.txt"
    if raw_path.exists() and not force:
        return f"SKIP  {audio.name} (exists)"
    t0 = time.monotonic()
    try:
        payload = transcribe(audio, endpoint, key, max_speakers)
    except RuntimeError as e:
        return f"FAIL  {audio.name}: {e}"
    elapsed = time.monotonic() - t0
    raw_path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n",
                        encoding="utf-8")
    txt_path.write_text(render_readable(payload, audio.name), encoding="utf-8")
    n = len(payload.get("phrases", []))
    line = (f"OK    {audio.name} "
            f"({ms_to_mmss(payload.get('durationMilliseconds', 0))}, "
            f"{n} phrases, {elapsed:.0f}s api)")
    with lock:
        print(line, flush=True)
    return line


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    parser = argparse.ArgumentParser(
        description="Transcribe all dotFIT Masterclass webinars (resumable)")
    parser.add_argument("--max-speakers", type=int, default=3,
                        help="host + moderator + guest covers the corpus")
    parser.add_argument("--workers", type=int, default=2,
                        help="parallel uploads — keep low: the API "
                        "rate-limits under concurrency (asr_pilot ruling)")
    parser.add_argument("--force", action="store_true",
                        help="re-transcribe even when the slug .json exists")
    args = parser.parse_args()

    episodes = sorted(audio_dir().glob("*.mp3"))
    out_dir = repo_root() / "processed" / "masterclass" / "transcripts"
    out_dir.mkdir(parents=True, exist_ok=True)
    print(f"episodes: {len(episodes)}, workers={args.workers}, out={out_dir}",
          flush=True)

    cfg = load_azure_config(require=REQUIRE_SPEECH)

    lock = threading.Lock()
    outcomes: list[str] = []
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        futs = {pool.submit(transcribe_one, ep, out_dir, cfg.speech_endpoint,
                            cfg.speech_key, args.max_speakers, lock,
                            args.force): ep
                for ep in episodes}
        for fut in as_completed(futs):
            try:
                outcomes.append(fut.result())
            except Exception as e:  # noqa: BLE001 — one bad file must not kill the sweep
                outcomes.append(f"FAIL  {futs[fut].name}: {type(e).__name__}: {e}")

    ok = sum(o.startswith("OK") for o in outcomes)
    skipped = sum(o.startswith("SKIP") for o in outcomes)
    failed = [o for o in outcomes if o.startswith("FAIL")]
    print(f"\ndone: {ok} transcribed, {skipped} skipped, {len(failed)} failed",
          flush=True)
    for o in failed:
        print(o, flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except AzureConfigError as e:
        print(f"FAIL: {e}", file=sys.stderr)
        raise SystemExit(1)
