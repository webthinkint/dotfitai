"""Podcast ASR pilot (plan §7) — transcribe ONE episode for manual inspection.

One-off tool in the spirit of ``scripts/embedding_smoke.py``: proves the
Speech endpoint + key + fast-transcription shape end-to-end (inline audio
upload, so no blob storage needed) before we run all 47 episodes.

Uses the fast-transcription REST API
(``POST {speech_endpoint}/speechtotext/transcriptions:transcribe``) with
``locales=["en-US"]``, diarization on, word-level timestamps (always
returned), and a dotFIT phrase list (product names + supplement jargon —
the plan's predictable failure mode).

Outputs (no timestamps inside — deterministic for a fixed API version):
    processed/podcasts/pilot/<slug>.json   raw API response
    processed/podcasts/pilot/<slug>.txt    readable Speaker/mm:ss transcript

Usage (from pipeline/):
    uv run scripts/asr_pilot.py --list
    uv run scripts/asr_pilot.py                       # default pilot episode
    uv run scripts/asr_pilot.py --episode "creatine faqs"

Never echoes key material. Exits non-zero on any failure.
"""

from __future__ import annotations

import argparse
import io
import json
import re
import sys
import threading
import time
import unicodedata
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

from qa_pipeline.azure_config import (
    AzureConfigError, REQUIRE_SPEECH, load_azure_config,
)
from qa_pipeline.podcast import ms_to_mmss, slugify

API_VERSION = "2025-10-15"
# Default pilot: product-dense, moderate size (~20MB) — exercises the
# phrase list (creatine/HCL/monohydrate) and diarization (host + guest).
DEFAULT_EPISODE_SUBSTR = "creatine faqs"

# Plan §7 phrase list: dotFIT product vocabulary + the supplement jargon the
# base model is expected to miss. Curation lives here (versioned in code).
PHRASE_LIST: list[str] = [
    # brand / hosts
    "dotFIT", "Suppbeast", "Neal Spruce", "Zane Spruce",
    # product families (products.json longnames, variant suffixes stripped)
    "WheySmooth", "AminoFormula", "Creatine Monohydrate", "Creatine Complex",
    "NO7 PreWorkout", "First String", "FirstString", "Alln1 SuperBlend",
    "ThermAccel", "CarbRepel", "WeightLoss and LiverSupport",
    "Active MV", "Women's MV", "Over 50 MV",
    "Omega-3 Fish Oil", "SuperOmega", "Antioxidant", "Probiotics",
    "CollagenComplex", "GlutamineComplex", "MuscleDefender",
    "LeanMeal", "LeanMR", "Digestive Enzymes", "Electrolytes",
    "Brain Health", "Calcium Complex", "Plant Protein", "dotBAR",
    "SleepAid", "Vitamin D-3", "Workout Extreme",
    "Pre and Post Workout Formula", "Recover and Build",
    # legacy names still spoken on air
    "NO7 Rage", "ExtremeCreatineXXXL", "SuperiorAntioxidant",
    # abbreviations actually spoken
    "EAA", "EAAs", "BCAA", "BCAAs",
    # supplement jargon (the predictable failure mode)
    "monohydrate", "creatine HCL", "Creapure", "micronized creatine",
    "loading phase", "maintenance dose", "DHT", "caffeine",
    "beta-alanine", "beta alanine", "citrulline", "nitric oxide",
    "ATP", "leucine", "isoleucine", "valine",
    "whey protein isolate", "third-party tested", "NSF Certified",
    "water retention",
]


def repo_root() -> Path:
    return Path(__file__).resolve().parents[2]


def podcast_dir() -> Path:
    return repo_root() / "data" / "Suppbeast Podcast"


def find_episode(substr: str) -> Path:
    needle = unicodedata.normalize("NFKC", substr).casefold()
    cands = sorted(p for p in podcast_dir().glob("*.mp3"))
    hits = [p for p in cands
            if needle in unicodedata.normalize("NFKC", p.name).casefold()]
    if not hits:
        raise SystemExit(f"no episode matches {substr!r} (try --list)")
    if len(hits) > 1:
        names = "\n".join(f"  {p.name}" for p in hits)
        raise SystemExit(f"{len(hits)} episodes match {substr!r}, be specific:\n{names}")
    return hits[0]


