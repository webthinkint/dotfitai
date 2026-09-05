"""Alias table builder tests (synthetic fixtures mirroring products.json shapes)."""

from __future__ import annotations

import json

import pytest

from qa_pipeline.alias import (
    build_alias_table, harvest_candidates, norm, strip_variant_suffix,
    write_curation_worksheet,
)


def _product(part_no, longname, url="https://www.dotFIT.com/x"):
    return {"part_no": part_no, "longname": longname, "coid": 1,
            "URL": url, "searchcontent": "### Specifications"}


# synthetic corpus mirroring the real shapes: formerly-markers, multi-segment
# names, MV descriptors, gear, curated WheySmooth/dotBAR universes
PRODUCTS = [
    _product(1005, "Active MV - Multivitamin & Mineral Formula"),
    _product(1007, "Women's MV - Multivitamin & Mineral Formula"),
    _product(1009, "Over 50 MV - Multivitamin & Mineral Formula"),
    _product(1001, "Brain Health"),
    _product(1003, "CollagenComplex"),
    _product(1000, "Antioxidant"),
    _product(1017, "Probiotics"),
    _product(1205, "GlutamineComplex (formerly Muscle Defender L-Glutamine)"),
    _product(1213, "AminoFormula - Blue Raspberry"),
    _product(1216, "AminoFormula - Lemonade"),
    _product(1214, "NO7 PreWorkout - Blue Raspberry"),
    _product(1215, "NO7 PreWorkout - Lemonade"),
    _product(1333, "LeanMeal Nutrition Shake - Chocolate (formerly LeanMR)"),
    _product(1334, "LeanMeal Nutrition Shake - Vanilla (formerly LeanMR)"),
    _product(1374, "All Natural WheySmooth - Chocolate"),
    _product(1391, "BULK - Whey Smooth - Chocolate"),
    _product(1369, "WheySmooth -  High Protein - Chocolate"),
    _product(1456, "Chocolate Fudge Crisp dotBAR"),
    _product(1480, "Trail Mix dotBAR"),
    _product(1466, "Vanilla dotWAFER"),
    _product(1200, "Creatine Monohydrate - Raspberry Lemonade Drink Mix"),
    _product(1227, "Creatine Monohydrate - Unflavored"),
    _product(1207, "Creatine Complex - Raspberry Lemonade"),
    _product(1100, "WeightLoss & LiverSupport"),
    _product(1371, "First String - Chocolate"),
    _product(1372, "First String - Vanilla"),
    _product(8001, "Alln1 SuperBlend - Orange Burst"),
    _product(8016, "Alln1 SuperBlend - Pineapple Swirl"),
    _product(1611, "dotFIT Shaker Bottle - Red (20 oz)"),
    _product(1630, "dotFIT Shaker Bottle - Black (20 oz)"),
    _product(1646, "SportMixer Shaker Bottle"),
]


class TestNorm:
    def test_spacing_and_case_variants_collapse(self):
        assert norm("First String") == norm("FirstString")
        assert norm("WheySmooth") == norm("whey smooth")
        assert norm("Active MV") == norm("ActiveMV")

    def test_punctuation_and_ampersand(self):
        assert norm("Pre & Post Workout") == norm("PrePostWorkout")
        assert norm("NO7 PreWorkout") == norm("no7 pre-workout")

    def test_unicode(self):
        assert norm("Women’s MV") == norm("Women's MV")


