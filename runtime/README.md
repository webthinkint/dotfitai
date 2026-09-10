# dotFIT runtime — knowledge assistant (plan §11)

.NET 10 solution with three projects:

- **`src/DotFit.Agents`** — the library. The §11 pipeline as components:
  guardrail pre-check (small model) → query rewrite (small model) +
  deterministic alias expansion (§5 artifact) → hybrid search on the §9 index
  (`is_current` filter, semantic ranker off by default, authority re-rank) →
  grounded answer with `[n]` citations (chat model, streamed) →
  deterministic post-check + optional claims-language audit. No tool calls:
  retrieval is single-shot by design in v1.
- **`src/DotFit.Agents.Cli`** — `dotfit-agent`, the testing/demo harness.
- **`src/DotFit.Agents.Service`** — `dotfit-agent-service`, the ASP.NET Core
  SSE endpoint the widget talks to. Transport only: config load, `POST /ask`,
  `GET /healthz`. It is **always `Gated`** and offers the client no choice
  (§11), and its payloads are narrower than the CLI's `--json` — no retrieved
  source `content`, no withheld draft, no post-check failure reasons. Those are
  operator diagnostics; this endpoint is public.

Both callers go through `IKnowledgeAssistant`, so the service is testable
without a host and without Azure.

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

**Index name:** the default is `kb-main-v2`, the live index — no `--index`
override needed. `kb-main`, the original index, was lost to a wedged delete
(progress open item 10, closed 2026-09-08); the name is free again but the
default stays `kb-main-v2` — a rename is cosmetic. This default mirrors
`index_build.INDEX_NAME`; a test pins each side, so change both or neither.

Note: `Azure.AI.OpenAI` is pinned to the prerelease line **on purpose** — the
GA build only offers api-version `2024-10-21`, which our Foundry v2 endpoint
404s. `RuntimeFactory` pins `2025-04-01-preview` (verified live by the
pipeline smokes).

## Usage

```bash
dotnet run --project src/DotFit.Agents.Cli -- ask "can I take creatine with coffee?" --trace
dotnet run --project src/DotFit.Agents.Cli -- ask "creatine and coffee" --json          # §12 eval-harness contract
dotnet run --project src/DotFit.Agents.Cli -- chat
dotnet run --project src/DotFit.Agents.Cli -- search "creatine loading" --trace
dotnet run --project src/DotFit.Agents.Cli -- guardrail "how much for my 10 year old?"
dotnet run --project src/DotFit.Agents.Cli -- rewrite "LeanMR dosage"
```

`ask`/`chat` run the full pipeline (exit 1 when the post-check fails). `ask` is
single-turn by definition and stays that way — the §12 harness drives it;
`chat` keeps the session transcript and resolves follow-ups against it
(`reset` starts a new conversation). See Multi-turn below.
`search` is retrieval-only (embedding + hybrid query, no chat LLM);
`guardrail`/`rewrite` run single stages. Flags: `--index <name>`, `--top N`,
`--semantic` (ranker on — open item 5; the re-rank then orders on the ranker's
score, not the fused retrieval score), `--filter <odata>` (ANDed with
`is_current eq true`), `--raw` (skip alias expansion), `--json`,
`--no-stream`, `--no-claims-check`, `--gated`, `--trace`.

`--gated` switches `ask`/`chat` from the CLI default (`Live` — stream deltas as
generated, report a post-check failure after the fact) to the mode the SSE
service uses (`Gated` — hold every delta until the post-check has run, and
on failure deliver the templated handoff instead of the answer, never the
answer text). See plan §11 "streaming vs. gating"; with `--trace`, a withheld
draft is still printed for diagnosis.

`--json` on `ask`/`chat` is the **§12 eval-harness contract**, not a rendering
option: stdout carries one JSON object per question (`AskJson`) and nothing
else — the banner, the streamed deltas and any `--trace` lines move to stderr,
so a harness can pipe stdout straight into a parser. The projection is
explicit and snake_case (matching the pipeline's artifacts), and it emits both
answer texts: `answer_text` is the generated draft — present even when the
gate withheld it, so §12 can score what the model actually produced — and
`delivered_text` is what the caller saw. Retrieved sources ride along with
their `content`, because the RAGAS-style faithfulness and context-precision
metrics score the answer against the retrieved context. `stage_seconds` is the
one non-deterministic field; nothing in the harness may key on it.

## The SSE service

```bash
ASPNETCORE_URLS=http://127.0.0.1:5199 \
  dotnet run --project src/DotFit.Agents.Service --no-launch-profile

curl -sN -X POST http://127.0.0.1:5199/ask \
  -H "Content-Type: application/json" \
  -d '{"question":"can I take creatine with my morning coffee?"}'
```

Config comes from the same root `.env`, loaded with `RuntimeNeeds.Full` **at
startup** — a missing deployment or key fails the boot rather than every
request. `DotFit:Index` overrides the index name.

The event names are the wire contract: `disclosure`, `stage`, `delta`,
`retraction`, `result`, `error`. `disclosure` is the §11 conversation-start
AI-identity notice and is emitted once, when the request carries **no**
`conversation_id` — send the id back on later turns or every turn re-announces.
The wire is snake_case in both directions.

`POST /ask` also takes `history`: `[{"role":"user"|"assistant","text":"…"}]`,
oldest first, excluding the question being asked. An unknown `role` is a `400`
rather than a dropped turn. The full client-facing contract is
`docs/website-integration.md`.

## Multi-turn

The service holds no state between requests, so the caller resends the recent
transcript it already owns (`AskOptions.History`; the CLI's `chat` verb keeps
its own, and `reset` clears it). `ConversationHistory` is the single place that
decides what is accepted — blank turns dropped, a trailing echo of the current
question dropped, newest 8 turns kept, 1,000 characters per turn — so the CLI
and the service cannot disagree about it.

History reaches **the rewrite stage only**. That is where a follow-up collapses
back into one standalone question, after which search, the answer agent and the
post-check see no conversational state at all. It deliberately never reaches
the answer agent: an answer grounded in anything but the retrieved sources
cannot honour the `[n]` citation contract, and an earlier assistant turn is not
a source.

It does not yet reach the **guardrail**, which still judges each turn alone —
progress open item 19, and its own row because an escalation trigger can arrive
turns before the question it applies to. `ConversationTests` pins that boundary
rather than leaving it to be rediscovered.

`stage` events stream even though deltas are gated: that is what makes gating
affordable, since the widget has something live to render while the answer is
held. On a post-check failure no `delta` of the answer is ever sent — a
`retraction` arrives, then the templated handoff.