def render_readable(payload: dict, episode: str) -> str:
    phrases = payload.get("phrases", [])
    speakers = sorted({p.get("speaker") for p in phrases
                       if p.get("speaker") is not None})
    total_ms = payload.get("durationMilliseconds", 0)
    lines = [
        f"Episode : {episode}",
        f"Duration: {ms_to_mmss(total_ms)} ({total_ms} ms)",
        f"Phrases : {len(phrases)}",
        f"Speakers: {speakers if speakers else 'n/a (no diarization)'}",
        "",
    ]
    for p in phrases:
        tag = f"Speaker {p['speaker']}" if p.get("speaker") is not None else "??"
        lines.append(f"[{ms_to_mmss(p.get('offsetMilliseconds', 0))}] {tag}: {p.get('text', '')}")
    return "\n".join(lines) + "\n"


RETRYABLE_HTTP = (429, 500, 502, 503, 504)
RETRY_BACKOFFS = (2, 4, 8, 16, 32)  # per the fast-transcription docs


def transcribe(audio: Path, endpoint: str, key: str, max_speakers: int) -> dict:
    definition = {
        "locales": ["en-US"],
        "diarization": {"enabled": True, "maxSpeakers": max_speakers},
        "phraseList": {"phrases": PHRASE_LIST},
        "profanityFilterMode": "Masked",
    }
    boundary = "----asr-pilot-boundary"
    buf = io.BytesIO()
    def field(name: str, value: str, ctype: str) -> None:
        buf.write(f"--{boundary}\r\n".encode())
        buf.write(f'Content-Disposition: form-data; name="{name}"\r\n'.encode())
        buf.write(f"Content-Type: {ctype}\r\n\r\n".encode())
        buf.write(value.encode())
        buf.write(b"\r\n")
    field("definition", json.dumps(definition), "application/json")
    buf.write(f"--{boundary}\r\n".encode())
    buf.write('Content-Disposition: form-data; name="audio"; '
              f'filename="{audio.name}"\r\n'.encode())
    buf.write(b"Content-Type: audio/mpeg\r\n\r\n")
    buf.write(audio.read_bytes())
    buf.write(f"\r\n--{boundary}--\r\n".encode())

    url = endpoint.rstrip("/") + f"/speechtotext/transcriptions:transcribe?api-version={API_VERSION}"
    req = urllib.request.Request(
        url, data=buf.getvalue(),
        headers={"Ocp-Apim-Subscription-Key": key,
                 "Content-Type": f"multipart/form-data; boundary={boundary}"},
        method="POST",
    )
    # 20–50 MB upload + synchronous transcription: allow up to 10 min.
    # Retry transient/rate-limit failures with exponential backoff (the
    # file bytes are re-read per attempt, so there is no stream to reset).
    # 400/401/422 are client errors — fail fast, no retry.
    last_err = "unknown"
    for attempt, backoff in enumerate([0, *RETRY_BACKOFFS]):
        if backoff:
            time.sleep(backoff)
        try:
            with urllib.request.urlopen(req, timeout=600) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            body = e.read().decode("utf-8", "replace")[:500]
            last_err = f"HTTP {e.code}: {body}"
            if e.code not in RETRYABLE_HTTP:
                break
        except (urllib.error.URLError, TimeoutError, OSError, ConnectionError) as e:
            last_err = f"{type(e).__name__}: {e}"
    raise RuntimeError(f"{audio.name}: {last_err}")


def transcribe_one(audio: Path, out_dir: Path, endpoint: str, key: str,
                   max_speakers: int, lock: threading.Lock, force: bool) -> str:
    """Transcribe one episode; returns a one-line outcome for the summary."""
    slug = slugify(audio.name)
    raw_path = out_dir / f"{slug}.json"
    txt_path = out_dir / f"{slug}.txt"
    if raw_path.exists() and not force:
        return f"SKIP  {audio.name} (exists)"
    try:
        payload = transcribe(audio, endpoint, key, max_speakers)
    except RuntimeError as e:
        return f"FAIL  {audio.name}: {e}"
    raw_path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n",
                        encoding="utf-8")
    txt_path.write_text(render_readable(payload, audio.name), encoding="utf-8")
    n = len(payload.get("phrases", []))
    dur = ms_to_mmss(payload.get("durationMilliseconds", 0))
    line = f"OK    {audio.name} ({dur}, {n} phrases)"
    with lock:
        print(line, flush=True)
    return line