class TestFamilyDerivation:
    def test_flavor_variants_group_by_base_name(self):
        table = build_alias_table(PRODUCTS)
        af = next(f for f in table["families"] if f["family"] == "AminoFormula")
        assert af["part_nos"] == [1213, 1216]
        assert af["canonical_part_no"] == 1213

    def test_mvs_are_separate_families(self):
        families = {f["family"] for f in build_alias_table(PRODUCTS)["families"]}
        assert {"Active MV", "Women's MV", "Over 50 MV"} <= families

    def test_wheysmooth_universe_is_one_family(self):
        table = build_alias_table(PRODUCTS)
        ws = next(f for f in table["families"] if f["family"] == "WheySmooth")
        assert ws["part_nos"] == [1369, 1374, 1391]

    def test_dotbars_group_but_wafer_separate(self):
        families = {f["family"]: f for f in build_alias_table(PRODUCTS)["families"]}
        assert families["dotBAR"]["part_nos"] == [1456, 1480]
        assert 1466 not in families["dotBAR"]["part_nos"]
        assert "Vanilla dotWAFER" in families  # no flavor suffix to strip

    def test_creatine_family_and_complex_separate(self):
        families = {f["family"]: f for f in build_alias_table(PRODUCTS)["families"]}
        assert families["Creatine Monohydrate"]["part_nos"] == [1200, 1227]
        assert families["Creatine Complex"]["part_nos"] == [1207]

    def test_families_keep_highest_authority_canonical(self):
        # canonical = lowest part_no in family (first SKU published)
        table = build_alias_table(PRODUCTS)
        ws = next(f for f in table["families"] if f["family"] == "WheySmooth")
        assert ws["canonical_part_no"] == 1369

    def test_family_count(self):
        table = build_alias_table(PRODUCTS)
        # 31 products - 3 gear = 28; families: MV x3, BrainHealth,
        # CollagenComplex, Antioxidant, Probiotics, Glutamine, AminoFormula,
        # NO7, LeanMeal, WheySmooth, dotBAR, dotWAFER, CreatineMonohydrate,
        # CreatineComplex, WLLS, FirstString, Alln1SuperBlend = 19
        assert table["n_families"] == 19


class TestLegacyRenames:
    def test_formerly_markers_extracted(self):
        renames = {r["deprecated"]: r for r in build_alias_table(PRODUCTS)["legacy_renames"]}
        assert renames["LeanMR"]["current_family"] == "LeanMeal Nutrition Shake"
        assert renames["LeanMR"]["part_nos"] == [1333, 1334]

    def test_corpus_form_override(self):
        # marker says "Muscle Defender L-Glutamine"; corpus (19 docs) writes
        # MuscleDefender / muscle defender — the override must win
        renames = {r["deprecated"]: r for r in build_alias_table(PRODUCTS)["legacy_renames"]}
        assert "MuscleDefender" in renames
        assert "Muscle Defender L-Glutamine" not in renames
        assert renames["MuscleDefender"]["current_family"] == "GlutamineComplex"
        assert "corpus-attested" in renames["MuscleDefender"]["source"]

    def test_curated_no7_rage(self):
        renames = {r["deprecated"]: r for r in build_alias_table(PRODUCTS)["legacy_renames"]}
        assert renames["NO7 Rage"]["current_family"] == "NO7 PreWorkout"
        assert renames["NO7 Rage"]["part_nos"] == [1214, 1215]
        assert "curated" in renames["NO7 Rage"]["source"]

    def test_pdsrg_disposition_renames(self):
        # support lead, 2026-09-01 (5): renamed PDSRG-only products
        renames = {r["deprecated"]: r for r in build_alias_table(PRODUCTS)["legacy_renames"]}
        assert renames["SuperiorAntioxidant"]["current_family"] == "Antioxidant"
        assert renames["SuperiorAntioxidant"]["part_nos"] == [1000]
        assert renames["UltraProbiotic"]["current_family"] == "Probiotics"
        assert renames["UltraProbiotic"]["part_nos"] == [1017]
        assert renames["ExtremeCreatineXXXL"]["current_family"] == "Creatine Complex"
        assert renames["ExtremeCreatineXXXL"]["part_nos"] == [1207]
        assert renames["JointSkinCollagen+"]["current_family"] == "CollagenComplex"
        assert renames["AdvancedBrainHealth"]["current_family"] == "Brain Health"
        assert "Recover&Build" not in renames   # a replacement, not a rename
        for token in ("SuperiorAntioxidant", "UltraProbiotic"):
            assert "PDSRG dispositions" in renames[token]["source"]

    def test_replacements_are_separated_from_renames(self):
        """A rename is an identity mapping; a replacement is a different
        formula. Expanding a replacement to the successor's part_nos would
        answer a Recover&Build question with AminoFormula's claims, so the
        two must not share a section that consumers read for part_nos."""
        table = build_alias_table(PRODUCTS)
        reps = {r["deprecated"]: r for r in table["replacements"]}
        assert set(reps) == {"Recover&Build"}
        assert reps["Recover&Build"]["successor_family"] == "AminoFormula"
        assert reps["Recover&Build"]["successor_part_nos"] == [1213, 1216]
        assert "part_nos" not in reps["Recover&Build"]   # not an identity map
        assert "never a product tag" in reps["Recover&Build"]["note"]

    def test_discontinued_section(self):
        table = build_alias_table(PRODUCTS)
        disc = {d["name"]: d for d in table["discontinued"]}
        assert set(disc) == {"KidsMV", "VeganMV"}
        assert "Active MV" in disc["KidsMV"]["note"]  # successor guidance
        # discontinued names never resolve to part_nos
        families = {f["family"] for f in table["families"]}
        assert not any(d["name"] in families for d in table["discontinued"])


