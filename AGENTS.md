# AGENTS.md

dotFIT Agentic Assistant — a frontier model given the nutrition/supplement
corpus as tools, deciding for itself what to look up and what to say.

This is the `agentic-rag` branch. The previous runtime — a fixed chain of
guardrail → rewrite → search → synthesis → post-check — still builds and still
runs, as the comparison baseline. Its docs are archived under `docs/v1/` and
govern nothing here.

## Read first

- `docs/agentic-assistant.md` — the design. `§N` refs are cited in code
  docstrings, commit messages and progress entries; keep citing them.
- `docs/progress.md` — living status: what is built, what is open, the newest
  log entries. Check the open items before claiming something is "next".
- `docs/v1/decisions.md` — **still binding upstream.** The corpus, PII, alias,
  PDSRG and index rulings constrain the pipeline this branch reuses unchanged.
  Read the group for the area before changing pipeline behavior. Its Runtime
  §11 group describes the old runtime and does not apply.
- **Module docstrings are the detail.** Every module opens with its §ref and the
  contract baked into it. Read the file you are about to touch — this document
  is a map, not a substitute.

## What this branch is trying to fix

The owners found the v1 runtime slow, refusal-prone and unconversational. All
three are architectural: a fixed pipeline pays for every stage on every turn,
gates the answer behind a claims audit measured at 0/3 recall and 39/125
withheld, and has no way to *not* run a search. The fix is one model with tools,
not better prompts in the same chain. Judge changes against that: **does this
make it faster, less refusal-prone, or more conversational?** If it makes it
safer by adding a blocking stage, it is re-creating v1 — raise it as a decision
instead of building it.

## Commands

Pipeline, from `pipeline/` (uv-managed, Python 3.12 pinned in `.python-version`):

```bash
uv sync                                  # only prerequisite is uv itself
uv run pytest                            # must stay green; this branch changes no pipeline code
uv run qa-pipeline --help                # subcommands; <sub> --help for all flags
```

Subcommands: `stage0`, `stage1`, `stage2`, `stage4`, `run`, `aliases`, `pdsrg`,
`index`, `podcast`, `golden`, `eval`. Path defaults are repo-root-relative, so
from `pipeline/` pass `../data/...` / `../processed/...`. Corpus filenames
contain spaces, commas and `&` — always quote paths.

Runtime (.NET 10), from `runtime/`:

```bash
dotnet build && dotnet test              # 85 agentic + 264 v1, all must stay green

dotnet run --project src/DotFit.Agentic.Cli -- config      # resolved config, keys masked
dotnet run --project src/DotFit.Agentic.Cli -- prompt      # the assembled system prompt
dotnet run --project src/DotFit.Agentic.Cli -- search "…"  # the search tool alone, no model
dotnet run --project src/DotFit.Agentic.Cli -- ask "…" --trace --log
dotnet run --project src/DotFit.Agentic.Cli -- chat        # keeps history; `reset`, `exit`
dotnet run --project src/DotFit.Agentic.Cli -- smoke --tier safety   # live, costs tokens
```

`search`, `prompt` and `config` need no chat deployment. `ask`, `chat` and
`smoke` cost Azure calls — `smoke` runs 29 items and every turn of the
multi-turn ones, so reach for `--tier` first.

The v1 verbs (`dotfit-agent`, `dotfit-agent-service`) still exist and still
work — do not break them, they are the baseline.

## Architecture

The pipeline and the index are **unchanged and not this branch's work**. Two
corpora feed a shared alias vocabulary, then a shared index; the runtime queries
it.

QA: `data/QAs/**/*.docx` → stage0 → stage1 → stage2 → stage4 → `index`
PDSRG: PDF → `pdsrg` → `index`; products.json + infopages + menus + podcasts → `index`

