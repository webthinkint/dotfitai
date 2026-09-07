# dotFIT runtime — knowledge assistant (plan §11)

.NET 10 solution with two projects:

- **`src/DotFit.Agents`** — the library. The §11 pipeline as components:
  guardrail pre-check (small model) → query rewrite (small model) +
  deterministic alias expansion (§5 artifact) → hybrid search on `kb-main`
  (`is_current` filter, semantic ranker off by default, authority re-rank) →
  grounded answer with `[n]` citations (chat model, streamed) →
  deterministic post-check + optional claims-language audit. No tool calls:
  retrieval is single-shot by design in v1.
- **`src/DotFit.Agents.Cli`** — `dotfit-agent`, the testing/demo harness.

Tests (`tests/`) are hermetic: scripted `IChatClient` fakes and synthetic
fixtures, no Azure calls.

## Build / test

```bash
cd runtime
dotnet build
dotnet test
```

## Config

The same gitignored root `.env` the Python pipeline uses (see
`.env.example`). The loader walks up from the current directory to find it,
prefers `AZURE_SEARCH_QUERY_KEY` over the admin key, and falls back
`AZURE_OPENAI_SMALL_CHAT_DEPLOYMENT` → `AZURE_OPENAI_CHAT_DEPLOYMENT`. Keys
are never echoed. The alias table is read from
`processed/aliases/alias_table.json` (override `--aliases`).

Each verb loads only the slice of the contract it uses (`RuntimeNeeds`, the
mirror of `azure_config.py`'s `require=` subsets): `search` needs the search
service and the embedding deployment but no chat deployment, `guardrail` and
`rewrite` need only the small chat deployment, `ask`/`chat` need everything.
Values that *are* present are validated either way — a broken optional is
still a broken `.env`.

**Index name:** the default is still `kb-main`, which is the orphaned index
(progress open item 10). Until that resolves, pass `--index kb-main-v2` — the
live index — on every command that queries.

Note: `Azure.AI.OpenAI` is pinned to the prerelease line **on purpose** — the
GA build only offers api-version `2024-10-21`, which our Foundry v2 endpoint
404s. `RuntimeFactory` pins `2025-04-01-preview` (verified live by the
pipeline smokes).

## Usage

```bash
dotnet run --project src/DotFit.Agents.Cli -- ask "can I take creatine with coffee?" --index kb-main-v2 --trace
dotnet run --project src/DotFit.Agents.Cli -- chat --index kb-main-v2
dotnet run --project src/DotFit.Agents.Cli -- search "creatine loading" --index kb-main-v2 --trace
dotnet run --project src/DotFit.Agents.Cli -- guardrail "how much for my 10 year old?"
dotnet run --project src/DotFit.Agents.Cli -- rewrite "LeanMR dosage"
```

`ask`/`chat` run the full pipeline (exit 1 when the post-check fails);
`search` is retrieval-only (embedding + hybrid query, no chat LLM);
`guardrail`/`rewrite` run single stages. Flags: `--index <name>`, `--top N`,
`--semantic` (ranker on — open item 5; the re-rank then orders on the ranker's
score, not the fused retrieval score), `--filter <odata>` (ANDed with
`is_current eq true`), `--raw` (skip alias expansion), `--json`,
`--no-stream`, `--no-claims-check`, `--gated`, `--trace`.

`--gated` switches `ask`/`chat` from the CLI default (`Live` — stream deltas as
generated, report a post-check failure after the fact) to the mode the SSE
service will use (`Gated` — hold every delta until the post-check has run, and
on failure deliver the templated handoff instead of the answer, never the
answer text). See plan §11 "streaming vs. gating"; with `--trace`, a withheld
draft is still printed for diagnosis.