class TestDeterministicAliases:
    def test_tag_aliases_resolve_to_part_nos(self):
        table = build_alias_table(PRODUCTS)
        det = {a["token"]: a for a in table["deterministic_aliases"]}
        # fixture AminoFormula = 1213 + 1216
        assert det["AF"]["part_nos"] == [1213, 1216]
        assert det["AF"]["family"] == "AminoFormula"

    def test_mvm_is_context_only_never_all_mvs(self):
        # MVs are distinct products chosen by audience; a deterministic
        # all-MV tag would blur the distinction (reconsidered 2026-09-01)
        table = build_alias_table(PRODUCTS)
        tokens = {a["token"] for a in table["deterministic_aliases"]}
        assert "MVM" not in tokens
        assert "MVM" in table["context_only_tokens"]
        assert "Over 50 MV" in table["context_only_tokens"]["MVM"]
        assert "never all three" in table["context_only_tokens"]["MVM"]

    def test_context_only_pp_not_deterministic(self):
        table = build_alias_table(PRODUCTS)
        tokens = {a["token"] for a in table["deterministic_aliases"]}
        assert "PP" not in tokens
        assert "PP" in table["context_only_tokens"]

    def test_unknown_alias_family_raises(self, monkeypatch):
        import qa_pipeline.alias as alias_mod
        monkeypatch.setitem(alias_mod.CURATED_ALIASES, "XX",
                            {"family": "No Such Family"})
        with pytest.raises(ValueError, match="unknown family"):
            build_alias_table(PRODUCTS)


class TestGearExclusion:
    def test_shakers_excluded(self):
        table = build_alias_table(PRODUCTS)
        assert table["excluded"]["gear"] == [1611, 1630, 1646]
        all_pns = [pn for f in table["families"] for pn in f["part_nos"]]
        assert not {1611, 1630, 1646} & set(all_pns)


class TestDeterminism:
    def test_byte_identical_reruns(self, tmp_path):
        import qa_pipeline.io_utils as iou
        p1, p2 = tmp_path / "a.json", tmp_path / "b.json"
        iou.write_json(p1, build_alias_table(PRODUCTS))
        iou.write_json(p2, build_alias_table(PRODUCTS))
        assert p1.read_bytes() == p2.read_bytes()

    def test_invalid_input_raises(self):
        with pytest.raises(ValueError):
            build_alias_table([])
        with pytest.raises(ValueError):
            build_alias_table([_product(1, "X"), _product(1, "Y")])