def run_batch(args: argparse.Namespace, cfg) -> int:
    out_dir = (Path(args.out) if args.out
               else repo_root() / "processed" / "podcasts" / "transcripts")
    out_dir.mkdir(parents=True, exist_ok=True)
    episodes = sorted(podcast_dir().glob("*.mp3"))
    print(f"episodes: {len(episodes)}, workers={args.workers}, out={out_dir}")
    lock = threading.Lock()
    outcomes: list[str] = []
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        futs = {pool.submit(transcribe_one, ep, out_dir, cfg.speech_endpoint,
                            cfg.speech_key, args.max_speakers, lock, args.force): ep
                for ep in episodes}
        for fut in as_completed(futs):
            try:
                outcomes.append(fut.result())
            except Exception as e:  # noqa: BLE001 — one bad file must not kill the sweep
                outcomes.append(f"FAIL  {futs[fut].name}: {type(e).__name__}: {e}")
    ok = sum(o.startswith("OK") for o in outcomes)
    skipped = sum(o.startswith("SKIP") for o in outcomes)
    failed = [o for o in outcomes if o.startswith("FAIL")]
    print(f"\ndone: {ok} transcribed, {skipped} skipped, {len(failed)} failed")
    for o in failed:
        print(o)
    return 1 if failed else 0


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    parser = argparse.ArgumentParser(description="Transcribe one podcast episode (pilot)")
    parser.add_argument("--episode", default=DEFAULT_EPISODE_SUBSTR,
                        help="case-insensitive substring of the .mp3 name")
    parser.add_argument("--list", action="store_true",
                        help="list episodes with sizes and exit")
    parser.add_argument("--max-speakers", type=int, default=3)
    parser.add_argument("--out", default=None,
                        help="output dir (default: processed/podcasts/pilot,"
                        " or transcripts/ with --all)")
    parser.add_argument("--all", action="store_true",
                        help="transcribe all 47 episodes (skips slugs with"
                        " existing .json; full §7 ASR sweep)")
    parser.add_argument("--workers", type=int, default=2,
                        help="parallel uploads with --all (keep low: the API"
                        " rate-limits under concurrency)")
    parser.add_argument("--force", action="store_true",
                        help="re-transcribe even when the slug .json exists")
    args = parser.parse_args()

    if args.list:
        for p in sorted(podcast_dir().glob("*.mp3")):
            mb = p.stat().st_size / 1e6
            print(f"{mb:5.1f}MB  {p.name}")
        return 0

    try:
        cfg = load_azure_config(require=REQUIRE_SPEECH)
    except AzureConfigError as e:
        print(f"FAIL: {e}", file=sys.stderr)
        return 1

    if args.all:
        return run_batch(args, cfg)

    episode = find_episode(args.episode)
    out_dir = Path(args.out) if args.out else repo_root() / "processed" / "podcasts" / "pilot"
    out_dir.mkdir(parents=True, exist_ok=True)
    slug = slugify(episode.name)
    print(f"episode : {episode.name} ({episode.stat().st_size / 1e6:.1f} MB)")
    print(f"slug    : {slug}")
    print(f"phrases : {len(PHRASE_LIST)} phrase-list entries, "
          f"maxSpeakers={args.max_speakers}")
    print("uploading + transcribing (synchronous, may take minutes)…", flush=True)

    payload = transcribe(episode, cfg.speech_endpoint, cfg.speech_key, args.max_speakers)

    raw_path = out_dir / f"{slug}.json"
    txt_path = out_dir / f"{slug}.txt"
    raw_path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n",
                        encoding="utf-8")
    readable = render_readable(payload, episode.name)
    txt_path.write_text(readable, encoding="utf-8")

    # inspection summary: duration, speakers, phrase-list hit check
    phrases = payload.get("phrases", [])
    speakers = sorted({p.get("speaker") for p in phrases if p.get("speaker") is not None})
    full = " ".join(p.get("text", "") for p in phrases)
    hits = sorted({ph for ph in PHRASE_LIST
                   if re.search(rf"(?<![A-Za-z0-9]){re.escape(ph)}(?![A-Za-z0-9])", full, re.IGNORECASE)})
    print(f"duration: {ms_to_mmss(payload.get('durationMilliseconds', 0))} "
          f"({len(phrases)} phrases, speakers={speakers or 'n/a'})")
    print(f"phrase-list hits ({len(hits)}/{len(PHRASE_LIST)}): {', '.join(hits) or '—'}")
    print(f"wrote   : {raw_path.relative_to(repo_root())}")
    print(f"wrote   : {txt_path.relative_to(repo_root())}")
    print()
    print("--- transcript head (first 2000 chars) ---")
    body = "\n".join(readable.splitlines()[5:9])[:2000]
    print(body)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
