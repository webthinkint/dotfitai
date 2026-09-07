"""Podcast transcript segmentation (plan §7 step 3).

Reads the fast-transcription JSONs produced by ``scripts/asr_pilot.py --all``
(word-level timestamps + diarization) and merges diarized turns into topic
chunks (~60–120 s / 150–250 words), each carrying ``episode_id``,
episode ``title`` (from the .mp3 filename, normalized — filenames contain
full-width characters ｜：？ that NFKC folds to ASCII), ``start``/``end``
(mm:ss), ``speakers``, and text.

Chunk text keeps one ``Speaker N: …`` line per turn (consecutive same-speaker
phrases merged) so the later speaker-map step (plan §7; per-episode LLM
attribution, blocked on the small chat deployment like Stage 2) can rewrite
the labels with a line-anchored substitution — and so clip segments (third-
party audio diarizes as its own speaker, e.g. Layne Norton in the David
Protein Bar episode) stay visibly distinct from host turns.

Phrases are atomic: one long API phrase can overshoot the caps, so maxima
(observed: 343 words / 130 s) sit just above the targets while medians
(251 words / 76 s) land in range — the same atomicity deal as PDSRG tables.

Determinism: greedy cut rule over phrases sorted by offset (the API already
emits them in order); outputs sorted by chunk id; no timestamps inside
artifacts (run manifests under ``runs/`` carry those). Paths in outputs are
relative to the audio root, never the cwd.

Not stamped here (index-layer concerns, plan §7 step 4): ``source_type``/
``authority``/``is_current``. The YouTube ``citation_url`` *is* owned here —
``PODCAST_VIDEO_IDS`` + ``citation_url`` at the foot of the module — because
the episode→video mapping is curation, and ``index_build`` only applies it.
"""

from __future__ import annotations

import re
import unicodedata
from pathlib import Path
from typing import Any

# §7 step 3 targets: ~60–120 s / 150–250 words per chunk.
TARGET_WORDS = 200
MAX_WORDS = 250
TARGET_SECONDS = 90.0
MAX_SECONDS = 120.0
# a soft cut additionally needs a "natural" boundary: a speaker change or a
# pause of at least this long. Hard caps cut anywhere (phrase boundary).
GAP_SECONDS = 3.0
# a trailing stub shorter than this merges into the previous chunk rather
# than indexing as a near-empty document (single-chunk episodes exempt).
MIN_WORDS = 50


def slugify(name: str) -> str:
    """Filesystem/URL-safe slug: NFKC, strip .mp3, casefold, dash-joined."""
    s = unicodedata.normalize("NFKC", name)
    if s.lower().endswith(".mp3"):
        s = s[:-4]
    s = s.casefold()
    s = re.sub(r"[^a-z0-9]+", "-", s).strip("-")
    return re.sub(r"-{2,}", "-", s)


def clean_title(filename: str) -> str:
    """Episode title from the .mp3 filename: NFKC folds the full-width
    characters (｜：？！…) to ASCII; collapse whitespace; strip .mp3."""
    s = unicodedata.normalize("NFKC", filename).strip()
    if s.lower().endswith(".mp3"):
        s = s[:-4]
    return re.sub(r"\s+", " ", s).strip()


def ms_to_mmss(ms: int) -> str:
    s = ms // 1000
    return f"{s // 60:02d}:{s % 60:02d}"


def phrase_words(phrase: dict) -> int:
    words = phrase.get("words")
    if isinstance(words, list):
        return len(words)
    return len(str(phrase.get("text") or "").split())


def phrase_end_ms(phrase: dict) -> int:
    return int(phrase.get("offsetMilliseconds", 0)) + int(
        phrase.get("durationMilliseconds", 0))


