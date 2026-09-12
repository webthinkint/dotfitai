# Integrating the dotFIT knowledge assistant (SSE) — agentic runtime

For the website engineering team. This is the contract between your server and
`dotfit-agentic-service`.

**Read this first if you already integrated the previous runtime.** The
transport, the auth and the request body are unchanged. The *event stream* is
not: answer text now streams live, the `retraction` event is gone, a `source`
event is new, and the `result` payload has lost several fields your UI may be
branching on. Section "What changed from the previous runtime" is the migration
list; everything after it is the full contract, standalone.

**Deployment shape this document assumes**, unchanged: your server calls the
service, your server relays the stream to the browser, and your database is the
system of record for the conversation. The service holds no state between
requests and stores nothing.

**Status.** Built and verified live end to end (2026-09-12). Not yet run in
front of an audience. Anything agreed but not implemented is marked **planned**
in place — do not build against it until we tell you it has landed.

---

## What changed from the previous runtime

The assistant is no longer a fixed pipeline that decides in advance what to
retrieve and then checks its own answer before releasing it. It is now one model
that chooses what to look up, looks it up as many times as it needs, and answers.
Four consequences reach your code.

| # | Change | What you do |
|---|---|---|
| 1 | **`delta` events stream live.** Text arrives token by token as it is generated. Nothing is buffered. | Build the typing effect you were previously told *not* to build. Remove any code that waits for the stream to finish before rendering. |
| 2 | **`retraction` is gone. So is `withheld`.** Nothing is held back and nothing is un-rendered. | Delete the retraction handler and the withheld branch. They will never fire; the event name is retired, not reused. |
| 3 | **`source` is a new event**, emitted as each source is numbered and **before any text that cites it**. | Accumulate sources as they arrive. You can now resolve `[3]` into a link the moment it appears mid-sentence, which was impossible before. |
| 4 | **`escalated` is gone from `result`.** Medical escalations are no longer a separate machine-readable outcome — the assistant handles them conversationally, inside the answer text. | **This is the one that needs a decision on your side.** See "Safety, and what you can no longer detect". |

Also gone from `result`: `post_check`, `repaired`, `citations`,
`rendered_citations`, `question`. Added: `cited`, `tool_calls`,
`budget_exhausted`, `first_delta_ms`, `total_ms`.

Unchanged: the endpoints, the auth scheme, the request body, `conversation_id`
and the disclosure rule, the `history` contract, every limit, the timeout
behaviour, the error shape, and the fact that we keep no transcript.

---

## Endpoints

### `GET /healthz`

Unauthenticated on purpose, so it works as your liveness and readiness check.
Returns `200` with the index name, the deployments it is configured against, its
hardening posture, and the assistant's own budgets:

```json
{
  "status": "ok",
  "runtime": "agentic",
  "index": "kb-main-v2",
  "chat_deployment": "...",
  "embedding_deployment": "...",
  "auth": "shared-secret",
  "max_question_chars": 2000,
  "timeout_seconds": 120,
  "debug_transcript": "off",
  "max_tool_calls": 8,
  "turn_timeout_seconds": 60,
  "default_top": 6,
  "gating": "none"
}
```

`"runtime": "agentic"` and `"gating": "none"` are how you tell which of the two
runtimes you are pointed at — worth asserting in a smoke check if both are
deployed anywhere.

The service validates its whole configuration at **startup** — a missing key,
deployment or shared secret fails the boot rather than the first question. If it
is up, it is configured.

### `POST /ask`

One customer question in, a Server-Sent Events stream out. Requires the shared
secret; without it you get `401` and no stream.

```json
{
  "question": "How much creatine should I take?",
  "conversation_id": "1b9f...",
  "request_id": "turn-8c2e...",
  "history": [
    {"role": "user", "text": "is LeanMeal good for weight loss?"},
    {"role": "assistant", "text": "..."}
  ],
  "top": 8
}
```

| Field | Required | Meaning |
|---|---|---|
| `question` | yes | The customer's question, verbatim. Empty or whitespace gets `400`, and so does anything over **2,000 characters**. |
| `conversation_id` | no | **Absent means "new conversation"**, which is what controls the disclosure. Send a stable id for every turn after the first. Maximum 64 characters. |
| `request_id` | no | Your id for this one turn. We echo it on `result` and `error` and key our turn log on it. If you leave it out we mint one and echo that instead. Maximum 64 characters. |
| `history` | no | Earlier turns, oldest first, **not including** `question`. See Multi-turn. |
| `top` | no | Sources returned per search. Default 6, maximum 20 (outside that is a `400`). Leave it unset unless we ask. Note this is now *per search* and the assistant may search several times, so a turn can end with more sources than `top`. |

