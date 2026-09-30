# Integrating the dotFIT assistant (SSE)

For the website engineering team: the contract between your server and
`dotfit-service`. Build against this document, not against the code.

**Deployment shape.** Your server calls the service, relays the stream to the
browser, and your database is the system of record for the conversation. The
service holds no state between requests and stores nothing.

**What the assistant is.** One model that decides what to look up in dotFIT's
own material, looks it up as many times as it needs, and answers. Answer text
streams live; nothing is held back, checked after the fact, or retracted.

---

## Endpoints

### `GET /healthz`

Unauthenticated on purpose, so it works as your liveness and readiness check.
Returns `200` with the service's configuration, hardening posture and budgets:

```json
{
  "status": "ok",
  "runtime": "dotfit-assistant",
  "index": "kb-main-v2",
  "prompt_variant": "default",
  "prompt_version": "e42cfc0ca669",
  "chat_deployment": "...",
  "embedding_deployment": "...",
  "auth": "shared-secret",
  "max_question_chars": 2000,
  "timeout_seconds": 120,
  "debug_transcript": "off",
  "max_tool_calls": 8,
  "turn_timeout_seconds": 60,
  "default_top": 6,
  "price_sheet": "builtin-2026-09",
  "price_currency": "USD",
  "price_chat_usd_per_1m": {"input": 1.25, "cached_input": 0.125, "output": 10},
  "price_embedding_usd_per_1m": 0.13,
  "price_search_usd_per_1k": 0.25,
  "gating": "none"
}
```

`prompt_variant` and `prompt_version` name the exact system prompt the service
is running; the version changes whenever its text does. If you report an odd
answer to us, the `prompt_version` at the time is useful.

The `price_*` fields are the sheet behind `result.cost` (see below), exposed so
anyone can check what a turn is priced against. `price_search_usd_per_1k` is a
**placeholder** until a real cost-per-query is derived from billing data — read
`cost.search.queries` as the reliable number and its `usd` as provisional.

The service validates its whole configuration at **startup**: a missing key,
deployment, shared secret or broken prompt fails the boot rather than the first
question. If it is up, it is configured.

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
| `request_id` | no | Your id for this one turn. We echo it on `result` and `error` and key our turn log on it. If you leave it out we mint one and echo that. Maximum 64 characters. |
| `history` | no | Earlier turns, oldest first, **not including** `question`. See Multi-turn. |
| `top` | no | Sources returned per search. Default 6, maximum 20 (outside that is a `400`). Leave it unset unless we ask. It is *per search*, and the assistant may search several times, so a turn can end with more sources than `top`. |

**Every rejection is a status code, never an event.** All validation happens
before the stream opens, because the first SSE frame commits the response to
`200`. Auth is checked first, before the body is read, so a request without the
secret is always `401`. After that: `415` if the content type is not
`application/json`, `413` if the body is over 256 KB, and `400` for a body that
is not valid JSON or breaks a rule below. A `401` has no body; the others carry
`{"error": "..."}`. None has `event:` lines; once you see the first `event:`,
the request was accepted.

Response headers are `text/event-stream`, `no-cache`, `X-Accel-Buffering: no`.
**Check whatever proxy sits in front of you**: a proxy that buffers the body
turns live streaming back into one lump at the end.

---

## The event contract

Event names are the contract. Key on them; do not parse the prose.

| Event | Payload | Notes |
|---|---|---|
| `disclosure` | `{text}` | AI-identity notice. Emitted **once**, only when the request had no `conversation_id`. Render it before any answer text. |
| `stage` | `{stage, detail}` | What the assistant is doing. `stage` is one of `thinking`, `search`, `fetch`, `product`, `guide`, `answer`. **Stages repeat** — three searches emit three `search` events. |
| `source` | `{n, source_type, authority, title, citation_url, locator, quotable, part_nos}` | A numbered source, emitted when it is assigned its number and before any delta cites it. |
| `delta` | `{text}` | A fragment of answer text, live. Concatenate in arrival order. |
| `result` | see below | Final, assembled. **Always last**, including after an `error`. |
| `error` | `{request_id, kind, message}` | The turn failed. Followed by `delta`s carrying a handoff message, then `result`. |

### `stage`, and a UI decision for you