def segment_phrases(phrases: list[dict]) -> list[list[dict]]:
    """Greedy deterministic merge of API phrases into chunk phrase-groups."""
    ordered = sorted(phrases, key=lambda p: int(p.get("offsetMilliseconds", 0)))
    groups: list[list[dict]] = []
    current: list[dict] = []
    words = 0

    def flush() -> None:
        nonlocal current, words
        if current:
            groups.append(current)
        current = []
        words = 0

    for i, phrase in enumerate(ordered):
        current.append(phrase)
        words += phrase_words(phrase)
        start_ms = int(current[0].get("offsetMilliseconds", 0))
        duration_s = (phrase_end_ms(phrase) - start_ms) / 1000
        last = i == len(ordered) - 1
        if last:
            continue
        nxt = ordered[i + 1]
        gap_s = (int(nxt.get("offsetMilliseconds", 0)) - phrase_end_ms(phrase)) / 1000
        speaker_change = nxt.get("speaker") != phrase.get("speaker")
        if words >= MAX_WORDS or duration_s >= MAX_SECONDS:
            flush()
        elif (words >= TARGET_WORDS or duration_s >= TARGET_SECONDS) and (
            speaker_change or gap_s >= GAP_SECONDS
        ):
            flush()
    flush()

    # fold a trailing stub into its predecessor (keeps near-empty tail
    # chunks out of the index); a lone chunk is the whole episode — keep it
    if len(groups) > 1 and sum(phrase_words(p) for p in groups[-1]) < MIN_WORDS:
        groups[-2].extend(groups[-1])
        groups.pop()
    return groups


def build_chunk(slug: str, title: str, source_file: str, index: int,
                group: list[dict]) -> dict:
    start_ms = int(group[0].get("offsetMilliseconds", 0))
    end_ms = max(phrase_end_ms(p) for p in group)
    speakers = sorted({p["speaker"] for p in group if p.get("speaker") is not None})
    # merge consecutive same-speaker phrases into turn lines; the
    # line-anchored "Speaker N:" prefix is the speaker-map rewrite contract
    turns: list[list[str]] = []
    for p in group:
        label = f"Speaker {p['speaker']}" if p.get("speaker") is not None else "Speaker ?"
        text = str(p.get("text") or "").strip()
        if turns and turns[-1][0] == label:
            turns[-1][1] += " " + text
        else:
            turns.append([label, text])
    text = "\n".join(f"{label}: {body}".rstrip() for label, body in turns)
    return {
        "id": f"{slug}-{index:03d}",
        "episode_id": slug,
        "episode_title": title,
        "source_file": source_file,
        "chunk_index": index,
        "start": ms_to_mmss(start_ms),
        "end": ms_to_mmss(end_ms),
        "start_ms": start_ms,
        "end_ms": end_ms,
        "duration_s": round((end_ms - start_ms) / 1000, 1),
        "speakers": speakers,
        "n_words": sum(phrase_words(p) for p in group),
        "text": text,
    }


def resolve_titles(audio_root: Path) -> dict[str, str]:
    """Map transcript slug -> .mp3 filename (input-relative POSIX).

    Slugs must resolve 1:1 — a collision or a transcript with no audio twin
    raises rather than emitting misattributed chunks.
    """
    from .io_utils import rel_posix  # local import: keeps module import-light

    mapping: dict[str, str] = {}
    for mp3 in sorted(audio_root.rglob("*.mp3")):
        if not mp3.is_file():
            continue
        slug = slugify(mp3.name)
        if slug in mapping:
            raise ValueError(
                f"slug collision: {mp3.name!r} and {mapping[slug]!r} "
                f"both slugify to {slug!r}")
        mapping[slug] = rel_posix(mp3, audio_root)
    return mapping


def segment_episode(slug: str, title: str, source_file: str,
                    payload: dict) -> list[dict]:
    phrases = payload.get("phrases", [])
    return [build_chunk(slug, title, source_file, i, group)
            for i, group in enumerate(segment_phrases(phrases))]


def summarize(chunks: list[dict]) -> dict[str, Any]:
    episodes: dict[str, dict] = {}
    for c in chunks:
        ep = episodes.setdefault(c["episode_id"], {
            "episode_id": c["episode_id"], "episode_title": c["episode_title"],
            "n_segments": 0, "n_words": 0, "duration_s": 0.0})
        ep["n_segments"] += 1
        ep["n_words"] += c["n_words"]
        ep["duration_s"] = round(ep["duration_s"] + c["duration_s"], 1)
    return {
        "n_episodes": len(episodes),
        "n_segments": len(chunks),
        "n_words": sum(c["n_words"] for c in chunks),
        "episodes": sorted(episodes.values(), key=lambda e: e["episode_id"]),
    }