**Every rejection is a status code, never an event.** All validation happens
before the stream opens, because the first SSE frame commits the response to
`200`. A `400`/`401` has a JSON body (`{"error": "..."}` for `400`) and no
`event:` lines at all; once you see the first `event:`, the request was accepted.

Response headers are `text/event-stream`, `no-cache`, `X-Accel-Buffering: no`.
**Check whatever proxy sits in front of you.** Buffering was a cosmetic problem
before, when text arrived in one burst anyway; now it defeats the entire point.

---

## The event contract

Event names are the contract. Key on them; do not parse the prose.

| Event | Payload | Notes |
|---|---|---|
| `disclosure` | `{text}` | AI-identity notice. Emitted **once**, only when the request had no `conversation_id`. Render it before any answer text. |
| `stage` | `{stage, detail}` | What the assistant is doing. `stage` is one of `thinking`, `search`, `fetch`, `product`, `answer`. **Stages repeat** — three searches emit three `search` events. |
| `source` | `{n, source_type, authority, title, citation_url, locator, quotable}` | A numbered source, emitted when it is assigned its number and before any delta cites it. |
| `delta` | `{text}` | A fragment of answer text, live. Concatenate in arrival order. |
| `result` | see below | Final, assembled. **Always last**, including after an `error`. |
| `error` | `{request_id, kind, message}` | The turn failed. Followed by `delta`s carrying a handoff message, then `result`. |

### `stage`, and a UI decision for you