| `stage` | Meaning | `detail` |
|---|---|---|
| `thinking` | The turn has started. | — |
| `search` | Searching dotFIT's material. | The query text the assistant chose. |
| `fetch` | Reading the rest of a document. | A document id. |
| `product` | Reading a product's approved copy. | The product name it asked for. |
| `guide` | Reading dotFIT's own guidance: the supplement program guide or the customer-service FAQ. Produces no `source`. | The guidance's title, e.g. `Customer service FAQ`. |
| `answer` | The answer has started streaming. | — |

`detail` is the conversational texture that replaces a progress bar. For
`search` it is **model-generated text** (asked "how much creatine should I
take?", it searched for `recommended daily creatine dose and whether loading is
needed`), so it is your call whether to render it verbatim, render a generic
label per stage type ("Searching dotFIT's guides…"), or render nothing. Any of
the three is a supported integration. If you render it verbatim, treat it as
untrusted text and escape it like any other.

A lookup's `stage` frame reaches you when that lookup *starts*, so it leads the
work it describes; the `source` frames and the answer follow behind it. A
spinner keyed on `stage` fills the wait.

### `source`, and inline citations

Sources are numbered per turn, starting at 1, in the order the assistant
retrieves them. **A number never changes meaning inside a turn**, and the
numbering keeps counting across searches — a second search does not restart at
1. A source retrieved twice keeps its first number and is sent once.

**A product page is one source.** dotFIT's approved product copy is stored as
sections of a page; every section of a page shares one number and one `source`
frame, so a product is one entry in your list.

The ordering guarantee is the useful part: a `source` frame for `[3]` always
precedes any `delta` containing the text `[3]`. Build the source list
incrementally and resolve citation markers as they stream. Numbers appear in the
text as `[1]`, `[2]`; your client renders the list.

`quotable` is `true` for authority 1–2 (dotFIT's approved product copy, the
official website pages, and the practitioner reference guide) and `false` for
3–5 (customer Q&A, podcasts, menus). It says which sources may carry
product-claim wording — useful if you ever want to style a citation to approved
copy differently from one to a podcast.

`part_nos` is the list of dotFIT part numbers (SKUs) our catalog tags that
source with — empty for sources with no product tag (some website pages,
menus). It is index metadata, not something the model wrote, so a SKU on the
wire is one the catalog attests. Read it as "this source is about these SKUs",
a mild over-approximation of "the answer discussed them". If you want "the
products this answer leaned on", union `part_nos` over the sources listed in
`cited`; we deliberately do not send that union.

**Answers from dotFIT's own guidance carry no number.** When the assistant
answers from the program guide or the customer-service FAQ (a `guide` stage),
it uses that guidance in its own words and cites nothing for it. An answer about
returns or a supplement program can therefore arrive with an empty `cited` list;
that is correct.

### The `result` payload

```json
{
  "request_id": "turn-8c2e...",
  "answer": "the delivered text, whole",
  "sources": [
    {"n": 1, "source_type": "pdsrg", "authority": 2, "title": "...",
     "citation_url": "...", "locator": "p. 20", "quotable": true,
     "part_nos": ["2101", "2102"]}
  ],
  "cited": [1],
  "tool_calls": 1,
  "budget_exhausted": false,
  "first_delta_ms": 4850,
  "total_ms": 5540,
  "cost": {
    "currency": "USD",
    "price_sheet": "builtin-2026-09",
    "chat": {"input_tokens": 18432, "cached_input_tokens": 9216,
              "output_tokens": 1204, "usd": 0.0483},
    "embedding": {"calls": 2, "tokens": 61, "usd": 0.0000079},
    "search": {"queries": 3, "ranker_queries": 0, "usd": 0.00075},
    "total_usd": 0.0490579
  }
}
```

`answer` is the same text the `delta`s carried, assembled — use it as the
authoritative record rather than your own concatenation.

`cited` lists the source numbers the answer actually uses; `sources` lists
everything it was given. The two differ routinely: the assistant retrieves more
than it ends up needing. **Render the cited ones**; a source list showing six
entries when the answer leaned on one reads as padding.

`tool_calls` and the two timings show how hard a turn worked.
`budget_exhausted: true` means the assistant hit its research limit and answered
with what it had — rare, not something to surface to a customer, worth logging.

**`cost` is what the turn spent, on every Azure call it made.** The counts are
observed: token counts from the model's own usage reports (summed across round
trips, so a tool-calling turn's re-sent context is fully counted;
`cached_input_tokens` is the discounted subset of `input_tokens`), embedding
calls and tokens from the embedding API, and `search.queries` counting every
index call the turn made (searches, product-copy lookups, document fetches — one
each, plus one per neighbour a fetch probed). The `usd` figures are those
counts priced against the sheet named in `price_sheet`, the same sheet
`/healthz` exposes. Two caveats: the **search rate is a placeholder**, and the
numbers are priced, not invoiced — Azure's invoice is the authority if the two
ever disagree. `cost` is absent only on the service-timeout handoff, where the
turn's usage was lost with the cancelled request. `total_usd` is the headline
number.

`request_id` is the one you sent, or the one we minted. **Store it on the
turn**: it is the only key joining your record of what was said to our record of
what was done.

Deliberately **not** on the wire: retrieved source `content`, and the arguments
of `fetch`/`get_product` calls. Those are operator diagnostics.

---

## Two outcomes to handle

1. **Answered** — the normal case. Text, usually citations, sometimes neither.
2. **Errored** — an `error` event arrived. The customer has already been given a
   handoff message to read. Log `kind`, treat the turn as failed, **do not
   retry**.

Shapes that are ordinary answers, not separate outcomes:

- **A conversational turn.** "Hi there", "thanks", "what can you do?" — the
  assistant replies without searching. `sources` and `cited` are empty,
  `tool_calls` is `0`, and it comes back in a second or two.
- **A customer-service question.** How to return something, when shipping is
  free, how to change a recurring order, how a trainer adds a client, how
  certification or club partnerships work — answered from dotFIT's
  customer-service FAQ, with no citation. Fixed policy figures (shipping
  thresholds, plan and certification fees, restocking fee, discounts) are stated.
- **Something only support can do.** Order status, cancelling or changing an
  order or subscription, a billing problem — the assistant cannot act on an
  account. It says so in a sentence, says where in My Account the customer can
  do it themselves when that applies, and points them at dotFIT support.
- **A product price.** The assistant never states what a product costs, because
  pricing is per customer; it points the customer to the price shown on the
  website or in their account.
- **A medical escalation.** See below.
- **An off-topic request.** The assistant is not a general chat and will not
  write, code or answer trivia. It says so briefly, offers what it can help
  with, and does not search.

---

## Safety

The assistant handles sensitive cases *inside the answer*, conversationally:
pregnancy or breastfeeding, a managed medical condition, prescription medication
or interactions, signs of disordered eating, a child under 12, extreme calorie
or weight targets, and any sign of self-harm. It acknowledges what the customer
said, answers what is safe to answer, names the limit once, and routes them to
dotFIT support or their own healthcare professional. It does not refuse the
whole turn.

Within that:

- **Teenagers (12–17) get real answers** within dotFIT's youth guidance: a
  multivitamin, protein, omega-3 and calcium as needed; creatine only for
  post-pubescent athletes around 16+ with a parent's approval; no pre-workouts,
  glutamine or weight-loss products.
- **Programs, menus and recipes.** A customer with a medical condition, on
  medication or pregnant can still be given dotFIT's restricted program baseline
  (a multivitamin and protein, and to tell their doctor) and general menu and
  recipe guidance. Anything individualized beyond that is routed.

**There is no machine-readable escalation flag.** Safety handling is a strong
tendency of the assistant, not a hard guarantee, and it is not visible to your
code. If your product needs to detect these turns — a crisis-support panel, a
human notification, suppressing an upsell — tell us rather than matching on
answer text. **Do not match on answer text**; the wording is not a contract.

The support route in the text is `support@dotfit.com or (877) 436-8348`, the
pair dotFIT publishes. It is configuration on our side; tell us if an audience
should be sent somewhere else.

**What the assistant can see** is the transcript you send, and only that. A
trigger held by your own product surface outside the question text — an age
field, a profile flag, an intake form — is invisible to it. If you hold anything
from the list above outside the conversation, put it in a turn of the history
you send or handle it on your side.

**Never edit the delivered text.** It is the record of what the customer was
told.

---

## Multi-turn

Send the recent turns with each request. Your database holds them; the service
stores nothing between requests.

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
| How many | Send what you have. We keep the most recent **8 turns** and the first **1,000 characters** of each; anything past that is trimmed on our side. |

The whole conversation reaches the model, so it can refer back to what it said
earlier, and something the customer stated earlier (an age, a pregnancy) still
applies. It cannot cite a source from an earlier turn — numbers are turn-scoped
and it no longer holds that material — so a follow-up may search again and take
as long as the original question.

---

## Operational notes

**Auth.** `POST /ask` requires a shared secret as a bearer token:

```
Authorization: Bearer <the secret we give you>
```

A missing, malformed or wrong value is `401` with no body detail and no stream.
`GET /healthz` is unauthenticated by design. We hand you the secret out of band;
treat it as a credential and tell us if it needs rotating. The service **fails
to start** without it, so there is no state in which it is running and open.

This is the boundary, not defence in depth: the service expects to sit on a
private network reachable only by your server. **Do not expose it to the public
internet**, with or without the secret. If your platform terminates mTLS in
front of it, say so and we will run it with auth explicitly disabled rather than
have two half-configured mechanisms.

**Limits.** A question over **2,000 characters** is a `400`, not a truncation.
`top` is 1–20. History needs no trimming on your side. The request body is
capped at 256 KB (`413` above that).

**Timeouts and cancellation.** If the client hangs up, the stream is cancelled
and the turn is abandoned. Our ceiling is **120 seconds** per request, reported
by `/healthz`, and the assistant has two tighter internal ones: it stops
*researching* at 60 seconds and stops entirely at 110, both of which end in an
answer rather than a timeout. When the outer ceiling does fire you get `error`
plus the handoff `delta`, the same shape as any other failure, rather than a
stream that silently stops.

Set your own client timeout comfortably above ours. First text typically arrives
in **1–2 seconds** when no lookup is needed, **2–3 seconds** for a
customer-service answer, and **4–7 seconds** when the assistant searches or
builds a supplement program; a turn that looks up several products can take
longer.

**Errors.** Any exception yields `error` plus a handoff `delta` plus `result`,
never a stack trace, never a bare stream end. `kind` is the exception type;
`message` is deliberately generic, because the real one can name a deployment.
If the failure lands *after* some answer text has streamed, the handoff is
appended to it rather than replacing it, and `result.answer` carries both — so
`answer` is always the whole of what the customer saw.

**Logging.** Your database is the system of record for transcripts. Worth
storing per turn from `result`: `request_id`, `cited`, `sources`, `tool_calls`,
`first_delta_ms` and `cost`.

**Our turn log.** We write one structured line per request recording what the
assistant *did*: how many tool calls, which tools, the search queries it chose,
which of dotFIT's guidance documents it read and their versions, how many
sources it retrieved and cited, which authority tiers those were, which product
families came up, the prompt it ran on, the timings, and the outcome. Keyed by
`request_id`.

It holds **no question text and no answer text**, by construction — there is no
field to put them in. The one text-bearing field is the assistant's own search
queries: model text, which can be close in substance to a question without
being the customer's words.

You hold what was said, we hold what was done, and `request_id` joins them. So
the two ids are the one thing you send that we keep: **put an opaque identifier
in them, never anything a customer typed.** Both are capped at 64 characters.

A request rejected with `400` or `401` never reaches the assistant and produces
no log line; you see it synchronously as a status code.

**Rate.** One trusted caller, so no rate limiting. Every request costs at least
one model call and often several; a retry loop on failure is an expensive
mistake.

---

## A standing caveat

This applies to every preview release: the preview is tested continuously, at
whatever state it is in. Please keep this in front of whoever briefs the
audience.

- **Nothing checks the answer before it reaches the customer.** What keeps it
  on track is the assistant's instructions and the shape of what it can reach:
  it has no source for a dotFIT fact other than dotFIT's own material, and
  quoting approved copy is the cheapest path available to it. That is a good
  design and it is not a guarantee.
- **Medical escalations are handled conversationally**, not by a blocking check
  (see Safety).

Stakeholders and partners should be told plainly that this is an early build
being tested in the open, and asked to report **anything that reads like a
product claim** and **anything that reads like medical advice**. dotFIT's
approved product copy is the only source of claim language; if the assistant's
phrasing differs from it, the approved copy is right.

Keep your per-turn record of `request_id` and what was delivered: joined to our
turn log, it is how either side reconstructs what the assistant told someone,
without keeping a second copy of the conversation anywhere.

If anyone in the preview audience may quote the assistant in external material,
tell us before it happens. It is a materially different risk.
