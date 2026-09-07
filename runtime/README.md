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

Note: `Azure.AI.OpenAI` is pinned to the prerelease line **on purpose** — the
GA build only offers api-version `2024-10-21`, which our Foundry v2 endpoint
404s. `RuntimeFactory` pins `2025-04-01-preview` (verified live by the
pipeline smokes).

## Usage

```bash
dotnet run --project src/DotFit.Agents.Cli -- ask "can I take creatine with coffee?" --trace
dotnet run --project src/DotFit.Agents.Cli -- chat
dotnet run --project src/DotFit.Agents.Cli -- search "creatine loading" --trace
dotnet run --project src/DotFit.Agents.Cli -- guardrail "how much for my 10 year old?"
dotnet run --project src/DotFit.Agents.Cli -- rewrite "LeanMR dosage"
```

`ask`/`chat` run the full pipeline (exit 1 when the post-check fails);
`search` is retrieval-only (embedding + hybrid query, no chat LLM);
`guardrail`/`rewrite` run single stages. Flags: `--top N`, `--semantic`
(ranker on — open item 5), `--filter <odata>` (ANDed with `is_current eq
true`), `--raw` (skip alias expansion), `--json`, `--no-stream`,
`--no-claims-check`, `--trace`.
