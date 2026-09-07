"""Verify the podcast ``archive.txt`` → YouTube mapping (open item 14).

`data/Suppbeast Podcast/archive.txt` is a bare list of ``youtube <video_id>``
lines with no titles and no stated ordering, so §7 step 4's citation URL
(``…as covered at 14:32 in *Creatine FAQs*``) cannot be built from it without
guessing. This resolves each id to its real video title through YouTube's
public **oEmbed** endpoint — title metadata only, no API key, no scraping —
and matches those titles against the episode filenames.

Result (run 2026-09-08): **47 ids, 47 episodes, 47 matched at Dice 1.00** —
every id resolved and every filename's token set is *identical* to its video's
title, because the downloader wrote the titles out verbatim and only
substituted the characters Windows forbids. The mapping was never ambiguous,
only unverified. (A ``wc -l`` of ``archive.txt`` reports 46: the file has no
trailing newline. It is 47 lines of content.)

That verdict is now frozen as ``PODCAST_VIDEO_IDS`` in ``podcast.py`` — the
pipeline must stay offline and deterministic, so it reads the constant, and
this script is the session record behind it. Re-run it when episodes are
added, and update the constant from the result.

Matching is deliberately conservative. Filenames carry fullwidth punctuation
from the download tool (``｜``, ``？``) and an ``| Ep 07`` suffix; titles carry
the real punctuation. Both sides are folded to alphanumeric-only lowercase
tokens and scored by token overlap (Dice), and only a match at or above
``MATCH_THRESHOLD`` counts — everything else is reported ``UNMATCHED`` for a
human. A wrong citation URL on a claims-sensitive assistant is worse than no
citation URL, which is why the index stamps ``null`` today.

This is a one-off verification tool, not part of the CLI: it makes network
calls, so it can never sit on the deterministic pipeline path. It writes
nothing — read the report, then decide (progress open item 14).

    uv run python scripts/podcast_archive_verify.py            # human report
    uv run python scripts/podcast_archive_verify.py --json     # machine
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

from qa_pipeline.io_utils import configure_stdio

OEMBED = "https://www.youtube.com/oembed"
WATCH = "https://www.youtube.com/watch?v={id}"
TIMEOUT = 15.0
MATCH_THRESHOLD = 0.60      # Dice coefficient over token sets

DEFAULT_AUDIO = Path("../data/Suppbeast Podcast")

# Filename noise that carries no signal for matching: the downloader's episode
# suffix and the fullwidth punctuation it substitutes for illegal characters.
_EP_SUFFIX = re.compile(r"\s*[|｜]\s*Ep\.?\s*\d+\s*$", re.IGNORECASE)
_STOPWORDS = {"the", "a", "an", "and", "or", "of", "to", "for", "with", "is",
              "are", "you", "your", "it", "in", "on", "ep"}


def tokens(text: str) -> set[str]:
    """Alphanumeric lowercase tokens, minus stopwords — punctuation-blind."""
    text = _EP_SUFFIX.sub("", text)
    return {t for t in re.findall(r"[a-z0-9]+", text.lower())
            if t not in _STOPWORDS} or {"∅"}


def dice(a: set[str], b: set[str]) -> float:
    return 2 * len(a & b) / (len(a) + len(b)) if (a or b) else 0.0


def read_archive(audio_root: Path) -> list[str]:
    path = audio_root / "archive.txt"
    if not path.is_file():
        raise SystemExit(f"archive.txt not found: {path}")
    ids = []
    for line in path.read_text(encoding="utf-8").splitlines():
        parts = line.split()
        if len(parts) == 2 and parts[0] == "youtube":
            ids.append(parts[1])
        elif line.strip():
            raise SystemExit(f"unexpected archive.txt line: {line!r}")
    return ids


def episode_titles(audio_root: Path) -> list[str]:
    return sorted(p.stem for p in audio_root.glob("*.mp3"))


def fetch_title(video_id: str) -> tuple[str | None, str]:
    """(title, status) for one id — never raises, the report carries failures."""
    url = f"{OEMBED}?" + urllib.parse.urlencode(
        {"url": WATCH.format(id=video_id), "format": "json"})
    try:
        with urllib.request.urlopen(url, timeout=TIMEOUT) as response:
            payload = json.loads(response.read().decode("utf-8"))
        return payload.get("title"), "ok"
    except urllib.error.HTTPError as e:
        # 401/403/404 from oEmbed = private, deleted or never public
        return None, f"http {e.code}"
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError, OSError) as e:
        return None, f"{type(e).__name__}"


def match(video_titles: dict[str, str], episodes: list[str]) -> list[dict]:
    """Greedy best-first match: strongest pairs claim each other first.

    Greedy rather than per-id argmax because two ids can both score highest
    against one episode ("#1 Expert Reacts…" and "#1 Nutrition Expert
    Reacts…"), and the loser must then be reported unmatched rather than
    silently stealing an episode that fits it worse.
    """
    scored = sorted(
        ((dice(tokens(title), tokens(episode)), vid, title, episode)
         for vid, title in video_titles.items()
         for episode in episodes),
        key=lambda row: (-row[0], row[1], row[3]))

    taken_ids: set[str] = set()
    taken_episodes: set[str] = set()
    rows: list[dict] = []
    for score, vid, title, episode in scored:
        if vid in taken_ids or episode in taken_episodes:
            continue
        if score < MATCH_THRESHOLD:
            continue
        taken_ids.add(vid)
        taken_episodes.add(episode)
        rows.append({"video_id": vid, "title": title, "episode": episode,
                     "score": round(score, 3), "status": "matched"})

    rows += [{"video_id": vid, "title": title, "episode": None, "score": None,
              "status": "unmatched_id"}
             for vid, title in sorted(video_titles.items())
             if vid not in taken_ids]
    rows += [{"video_id": None, "title": None, "episode": episode,
              "score": None, "status": "unmatched_episode"}
             for episode in episodes if episode not in taken_episodes]
    return rows


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--audio", default=str(DEFAULT_AUDIO),
                        help=f"podcast audio dir (default: {DEFAULT_AUDIO})")
    parser.add_argument("--json", action="store_true",
                        help="machine-readable output")
    args = parser.parse_args()
    configure_stdio()

    audio_root = Path(args.audio).resolve()
    ids = read_archive(audio_root)
    episodes = episode_titles(audio_root)

    resolved: dict[str, str] = {}
    failures: list[dict] = []
    for video_id in ids:
        title, status = fetch_title(video_id)
        if title:
            resolved[video_id] = title
        else:
            failures.append({"video_id": video_id, "status": status})

    rows = match(resolved, episodes)
    matched = [r for r in rows if r["status"] == "matched"]
    report = {
        "n_archive_ids": len(ids),
        "n_episodes": len(episodes),
        "n_resolved": len(resolved),
        "n_matched": len(matched),
        "n_unresolvable": len(failures),
        "match_threshold": MATCH_THRESHOLD,
        "coverage": round(len(matched) / len(episodes), 3) if episodes else 0.0,
        "failures": failures,
        "rows": rows,
    }

    if args.json:
        print(json.dumps(report, ensure_ascii=False, indent=1))
        return 0

    print(f"archive ids     {len(ids)}")
    print(f"episodes (mp3)  {len(episodes)}")
    print(f"titles resolved {len(resolved)}"
          + (f"  ({len(failures)} unresolvable)" if failures else ""))
    print(f"matched         {len(matched)}/{len(episodes)} episodes "
          f"({report['coverage'] * 100:.0f}% coverage, "
          f"Dice ≥ {MATCH_THRESHOLD})")
    print()
    for row in matched:
        print(f"  {row['score']:.2f}  {row['video_id']}  {row['episode']}")
    for row in rows:
        if row["status"] == "unmatched_id":
            print(f"  ----  {row['video_id']}  UNMATCHED ID: {row['title']!r}")
    for row in rows:
        if row["status"] == "unmatched_episode":
            print(f"  ----  (no video)     UNMATCHED EPISODE: {row['episode']}")
    for failure in failures:
        print(f"  ----  {failure['video_id']}  UNRESOLVABLE: {failure['status']}")
    print()
    print("Only a full, human-checked match justifies stamping citation_url; "
          "a partial one means some episodes cite linkless (open item 14).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
