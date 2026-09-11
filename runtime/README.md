# dotFIT runtime — knowledge assistant (plan §11)

.NET 10 solution with three projects:

- **`src/DotFit.Agents`** — the library. The §11 pipeline as components:
  guardrail pre-check (small model) → query rewrite (small model) +
  deterministic alias expansion (§5 artifact) → hybrid search on the §9 index
  (`is_current` filter, semantic ranker off by default, authority re-rank) →
  grounded answer with `[n]` citations (chat model, streamed) →
  deterministic post-check + optional claims-language audit → one bounded
  repair pass when the *only* thing that failed was the audit. No tool calls:
  retrieval is single-shot by design in v1. A turn the guardrail reads as
  small talk skips all of that — see "The conversational branch" below.
  The audit sees the same numbered sources the answer agent did *and* the §5
  alias notes it was given, so an instruction the pipeline issued — "mention the
  rename" — is not graded as an invention (open item 17). It does not see the
  claim-trap note, which would bias its verdict.
- **`src/DotFit.Agents.Cli`** — `dotfit-agent`, the testing/demo harness.
- **`src/DotFit.Agents.Service`** — `dotfit-agent-service`, the ASP.NET Core
  SSE endpoint the widget talks to. Transport only: config load, `POST /ask`,
  `GET /healthz`. It is **always `Gated`** and offers the client no choice
  (§11), and its payloads are narrower than the CLI's `--json` — no retrieved
  source `content`, no withheld draft, no post-check failure reasons. Those are
  operator diagnostics; this endpoint is public. Hardening lives in
  `ServiceOptions` (open item 22): shared-secret auth on `/ask`, a question
  length cap, a request timeout, and the support route the handoffs end on.

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

**Service hardening** (open item 22) adds four variables the CLI mostly ignores,
documented in `.env.example` and parsed by `ServiceOptions`:

| Variable | Default | Meaning |
|---|---|---|
| `DOTFIT_SERVICE_API_KEY` | — | Shared secret for `POST /ask`, sent as `Authorization: Bearer`. ≥16 chars. |
| `DOTFIT_SERVICE_AUTH` | unset | Only `none` is accepted, and only when something in front authenticates. |
| `DOTFIT_SERVICE_MAX_QUESTION_CHARS` | 2000 | Over the limit is a `400`, never a truncation. |
| `DOTFIT_SERVICE_TIMEOUT_SECONDS` | 120 | Our ceiling on one request; firing yields `error` + handoff, not silence. |
| `DOTFIT_SUPPORT_CONTACT` | the PDSRG-attested route | Where the refusal/withheld handoffs send a customer; `none` drops the line. Used by the **CLI too** — it is answer copy, not transport. |

Auth is **fail-closed**: with neither the key nor `AUTH=none`, the service does
not start. That is the posture, not an oversight — it has no authentication of
its own and must not be reachable without this, so there must be no state in
which it is running and open. The three `DOTFIT_SERVICE_*` variables are the one
place the process environment **overrides** the `.env` file, so a deployment can
inject the secret as a container setting instead of baking it into an image;
`DOTFIT_SUPPORT_CONTACT` and the Azure keys keep the file-only rule that
`azure_config.py` mirrors. CORS and rate limiting are deliberately absent: one
trusted server-side caller, no browser origin.

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

`ask`/`chat` run the full pipeline (exit 1 when the post-check fails). `ask`
keeps no transcript of its own but can be *given* one with `--history` (the
`POST /ask` JSON shape), which is how the §12 multi-turn set is driven;
`chat` keeps the session transcript and resolves follow-ups against it
(`reset` starts a new conversation). See Multi-turn below.
`search` is retrieval-only (embedding + hybrid query, no chat LLM);
`guardrail`/`rewrite` run single stages. Flags: `--index <name>`, `--top N`,
`--semantic` (ranker on — open item 5; the re-rank then orders on the ranker's
score, not the fused retrieval score), `--filter <odata>` (ANDed with
`is_current eq true`), `--raw` (skip alias expansion), `--json`,
`--no-stream`, `--no-claims-check`, `--no-repair`, `--gated`, `--trace`,
`--history '[{"role":"user","text":"I am 14"}]'` (`ask`/`guardrail`; the
`POST /ask` shape, oldest first, current question excluded — an unknown role is
a usage error, not a dropped turn).

`--gated` switches `ask`/`chat` from the CLI default (`Live` — stream deltas as
generated, report a post-check failure after the fact) to the mode the SSE
service uses (`Gated` — hold every delta until the post-check has run, and
on failure deliver the templated handoff instead of the answer, never the
answer text). See plan §11 "streaming vs. gating"; with `--trace`, a withheld
draft is still printed for diagnosis.