class TestHarvest:
    def _docs(self, tmp_path):
        docs = [
            {"source_file": "2024/a.docx", "question": "Can I stack AF with NO7?",
             "customer_section": "", "expert_section": "AF is fine.",
             "filename": "AF"},
            # multi-line text: evidence cells must not contain raw newlines
            {"source_file": "2024/e.docx", "question": "AF multi\nline\nusage",
             "customer_section": "", "expert_section": "",
             "filename": "x"},
            # corpus form of the GlutamineComplex legacy name (spacing variant)
            {"source_file": "2024/f.docx", "question": "is muscle defender discontinued?",
             "customer_section": "", "expert_section": "",
             "filename": "MuscleDefender"},
            {"source_file": "2024/b.docx", "question": "What was LeanMR?",
             "customer_section": "I used LeanMR for months",
             "expert_section": "", "filename": "LeanMR question"},
            {"source_file": "2024/c.docx", "question": "unrelated",
             "customer_section": "", "expert_section": "", "filename": "x"},
        ]
        path = tmp_path / "documents.jsonl"
        path.write_text("\n".join(json.dumps(d) for d in docs), encoding="utf-8")
        return path

    def test_counts_and_evidence(self, tmp_path):
        table = build_alias_table(PRODUCTS)
        cands = {c["token"]: c for c in harvest_candidates(table, self._docs(tmp_path))}
        assert cands["AF"]["n_docs"] == 2
        assert "a.docx" in cands["AF"]["evidence"][0]
        # legacy rename found via tolerant pattern (spacing/punct-insensitive);
        # per-document count; snippet aligns to the raw match position
        assert cands["LeanMR"]["n_docs"] == 1
        assert "LeanMR" in cands["LeanMR"]["evidence"][0]
        # tolerant match across spacing: 'Lean MR' must also hit
        path_b = tmp_path / "documents.jsonl"
        docs2 = [{"source_file": "2024/d.docx", "question": "is Lean MR the same?",
                  "customer_section": "", "expert_section": "", "filename": "x"}]
        path_b.write_text("\n".join(json.dumps(d) for d in docs2), encoding="utf-8")
        cands2 = {c["token"]: c for c in
                  harvest_candidates(build_alias_table(PRODUCTS), path_b)}
        assert cands2["LeanMR"]["n_docs"] == 1
        assert "Lean MR" in cands2["LeanMR"]["evidence"][0]
        # overridden legacy form matches any spacing variant of the corpus form
        assert cands["MuscleDefender"]["n_docs"] == 1
        assert "muscle defender" in cands["MuscleDefender"]["evidence"][0]
        # absent token counts zero, no evidence
        assert cands["SB"]["n_docs"] == 0
        assert cands["SB"]["evidence"] == []

    def test_discontinued_names_are_currency_candidates(self, tmp_path):
        # mentions of discontinued products must surface as currency-cue
        # rows (KidsMV 22 / VeganMV 81 docs in the real corpus — Stage 4
        # needs them to flag superseded answers)
        table = build_alias_table(PRODUCTS)
        docs = [
            {"source_file": "2024/k.docx", "question": "can my kid take KidsMV?",
             "customer_section": "", "expert_section": "", "filename": "kids"},
            {"source_file": "2024/v.docx", "question": "vegan multivitamin?",
             "customer_section": "All vegans use VeganMV",
             "expert_section": "", "filename": "x"},
        ]
        path = tmp_path / "documents.jsonl"
        path.write_text("\n".join(json.dumps(d) for d in docs), encoding="utf-8")
        cands = {c["token"]: c for c in harvest_candidates(table, path)}
        assert cands["KidsMV"]["n_docs"] == 1
        assert cands["VeganMV"]["n_docs"] == 1
        assert "discontinued (no successor)" in cands["KidsMV"]["candidate_target"]
        assert cands["KidsMV"]["recommendation"] == "tag"

    def test_rename_chain_names_harvest(self, tmp_path):
        # formerly-chain names (CreatineXXL, JointFlexPlus) count as their
        # own deprecated tokens targeting the chain's current family
        table = build_alias_table(PRODUCTS)
        docs = [
            {"source_file": "2024/x.docx", "question": "creatine XXL stack?",
             "customer_section": "", "expert_section": "", "filename": "x"},
        ]
        path = tmp_path / "documents.jsonl"
        path.write_text("\n".join(json.dumps(d) for d in docs), encoding="utf-8")
        cands = {c["token"]: c for c in harvest_candidates(table, path)}
        assert cands["CreatineXXL"]["n_docs"] == 1  # tolerant: 'creatine XXL'
        assert "Creatine Complex" in cands["CreatineXXL"]["candidate_target"]
        assert cands["JointFlexPlus"]["n_docs"] == 0
        assert "CollagenComplex" in cands["JointFlexPlus"]["candidate_target"]

    def test_tolerant_pattern_accepts_and_form(self, tmp_path):
        # "Recover & Build" ≡ "Recover and Build" — both attested;
        # the old gap missed the word form entirely (review fix)
        from qa_pipeline.alias import _tolerant_pattern
        pat = _tolerant_pattern("Recover&Build")
        assert pat.search("Recover & Build")
        assert pat.search("Recover and Build")
        assert pat.search("Recover&Build")
        table = build_alias_table(PRODUCTS)
        docs = [
            {"source_file": "2024/r.docx",
             "question": "is Recover and Build discontinued?",
             "customer_section": "", "expert_section": "", "filename": "x"},
        ]
        path = tmp_path / "documents.jsonl"
        path.write_text("\n".join(json.dumps(d) for d in docs), encoding="utf-8")
        cands = {c["token"]: c for c in harvest_candidates(table, path)}
        assert cands["Recover&Build"]["n_docs"] == 1

    def test_worksheet_renders_rows(self, tmp_path):
        table = build_alias_table(PRODUCTS)
        cands = harvest_candidates(table, self._docs(tmp_path))
        ws = tmp_path / "ws.md"
        write_curation_worksheet(ws, cands)
        text = ws.read_text(encoding="utf-8")
        assert "| [x] | `AF` | **tag** |" in text
        assert "| [x] | `PP` | **context-only** |" in text
        assert "| [x] | `MVM` | **context-only** |" in text
        assert "reconsidered" in text
        assert "`LeanMR`" in text
        # policy + legend must be present so the session can't miss them
        assert "NEVER rewritten" in text
        assert "**context-only**" in text
        assert "**no mapping**" in text
        # no raw newlines inside table cells (breaks md viewers)
        assert "multi line usage" in text
        assert "multi\nline" not in text