`detail` carries what the assistant is doing in its own words — for a `search`
stage, the query text it chose. A real example: asked "how much creatine should
I take?", it searched for `recommended daily creatine dose and whether loading is
needed`.

That is the conversational texture that replaces a progress bar, and rendering
it ("Looking up: recommended daily creatine dose…") is what we designed for. But
**it is model-generated text going straight to a customer**, so it is your call
whether to render it verbatim, render a generic label per stage type
("Searching dotFIT's guides…"), or render nothing. Any of the three is a
supported integration. If you render it verbatim, treat it as untrusted text and
escape it like any other.

### `source`, and inline citations

Sources are numbered per turn, starting at 1, in the order the assistant
retrieves them. **A number never changes meaning inside a turn**, and the
numbering keeps counting across searches — a second search does not restart at
1. A source retrieved twice keeps its first number and is sent once.

The ordering guarantee is the useful part: a `source` frame for `[3]` always
precedes any `delta` containing the text `[3]`. So you can build the source list
incrementally and resolve citation markers as they stream, rather than
rewriting the rendered answer at the end.

`quotable` is `true` for authority 1–2 (dotFIT's approved product copy, the
official website pages, and the practitioner reference guide) and `false` for
3–5 (customer Q&A, podcasts, menus). It tells you which sources may carry
product-claim wording. You do not have to surface it, but it is the field to
use if you ever want to style a citation to approved copy differently from one
to a podcast.

Numbers appear in the text as `[1]`, `[2]`. There is no `rendered_citations`
field any more — the assistant emits inline markers and your client renders the
list, which is what makes live streaming worth having.

### The `result` payload

```json
{
  "request_id": "turn-8c2e...",
  "answer": "the delivered text, whole",
  "sources": [
    {"n": 1, "source_type": "pdsrg", "authority": 2, "title": "...",
     "citation_url": "...", "locator": "p. 20", "quotable": true}
  ],
  "cited": [1],
  "tool_calls": 1,
  "budget_exhausted": false,
  "first_delta_ms": 5809,
  "total_ms": 5820
}
```

`answer` is the same text the `delta`s carried, assembled — use it as the
authoritative record rather than your own concatenation.

`cited` lists the source numbers the answer actually uses. `sources` lists
everything it was given. The two differ routinely and that is normal: the
assistant retrieves more than it ends up needing. **Render the cited ones**; a
source list showing six entries when the answer leaned on one reads as padding.

`tool_calls` and the two timings are there so you can see how hard a turn
worked. `budget_exhausted: true` means the assistant hit its research limit and
answered with what it had — rare, and not something to surface to a customer,
but worth logging.

`request_id` is the one you sent, or the one we minted. **Store it on the turn.**
It is the only key joining your record of what was said to our record of what
was done.

Note what is deliberately **not** on the wire: retrieved source `content`, and
the arguments of `fetch`/`get_product` calls. Those are operator diagnostics.

---

## Two outcomes to handle

Down from four.

1. **Answered** — the normal case. Text, usually citations, sometimes neither.
2. **Errored** — an `error` event arrived. The customer has already been given a
   handoff message to read. Log `kind`, treat the turn as failed, **do not
   retry**.

Shapes that are *not* separate outcomes any more, and which your UI should treat
as ordinary answers:

- **A conversational turn.** "Hi there", "thanks", "what can you do?" — the
  assistant replies without searching. `sources` and `cited` are empty,
  `tool_calls` is `0`, and it comes back in about two seconds. That is correct,
  not a failed retrieval. If your UI hides the citation block when the list is
  empty it already does the right thing.
- **A medical escalation.** See below.
- **An off-topic request.** Order status, returns, billing. The assistant says
  briefly that it cannot help and points at dotFIT support. It is an answer.

---

## Safety, and what you can no longer detect

**This section is the most important change and needs a decision on your side.**

The previous runtime ran a classifier before answering and returned
`escalated: true` on a machine-readable flag when a question touched the
escalation list — pregnancy, a managed condition, medication interactions, a
minor, eating-disorder signals, extreme calorie targets, self-harm. You could
branch on it: badge the turn, show a support panel, suppress a feature.

**That flag no longer exists.** The assistant handles those cases *inside the
answer*, conversationally: it acknowledges what the customer said, answers what
is safe to answer, names the limit in a sentence, and routes them to dotFIT
support or their own healthcare professional. This was a deliberate owner
decision — a templated refusal is not care, and the old behaviour refused far
more often than it needed to.

The trade, stated plainly: **safety handling is now a strong tendency rather
than a hard guarantee**, and it is not visible to your code. If your product
relies on detecting these turns — to show a crisis-support panel, to notify a
human, to suppress upsell, to comply with something on your side — tell us. The
fix is an out-of-band classifier that flags the turn *after* the answer is
delivered, which costs no latency and which we have designed but not built. It
is a short piece of work and we would rather do it than have you infer the case
by matching on answer text. **Do not do that**; the wording is not a contract.

The handoff route in the text is `support@dotfit.com or (877) 436-8348`, the
pair the practitioner reference guide publishes. It is configuration on our
side — tell us if the preview audience should be sent somewhere else and we
change it in one place rather than have you rewrite delivered copy.

**Never edit the delivered text.** It is the record of what the customer was
told.

---

## Multi-turn

Send the recent turns with each request. Your database already holds them, and
the service still stores nothing between requests.

```json
{
  "question": "what about the chocolate one?",
  "conversation_id": "1b9f...",
  "history": [
    {"role": "user", "text": "is LeanMeal good for weight loss?"},
    {"role": "assistant", "text": "..."}
  ]
}
```

| Rule | Value |
|---|---|
| Order | Oldest first. |
| `role` | `user` or `assistant`, case-insensitive. Anything else is a `400` — we reject rather than skip, because a transcript with a hole in it resolves follow-ups wrongly. |
| `text` | The turn as the customer saw it. For an assistant turn use the delivered `answer` from that turn's `result`. |
| Do **not** include | The question you are asking now. If you do, we drop the duplicate rather than read it as the customer asking twice. |
| How many | Send what you have, up to a few exchanges. We keep the most recent **8 turns** and the first **1,000 characters** of each; anything past that is trimmed on our side. |

**What changed here.** The whole conversation now reaches the model, not just
two internal stages. Practically: it *can* refer back to what it said two turns
ago, which the previous runtime could not. What it cannot do is cite a source
from an earlier turn — those numbers are turn-scoped and it no longer holds the
material, so it will look it up again. A follow-up may therefore re-search and
take as long as the original question. That is a known cost and we may change it
later by holding session state; if we do, it is a contract change and you will
hear about it first.

**What the assistant can and cannot see.** The transcript you send, and only
that. A trigger stated in a turn you trimmed away, or held by your own product
surface outside the question text — an age field, a profile flag, an intake
form — is invisible to us. If you hold anything from the escalation list outside
the conversation, put it in a turn of the history you send or handle it on your
side. Do not assume we can infer it.

---

## Operational notes

**Auth.** `POST /ask` requires a shared secret as a bearer token:

```
Authorization: Bearer <the secret we give you>
```

A missing, malformed or wrong value is `401` with no body detail and no stream.
`GET /healthz` is unauthenticated by design.

We hand you the secret out of band; treat it as a credential (your secret store,
not your repo) and tell us if it needs rotating. The service **fails to start**
without it, so there is no state in which it is running and open.

This is the boundary, not defence in depth: the service expects to sit on a
private network reachable only by your server. **Do not expose it to the public
internet**, with or without the secret. If your platform terminates mTLS in
front of it, say so and we will run it with auth explicitly disabled rather than
have two half-configured mechanisms.

**Limits.** A question over **2,000 characters** is a `400`, not a truncation.
`top` is 1–20. History needs no trimming on your side. The request body is
capped at 256 KB.

**Timeouts and cancellation.** If the client hangs up, the stream is cancelled
and the turn is abandoned. We impose our own ceiling — **120 seconds** per
request, reported by `/healthz` — and the assistant has two tighter internal
ones: it stops *researching* at 60 seconds and stops entirely at 110, both of
which end in an answer rather than a timeout. When the outer ceiling does fire
you get `error` plus the handoff `delta`, the same shape as any other failure,
rather than a stream that silently stops.

Set your own client timeout comfortably above ours. **First text typically
arrives in 1.5–2.5 seconds when no lookup is needed and around 5–6 seconds when
the assistant searches**; those are early numbers from a handful of live turns,
not a measured distribution, and we are still working on the second one.

**Errors.** Any exception yields `error` plus a handoff `delta` plus `result`,
never a stack trace, never a bare stream end. `kind` is the exception type;
`message` is deliberately generic, because the real one can name a deployment.

**Logging.** Your database is the system of record for transcripts. Worth
storing per turn from `result`: `request_id`, `cited`, `sources`, `tool_calls`
and `first_delta_ms`.

**Our turn log.** We write one structured line per request recording what the
assistant *did* — how many tool calls, which tools, the search queries it chose,
how many sources it retrieved and cited, which authority tiers those were, which
product families came up, the timings, and the outcome. Keyed by `request_id`.

It holds **no question text and no answer text**, by construction — not
redacted, simply not collected; there is no field to put them in. The one
text-bearing field is the assistant's own search queries, which the owner has
reviewed and accepted (2026-09-12): they are model text and can be close in
substance to a question without being the customer's words.

That is the deal that makes the split work: you hold what was said, we hold what
was done, and `request_id` joins them when someone asks weeks later why a
particular turn did what it did. Which is why the two ids are the one thing you
send that we keep — **put an opaque identifier in them, never anything a
customer typed.** Both are capped at 64 characters.

One gap: a request rejected with `400` or `401` never reaches the assistant, so
it produces no log line. Those are transport rejections and you see them
synchronously as a status code.

**Rate.** One trusted caller, so no rate limiting. Every request costs at least
one model call and often several; a retry loop on failure is an expensive
mistake.

---

## A standing caveat, not a first-release one

**This applies to every preview release.** By owner ruling, the preview is never
held back for a measurement — it is meant to be tested continuously, at whatever
state it is in. Please keep this in front of whoever briefs the audience, each
time.

Two things are open, and both concern **claim wording and safety phrasing**
rather than retrieval:

- **Nothing checks the answer before it reaches the customer.** The previous
  runtime ran a compliance audit and withheld answers that failed it. That audit
  caught almost none of the violations it was meant to and withheld a great many
  answers that were fine, so it was removed. What replaces it is the assistant's
  instructions and the shape of what it can reach: it has no source for a dotFIT
  fact other than dotFIT's own material, and quoting approved copy is the
  cheapest path available to it. That is a good design and it is not a guarantee.
- **Medical escalations are handled conversationally, not by a blocking check**
  (see Safety above).

Retrieval accuracy — does it find the right material — is measuring well and is
unchanged from the previous runtime; it is the same index and the same search.

Stakeholders and partners should be told plainly that this is an early build
being tested in the open, and asked to report **anything that reads like a
product claim** and **anything that reads like medical advice**. dotFIT's
approved product copy is the only source of claim language; if the assistant's
phrasing differs from it, the approved copy is right and the assistant is wrong.

Keep your per-turn record of `request_id` and what was delivered. Since any
state may ship, that record joined to our turn log is how either side
reconstructs what the assistant told someone, weeks later, without keeping a
second copy of the conversation anywhere.

If anyone in the preview audience may quote the assistant in external material —
as opposed to testing it internally — tell us before it happens. It is a
materially different risk and the briefing should say so.

---

## Migration checklist

For a client already speaking the previous contract:

- [ ] Remove the `retraction` handler and the `withheld` branch.
- [ ] Stop waiting for the stream to end before rendering; render `delta`s live.
- [ ] Add a `source` handler; build the source list incrementally.
- [ ] Resolve `[n]` markers against sources already received.
- [ ] Replace `result.citations` / `rendered_citations` with `result.cited` +
      `result.sources`.
- [ ] Remove any branch on `result.escalated` or `result.post_check` — and tell
      us if you needed the escalation flag (see Safety).
- [ ] Handle repeated `stage` events; decide how to render `stage.detail`.
- [ ] Assert `"runtime": "agentic"` in your `/healthz` check if both runtimes
      are deployed anywhere.
- [ ] Verify no proxy between you and the service buffers the response body.