# --- §7 step 4: YouTube citation URLs (progress open item 14) ----------------

# Episode .mp3 stem -> YouTube video id, verified 2026-09-08 against the real
# video titles (`scripts/podcast_archive_verify.py`, which resolves each id
# through YouTube's public oEmbed endpoint): 47 ids, 47 episodes, 47 matched at
# Dice 1.00 — every filename's token set is *identical* to its video's title,
# because the downloader wrote the titles out verbatim and only substituted
# characters Windows forbids (｜ ？ ：). The mapping was never ambiguous, only
# unverified, which is why the index stamped `citation_url: null` until now.
#
# It lives here as a constant rather than being read from `archive.txt` for two
# reasons: `archive.txt` carries ids in an order that means nothing (verifying
# it needs the network, and the pipeline is offline and deterministic), and
# curation lives in code (AGENTS.md). Re-run the script when episodes are
# added; an episode missing from this table raises rather than citing linkless.
PODCAST_VIDEO_IDS: dict[str, str] = {
    "#1 Expert Reacts to Nutrition and Fitness Advice":
        "VKSaDuASiWo",
    "#1 Nutrition Expert Reacts to Controversial Nutrition Advice":
        "M2k0VtvA5ek",
    "About Neal & Zane Spruce ｜ Ep 00":
        "uL6pUiUoll0",
    "Amino Acids Supplementation Benefits ｜ EAAs and BCAAs":
        "xZ-dr-BO1SQ",
    "Are Supplements WORTH It？ ｜ Ep 07":
        "q5n5MqhVIdg",
    "Carbs： The Most Misunderstood Nutrient":
        "FH52uRz-vek",
    "Creatine FAQs： Hair Loss, Caffeine, Dosing by Weight, HCL vs Monohydrate & More":
        "KWkLf0w9zJA",
    "DIETARY FIBER： Gut Health, Foods, Weight Control and More!":
        "XK4mKWAlcOs",
    "David Protein Bar FAILED Consumer Labs Test ｜ Layne Norton & Neal Spruce React":
        "zIOrIa575I8",
    "Doctor Says Protein Powder is BAD and More! ｜ Fitness Expert Reacts":
        "X9jv8V9I-R0",
    "EAAs vs BCAAs： What Actually Builds Muscle (From the Guy Who Formulated the First EAA)":
        "BUvkfHHSD5Q",
    "Eat Like A Pro Athlete ｜ NFL Edition ｜ Ep 06":
        "Yd9FKGXgMus",
    "Every Diet Works Because of Calories In vs Calories Out":
        "wvA5nfao4Vg",
    "Everything You Need to Know About Macronutrients From an Expert ｜ Ep 01":
        "1WakPw7Xv1w",
    "Expert Explains KEYS to LOSE FAT! ｜ Ep 04":
        "hpQixrxQOPs",
    "Fully Understanding Micronutrients ｜ Ep 02":
        "-j5CvQkQBd4",
    "GLP-1 Weight Loss Done RIGHT： Ozempic, Wegovy & The Real Protocol for Long-Term Success":
        "YihK_adXHag",
    "How to Avoid Holiday Weight Gain (Without Skipping the Foods You Love)":
        "SYUW9FC-tlA",
    "How to Break a Muscle Building Plateau":
        "x-9onA8ik8I",
    "How to Break a Weight Loss Plateau":
        "Ab3KlSQd9P0",
    "How to Build the Most Muscle (Naturally)":
        "T4w8HKcp-VY",
    "How to LOSE Fat and GAIN Muscle AT THE SAME TIME!":
        "5pNsm9h0G5s",
    "I Asked a Nutrition Expert Which Supplements Are Actually Worth It!":
        "-2SmKpkDOW8",
    "Improve Muscle and Brain ｜ The Complete Guide to Creatine ｜ Ep 12":
        "t9UCe9trViQ",
    "NEW STUDY： CREATINE DOESN'T HELP BUILD MUSCLE ｜ Nutrition Expert Explains":
        "dLLADRxc9zs",
    "Nutirition Expert on： Clean Eating, HRT, Fat Loss Exercise, Keto Diet and Vegan Diet!":
        "KagJdLwziDI",
    "Nutrition Expert REACTS to Dr. Berg, Doctor Mike, Jay Cutler & more!":
        "8KWE4nJSY-k",
    "Nutrition Expert Reacts to Fitness Advice! ｜ Mike Israetel, Gary Brecka and more!":
        "tjqvUdijEsY",
    "Nutrition Expert Responds to Calories In vs Calories Out Haters ｜ Ep 05":
        "6F0xDnTNxC0",
    "Nutrition Expert： Optimal Nutrition for Kids to Reach Maximum Potential":
        "fy6SCYjYjxQ",
    "Old School Bodybuilding vs Science Based Lifting":
        "cJw2KBZPSeY",
    "Over 40 Fitness Blueprint： Anti-Aging Nutrition Plan to Stay Strong, Lean & Young":
        "aXq6KLTLY4I",
    "Over 70% of Protein Podwers Contain High Lead？! Expert Explains":
        "3PHafKAqbCk",
    "Protein 101 with a Nutrition Expert":
        "XsHCceudMk4",
    "Stop BUYING Supplements HERE! ｜ Mass Market ｜ Ep 10":
        "if7oLZ8IhPo",
    "Study Shows Endurance Athletes No Longer Need to Carb Load？":
        "XfX1zw0i1N4",
    "Supplement Protocols for OPTIMAL Health ｜ Ep 08":
        "xaF27bTBv7U",
    "THE PROTEIN CRAZE... has it gone too far？":
        "UV-Pp5GRdiA",
    "The BEST Immune System Booster Protocol ｜ Ep. 11":
        "G8KLQLKqH7I",
    "The Benefits of Protein Powders ｜ Whey, Soy, Collagen, Egg White etc.":
        "0mP76qPYA5w",
    "The Truth about Creatine Side Effects ｜ Nutrition Expert Explains Science":
        "eStY3VjfNhU",
    "Top 10 Missing Vitamins and Minerals ｜ Ep 03":
        "lOlxdGw7CSU",
    "Top Nutrition Expert Reacts to RFK Jr. New Food Pyramid":
        "L-JRcHbAtjE",
    "Understanding Aging and How to Beat It ｜ Ep 09":
        "UrY6m5fGmhY",
    "Vacation Weight Gain： How Fast You Lose Muscle & Gain Fat (The Truth)":
        "aFvFif4fCDA",
    "What Neal Spruce Takes Every Day at 73 ｜ A 40-Year Fitness Expert's Full Supplement Stack":
        "w5pK8g8zLvs",
    "Why We Started a Fitness Channel With No Agenda":
        "0XfmdMZUvqc",
}


def citation_url(source_file: str, start_ms: int,
                 video_ids: dict[str, str] | None = None) -> str:
    """Deep link to the moment a segment starts (§7 step 4).

    ``…as covered at 14:32 in *Creatine FAQs*`` is only useful if the link
    lands there, so the timestamp rides on the URL. A stem this table does not
    know **raises**: a podcast document with a silently absent link is exactly
    the failure open item 14 describes, and it must not be reintroduced by a
    new episode landing in the corpus.
    """
    table = PODCAST_VIDEO_IDS if video_ids is None else video_ids
    stem = source_file[:-4] if source_file.lower().endswith(".mp3") else source_file
    video_id = table.get(stem)
    if video_id is None:
        raise ValueError(
            f"no verified YouTube id for episode {stem!r} — re-run "
            "scripts/podcast_archive_verify.py and update PODCAST_VIDEO_IDS")
    return f"https://www.youtube.com/watch?v={video_id}&t={max(start_ms, 0) // 1000}s"