class TestRealCorpusSanity:
    """Integration guard against the real products.json (no PII — committed data)."""

    def test_builds_and_meets_expectations(self):
        from pathlib import Path
        products_path = Path(__file__).resolve().parents[2] / \
            "data" / "Product Data" / "products.json"
        if not products_path.is_file():
            pytest.skip("real products.json not present")
        products = json.loads(products_path.read_text(encoding="utf-8"))
        table = build_alias_table(products)
        assert table["n_products"] == 56  # sku 1000 Antioxidant added 2026-09-01
        assert table["n_families"] >= 31
        renames = {r["deprecated"] for r in table["legacy_renames"]}
        assert {"LeanMR", "MuscleDefender", "NO7 Rage", "SuperiorAntioxidant",
                "UltraProbiotic", "ExtremeCreatineXXXL", "JointSkinCollagen+",
                "AdvancedBrainHealth"} <= renames
        assert "Recover&Build" not in renames
        assert {r["deprecated"] for r in table["replacements"]} == {"Recover&Build"}
        assert {d["name"] for d in table["discontinued"]} == {"KidsMV", "VeganMV"}
        assert table["excluded"]["gear"] == [1611, 1612, 1630, 1631, 1646]
        det = {a["token"]: a["part_nos"] for a in table["deterministic_aliases"]}
        assert det["AF"] == [1213, 1216, 1220]
        assert det["SB"] == [8001, 8016]
        assert det["WLLS"] == [1100]
        assert "PP" not in det
        assert "MVM" not in det

    def test_family_canonicals_are_pinned(self):
        """The family voice is the canonical (lowest part_no ≈ first
        published) SKU's content. A re-export that renumbers SKUs, retires a
        hero, or backfills a lower number would silently revoice families —
        this pins canonicals + membership so that change goes red and forces
        a conscious call (verified sane 2026-09-05: every canonical is the
        hero flavor)."""
        from pathlib import Path
        products_path = Path(__file__).resolve().parents[2] / \
            "data" / "Product Data" / "products.json"
        if not products_path.is_file():
            pytest.skip("real products.json not present")
        products = json.loads(products_path.read_text(encoding="utf-8"))
        fams = {f["family"]: f for f in build_alias_table(products)["families"]}
        expected = {
            "Alln1 SuperBlend": (8001, [8001, 8016]),
            "AminoFormula": (1213, [1213, 1216, 1220]),
            "Creatine Monohydrate": (1200, [1200, 1227]),
            "First String": (1371, [1371, 1372]),
            "LeanMeal Nutrition Shake": (1333, [1333, 1334]),
            "NO7 PreWorkout": (1214, [1214, 1215, 1217]),
            "Plant Protein": (1300, [1300, 1301]),
            "Pre & Post Workout Formula": (1367, [1367, 1368]),
            "WheySmooth": (1369, [1369, 1370, 1374, 1375, 1391, 1392, 1399]),
            "dotBAR": (1456, [1456, 1457, 1462, 1480, 1482]),
        }
        assert {f for f, fam in fams.items() if fam["n_variants"] > 1} == set(expected)
        for family, (canon, pns) in expected.items():
            assert fams[family]["canonical_part_no"] == canon, family
            assert fams[family]["part_nos"] == pns, family