| File | Owns | Ref |
|---|---|---|
| `pipeline/` (all of it) | corpora → `processed/` → `kb-main-v2`. Reused as-is | §4 |
| `runtime/src/DotFit.Agents/Retrieval/` | hybrid search client, authority re-rank. **Reused, not forked** | §5 |
| `runtime/src/DotFit.Agents/Aliases/` | alias table load + expansion. **Reused, not forked** | §5, §7 |
| `runtime/src/DotFit.Agentic/` | the tools, the loop, the system prompt, the turn log | §6–§8, §10 |
| `runtime/src/DotFit.Agentic.Cli/` | `dotfit-agentic` — chat + one-shot, trace, smoke runner | §12 |
| `runtime/src/DotFit.Agentic.Service/` | SSE endpoint, new event vocabulary | §9 |
| `runtime/smoke/` | the written conversational set, and how to read a transcript | §11.2 |
| `runtime/src/DotFit.Agents*` (v1 projects) | the baseline. Keep buildable, change only to keep it building | `docs/v1/` |

`DotFit.Agentic` is a **sibling** namespace of `DotFit.Agents`, not a child.
That is deliberate: as a child, every file needing a conversation type also
inherited v1's `StageEvent` / `DeltaEvent` / `ResultEvent` — a different
contract with the same names. This branch's events are `Turn…`-prefixed so the
ambiguity cannot come back.

## Working rules that bite

- **Do not add a blocking stage.** Nothing gates, nothing is withheld, nothing
  is retracted (§8, decision D3). Safety and claims are enforced by the prompt,
  by the tool surface, and by the log. If evidence says that is not enough, the
  next move is the out-of-band review in §8.3 — off the critical path — not a
  gate back in the path.
- **No model call outside the loop.** No pre-check, no rewrite, no post-check.
  Alias expansion, filters and source numbering are deterministic code. A second
  model in the request path is the thing this branch removed.
- **Source numbers are turn-scoped and assigned at tool-result time**, emitted
  to the caller *before* any text cites them (§7). A number never changes
  meaning inside a turn. This is what makes `[n]` resolvable while streaming —
  break it and citations in a live stream point at nothing.
- **A rename is not a replacement.** A rename is an identity mapping and may
  expand to the successor's `part_no`s ("LeanMR, now LeanMeal"); a replacement is
  a different formula and is only a currency cue (Recover&Build was *replaced
  by* AminoFormula, it is not its old name). Both reach the model through the
  system prompt's currency facts and through tool-side expansion — keep them
  distinct in both.
- **The alias table is never used to rewrite corpus text** — only to tag and to
  expand queries.
- **`products.json` is the legal-approved claims corpus** — quote it, never
  paraphrase claims. `get_product` exists to make quoting the cheap path.
- **`is_current eq true` is always in the filter.** AI Search does not match a
  filter against null, so an unstamped document is invisible to every query.
  `product_status` is a separate axis: a discontinued product's documents stay
  current so "what happened to X" is answerable.
- **PII**: `data/QAs/` is read-only and holds real customer mail; nothing
  unscrubbed leaves Stage 0. The turn log holds **no question or answer text** —
  enforced by the schema having no such field, not by remembering (§10). Tests
  use synthetic fixtures — never paste real corpus text into tests or docs.
- **Determinism where it is claimed**: pipeline reruns are byte-identical, from
  a different working directory too. The loop is not deterministic and no test
  may pretend it is — pin the tool layer, script the `IChatClient` for the loop.
- **Minimal tests, deliberately** (§11, decision D7). The deterministic tool
  layer gets unit tests; the loop gets a handful of shape tests. Do not port
  v1's 264. The 250-item golden set is **not** a validity signal — it was never
  labeled — and no number computed against it may be quoted as correctness.
- `processed/` is committed but derived: regenerate, don't hand-edit. Numbers
  quoted in `docs/progress.md` must match a regenerated run or a dated live run.
- Commits: `area: description`, lowercase. New behavior gets a test where a test
  is meaningful; say in the progress entry when it isn't.
