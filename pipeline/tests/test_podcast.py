"""Podcast segmentation tests (plan §7 step 3) — synthetic fixtures only."""

from __future__ import annotations

import json
import re

import pytest

from qa_pipeline.podcast import (
    MAX_WORDS, TARGET_WORDS, build_chunk, clean_title, ms_to_mmss,
    resolve_titles, segment_episode, segment_phrases, slugify, summarize,
)


def _phrase(offset_ms: int, n_words: int, speaker: int = 1,
            dur_ms: int = 4000) -> dict:
    words = [{"text": f"w{i}", "offsetMilliseconds": offset_ms + i * 10,
              "durationMilliseconds": 10} for i in range(n_words)]
    return {"offsetMilliseconds": offset_ms, "durationMilliseconds": dur_ms,
            "text": " ".join(f"w{i}" for i in range(n_words)),
            "words": words, "speaker": speaker, "locale": "en-US",
            "confidence": 0.9}


def test_slugify_fullwidth_and_suffix() -> None:
    assert slugify("Creatine FAQs： Hair Loss & More.mp3") == \
        "creatine-faqs-hair-loss-more"
    assert slugify("About Neal ｜ Ep 00.mp3") == "about-neal-ep-00"
    assert slugify("A  B--C.mp3") == "a-b-c"


def test_clean_title_folds_fullwidth() -> None:
    assert clean_title("About Neal ｜ Ep 00.mp3") == "About Neal | Ep 00"
    assert clean_title("Carbs： The Nutrient？.mp3") == "Carbs: The Nutrient?"
    assert clean_title("  Spaced   Out .mp3  ") == "Spaced Out"


def test_ms_to_mmss_long_episode() -> None:
    assert ms_to_mmss(0) == "00:00"
    assert ms_to_mmss(65_000) == "01:05"
    assert ms_to_mmss(5_093_000) == "84:53"


def test_no_cut_before_target() -> None:
    phrases = [_phrase(i * 5000, 10, speaker=i % 2) for i in range(5)]
    assert segment_phrases(phrases) == [phrases]


def test_soft_cut_at_speaker_change_after_target() -> None:
    phrases = [_phrase(0, TARGET_WORDS, speaker=1),
               _phrase(60_000, 30, speaker=2),
               _phrase(65_000, 30, speaker=2)]
    groups = segment_phrases(phrases)
    assert len(groups) == 2
    assert groups[0] == phrases[:1]
    assert groups[1] == phrases[1:]


def test_soft_cut_needs_boundary_before_is_forced() -> None:
    # past target words but same speaker and no pause: keeps accumulating
    phrases = [_phrase(i * 4000, 60, speaker=1) for i in range(4)]
    assert segment_phrases(phrases) == [phrases]


def test_hard_cut_at_max_words() -> None:
    phrases = [_phrase(0, MAX_WORDS + 10, speaker=1),
               _phrase(60_000, 60, speaker=1)]
    groups = segment_phrases(phrases)
    assert len(groups) == 2
    assert groups[0] == phrases[:1]


def test_trailing_stub_merges() -> None:
    phrases = [_phrase(0, TARGET_WORDS, speaker=1),
               _phrase(120_000, 10, speaker=2),
               _phrase(125_000, 5, speaker=2)]
    groups = segment_phrases(phrases)
    assert len(groups) == 1
    assert groups[0] == phrases


def test_single_chunk_episode_kept_despite_stub() -> None:
    phrases = [_phrase(0, 5, speaker=1)]
    assert segment_phrases(phrases) == [phrases]


def test_empty_phrases() -> None:
    assert segment_phrases([]) == []


def test_build_chunk_turn_lines_are_speaker_map_rewritable() -> None:
    group = [_phrase(61_000, 3, speaker=2), _phrase(66_000, 3, speaker=2),
             _phrase(71_000, 3, speaker=1)]
    chunk = build_chunk("ep", "Ep Title", "ep.mp3", 4, group)
    assert chunk["id"] == "ep-004"
    assert chunk["start"] == "01:01"
    assert chunk["speakers"] == [1, 2]
    assert chunk["n_words"] == 9
    lines = chunk["text"].split("\n")
    assert len(lines) == 2  # same-speaker phrases merged into one turn
    assert all(re.match(r"^Speaker \d:", line) for line in lines)
    # the speaker-map rewrite contract: line-anchored substitution
    mapped = re.sub(r"^Speaker 2:", "Neal Spruce:", chunk["text"],
                    flags=re.M)
    assert mapped.split("\n")[0].startswith("Neal Spruce:")


def test_resolve_titles_collision_raises(tmp_path) -> None:
    (tmp_path / "A - B.mp3").write_bytes(b"x")
    (tmp_path / "A – B.mp3").write_bytes(b"x")  # en dash also slugifies to "-"
    with pytest.raises(ValueError, match="slug collision"):
        resolve_titles(tmp_path)


def test_segment_episode_ids_sorted_and_summarize() -> None:
    payload = {"phrases": [_phrase(0, TARGET_WORDS, speaker=1),
                           _phrase(200_000, TARGET_WORDS, speaker=2)]}
    chunks = segment_episode("ep", "Ep", "ep.mp3", payload)
    assert [c["id"] for c in chunks] == ["ep-000", "ep-001"]
    summary = summarize(chunks)
    assert summary["n_episodes"] == 1
    assert summary["n_segments"] == 2
    assert summary["n_words"] == 2 * TARGET_WORDS


def test_determinism_byte_identical() -> None:
    payload = {"phrases": [_phrase(i * 5000, 20, speaker=(i % 3) + 1)
                           for i in range(30)]}
    first = json.dumps(segment_episode("ep", "Ep", "ep.mp3", payload),
                       ensure_ascii=False)
    second = json.dumps(segment_episode("ep", "Ep", "ep.mp3", payload),
                        ensure_ascii=False)
    assert first == second