`--no-repair` turns off the stage 6b repair pass. The gate is whole-or-nothing
by construction, and that was costing whole correct answers: "How much creatine
should I take?" returned three bullets quoted verbatim from the approved copy
plus one appended sentence carrying a NO7 Preworkout3 statement onto
CreatineMonohydrate, and the customer got the support handoff. The audit was
right; discarding the other three bullets was not. So a failure where **every**
failing check is `claims_language` earns exactly one edit — excise or re-ground
the wording the audit named, change nothing else — and is then judged again by
the same checks. The bounds are the design: once, never a loop; claims only,
because `citation_presence` and `escalation_respected` are failures of shape and
a mixed failure is not repairable; the repaired draft goes back through the same
audit rather than reporting on itself; and both verdicts survive on the result,
so a repaired answer can never read as one that was clean the first time.
`--no-repair` is the diagnostic posture — the only way to see what the audit
rejected rather than what the repair made of it.

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

### The verdict log

Every accepted request writes exactly one JSON line to **stdout** (open item
20) — `VerdictLog`, tagged `"log": "dotfit.verdict"` so a collector can pick it
out of the host's own console logging:

```json
{"log":"dotfit.verdict","log_version":"1.2.0","request_id":"…","conversation_id":"…",
 "outcome":"escalated","escalated":true,"reasons":["under_18"],"history_trigger":true,
 "post_check_passed":true,"claims":"not_run","n_sources":0,"n_citations":0,
 "cited_authorities":[],"duration_ms":1981,"stage_ms":{"guardrail":1980,"answer":0}}
```

`outcome` is one of `answered` / `repaired` / `escalated` / `withheld` /
`chitchat` (the conversational branch below) / `error` / `abandoned` (the client
hung up), and `intent` carries the guardrail's reading of the turn (`question`
/`smalltalk` / `out_of_scope`, filtered to that vocabulary like `reasons`).

`repaired` is counted apart from `answered` for the same reason `chitchat` is,
and with more at stake: folded in, those rows would read `claims: compliant` —
the verdict of the *second* audit — and the flag the first one raised would be
absent from the log entirely, which is open item 12's precision numerator
quietly deleting itself. So "how many did we answer" is `answered + repaired`,
and `claims_pre_repair` / `pre_repair_failures` carry the first verdict
alongside. `withheld` outranks `repaired`: a repair that did not save the answer
is a withheld request, and the `repaired` flag stays true on that row either
way. A rising repair rate is the answer prompt regressing (open item 17) and has
to be visible without anyone having thought to look for it.

There is one line
on **every** terminal path — the write is in a `finally`, because a run that logged nothing is
indistinguishable from a run that never happened. A request rejected before the
stream opens (`400`/`401`) logs nothing: it never reached a verdict.

**It holds no question and no answer text**, and that is enforced rather than
trusted — there is no text field to put one in, `reasons` is filtered to the
known escalation vocabulary (the guardrail's free-prose `notes` is not logged at
all), and `failures`/`warnings` keep only each check's *name*, never its message,
which for `claims_language` quotes the draft back. `VerdictLogTests` pins each of
those. It is not configurable and has no off switch: with the preview free to
ship at any state (item 21), this is the only record of what an audience was
shown, and there is nothing to switch off for privacy.

`request_id` is echoed on the terminal `result` and `error` frames — in the
body, not a header, because the website server relays the stream — so the
caller can join its transcript to our verdicts. A caller may send its own
`request_id`; both it and `conversation_id` are capped at 64 characters, since
they are the only caller-supplied values the service retains.

Nothing is logged by the CLI or the eval harness: they drive `AskStream` and
`IKnowledgeAssistant` without a sink, and already have `--json` and `--trace`.

### The debug transcript (preview-only)

The verdict log's text-free rule is priced against *public* traffic. The
preview is a different audience, and retractions cannot be debugged from a log
that holds neither the withheld draft nor the wording the check rejected — so
an **opt-in companion record** exists (owner ruling 2026-09-11,
`docs/decisions.md` Runtime §11): with
`DOTFIT_SERVICE_DEBUG_TRANSCRIPT=true`, the same `finally` writes one
`dotfit.transcript` line per request carrying exactly what `VerdictLog`
structurally cannot — the raw question, the history as received, the draft
answer *including a withheld one*, the delivered text, the retraction reason,
the guardrail's free-prose `notes` and the **full** failure messages. On a
request the stage 6b repair pass touched it also carries the draft that was
replaced and the wording the audit cut (`pre_repair_*`): the verdict log can say
a request was `repaired`, but only this one can say whether the pass excised a
bad sentence or deleted a correct answer — in every other field those look
identical.

The posture is paid visibly, not eroded:

- **Off by default** — off is the public-traffic posture, and this must be
  **off before public customer traffic** (the ruling's scope; a gate on the
  same line as items 12/17).
- **Loud when on** — a boot line on stdout and `debug_transcript` on
  `/healthz`, for the same reason `auth` is there.
- **A separate record** — `VerdictLog` is unchanged and keeps every guarantee
  it ever had; `TranscriptLogTests` pins the separation (both sinks wired, the
  verdict still holds no text).

The preview unit (`runtime/deploy/`) enables it; delete that `Environment=`
line to turn it off.

### Reading the logs on the VM

`runtime/deploy/verdict-log` (installed by the deploy script as
`~/.local/bin/dotfit-verdict-log`) filters the service journal to the records
above — `journalctl --grep` does the filtering, so `-n N` means the last N
verdicts, not the last N journal lines:

```bash
dotfit-verdict-log -n 20                # the traffic view: one line per request
dotfit-verdict-log -f                    # follow it live
dotfit-verdict-log --transcripts -n 5    # the debug blocks: Q, retraction, draft,
                                         # what was delivered instead, sources
dotfit-verdict-log --raw -n 1            # collector mode: the JSON line, untouched
```

Anything unrecognized is passed to journalctl verbatim (`--since "1 hour
ago"`, `-b`, `--grep`). `--transcripts` output holds the words themselves —
treat it like customer data.

## Multi-turn

The service holds no state between requests, so the caller resends the recent
transcript it already owns (`AskOptions.History`; the CLI's `chat` verb keeps
its own, and `reset` clears it). `ConversationHistory` is the single place that
decides what is accepted — blank turns dropped, a trailing echo of the current
question dropped, newest 8 turns kept, 1,000 characters per turn — so the CLI
and the service cannot disagree about it.

History reaches **the rewrite stage and the guardrail**, and nothing else. The
rewrite is where a follow-up collapses back into one standalone question, after
which search, the answer agent and the post-check see no conversational state
at all. It deliberately never reaches the answer agent: an answer grounded in
anything but the retrieved sources cannot honour the `[n]` citation contract,
and an earlier assistant turn is not a source. `ConversationTests` pins that
boundary rather than leaving it to be rediscovered.

The **guardrail** reads it because a hard-escalation trigger is a fact about
the customer, stated once — "I'm 14" three turns before "how much creatine?"
(progress open item 19). The prompt carries the opposite rule too: history is
context for the question being asked, not a second question to answer, so one
trigger does not refuse every later turn. `GuardrailVerdict.HistoryTrigger`
records which of the two a verdict rests on; it changes nothing the customer
sees and everything an operator can reconstruct.

`stage` events stream even though deltas are gated: that is what makes gating
affordable, since the widget has something live to render while the answer is
held. On a post-check failure no `delta` of the answer is ever sent — a
`retraction` arrives, then the templated handoff.

## The conversational branch

Not every turn is a question. The guardrail classifies one alongside its safety
verdict — `intent` is `question`, `smalltalk` or `out_of_scope` — and a
`smalltalk` turn skips the rewrite, the alias expansion, the search and the
answer agent, and gets a short reply from `IChatReplyAgent` (small deployment),
falling back to `Prompts.SmallTalkMessage()` when that call fails.

It exists because "Hi there" used to come back as the support handoff. The
retrieval path runs whatever you type: hybrid search returns its `top` nearest
neighbours for a greeting as readily as for a question, the answer agent writes
a greeting with no `[n]`, and the post-check fails it on `citation_presence` —
which under `Gated` withholds the greeting and delivers the handoff instead.
The branch retrieves nothing, so the citation checks (already conditioned on a
non-empty source list) have nothing to fire on. **No check was relaxed**, and
`PostChecker` reads the shape off the verdict rather than guessing it from the
text.

`GuardrailVerdict.Conversational` owns the precedence in one expression:
escalation wins, a claim trap wins (it has to be corrected from approved copy,
which needs retrieval), a degraded pre-check never branches — failing open means
falling back to the fully checked path, not the one with no sources in it — and
only `smalltalk` branches. `out_of_scope` is classified and logged but keeps the
retrieval path, which it already passes: the §12 adversarial out-of-scope items
are delivered today, so there is nothing there to fix.

The branch is the one path where text reaches a customer without retrieval, so
it is fenced rather than trusted: the prompt forbids product content, guidance
and citations outright; it is not shown conversation history, for the same
reason the answer agent is not, and more sharply — a branch that cannot cite
must not be able to carry a fact forward out of an earlier assistant turn; and
the verdict log counts it as `chitchat`, never as `answered`, so a run of
greetings cannot read as a healthy answer rate with a citation rate of zero.
