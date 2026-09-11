# Integrating the dotFIT knowledge assistant (SSE)

For the website engineering team. This is the contract between your server and
`dotfit-agent-service`, the §11 runtime behind an HTTP endpoint.

**Deployment shape this document assumes**, as agreed: your server calls the
service, your server relays the stream to the browser, and your database is the
system of record for the conversation. The service holds no state between
requests and stores nothing. The first release is for stakeholders and approved
partners, not public traffic.

**Status.** Everything in this document is built and live, including multi-turn
and the service hardening — auth, limits, request timeout, support route —
(2026-09-10). Anything agreed but not yet implemented is marked **planned** in
place — do not build against it until we tell you it has landed.

---

## Endpoints

### `GET /healthz`

Returns `200` with the index name, the three deployment names it is configured
against, and its hardening posture — `auth` (`shared-secret` or `none`),
`max_question_chars` and `timeout_seconds`. Deployment names and limits are
configuration, not secrets; no key is ever read on this path and the shared
secret is never rendered. **Unauthenticated on purpose**, so it works as your
liveness and readiness check — and so `"auth": "none"` on a deployment that was
meant to require a secret is visible rather than silent.

The service validates its whole configuration at **startup** — a missing key or
deployment fails the boot rather than the first question, and so does a missing
shared secret. If it is up, it is configured.

### `POST /ask`

One customer question in, a Server-Sent Events stream out. Requires the shared
secret (see Auth below); without it you get `401` and no stream.

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
| `question` | yes | The customer's question, verbatim. Empty or whitespace gets `400`, and so does anything over **2,000 characters** — see Limits. |
| `conversation_id` | no | **Absent means "new conversation"**, which is what controls the disclosure — see Disclosure below. Send a stable id for every turn after the first. Maximum 64 characters. |
| `request_id` | no | Your own id for this one turn. We echo it on `result` and `error` and key our verdict log on it — see Our verdict log. If you leave it out we mint one and echo that instead, so you can file it either way. Maximum 64 characters. |
| `history` | no | Earlier turns of this conversation, oldest first, **not including** `question`. See Multi-turn. Absent or empty is a standalone question. |
| `top` | no | Sources retrieved and fed to the answer. Default 8, maximum 20 (outside that is a `400`). Leave it unset unless we ask you to change it. |

**Every rejection is a status code, never an event.** All validation happens
before the stream opens, because the first SSE frame commits the response to
`200` and there is no status code left to reject with. So a `400`/`401` response
has a JSON body (`{"error": "..."}` for `400`) and no `event:` lines at all; once
you see the first `event:`, the request was accepted.

Response headers are `text/event-stream`, `no-cache`, and
`X-Accel-Buffering: no`. If anything between you and the service buffers, the
stage events arrive in one lump at the end and the streaming is pointless —
worth checking on whatever proxy sits in front.

---

## The event contract

Event names are the contract. Key on them; do not parse the prose.

| Event | Payload | Notes |
|---|---|---|
| `disclosure` | `{text}` | AI-identity notice. Emitted **once**, only when the request had no `conversation_id`. Render it before any answer text. |
| `stage` | `{stage, detail}` | Pipeline progress. `stage` is one of `guardrail`, `rewrite`, `aliases`, `search`, `answer`, `post-check`. |
| `delta` | `{text}` | A fragment of answer text. Concatenate in arrival order. |
| `retraction` | `{reason, mode}` | The post-check failed. See Gating. |
| `result` | see below | Final, assembled. **Always last.** |
| `error` | `{request_id, message, kind}` | The pipeline threw. Terminal, and always followed by `delta`s carrying a handoff message. |

`stage` events exist so you have something to render while the answer is held.
That is the whole reason gating is affordable — see below.

### The `result` payload

```json
{
  "request_id": "turn-8c2e...",
  "question": "...",
  "escalated": false,
  "withheld": false,
  "answer": "the delivered text, whole",
  "citations": [{"index": 1, "source_id": "product-...", "title": "..."}],
  "rendered_citations": "a formatted citation block",
  "sources": [{"id": "...", "source_type": "product", "authority": 1,
               "title": "...", "citation_url": "...", "locator": "..."}],
  "post_check": {"passed": true, "n_failures": 0}
}
```

`answer` is the same text the `delta`s carried, assembled — use it as the
authoritative record rather than your own concatenation.

`request_id` is the one you sent, or the one we minted if you did not. **Store
it on the turn.** It is the only key that joins your record of what was said to
our record of what was decided — see Our verdict log.

Note what is deliberately **not** on the wire: retrieved source `content`, the
post-check's failure reasons, and, when an answer is withheld, the draft that
was withheld. Those are operator diagnostics. `post_check.n_failures` tells you
*that* something fired without telling a customer what.

---

## Gating — the behaviour that matters most

The service always runs in `Gated` mode and does not offer a choice.

In `Gated` mode, **no `delta` is emitted until the post-check has passed.** When
it passes, the answer arrives in one burst. When it fails, you get a
`retraction` and then `delta`s carrying a handoff message — the customer never
saw a word of the draft, so nothing has to be un-rendered.

This is a deliberate §11 ruling: streaming live means a claims-compliance
failure cannot retract text a customer has already read. It costs perceived
latency, which is what the `stage` events are for.

**For your UI:** a spinner driven by `stage` events, then answer text appearing
at once, is the intended experience. Do not build a typing effect that assumes
tokens trickle in — they do not.

### Three outcomes to handle distinctly

1. **Answered** — `withheld: false`, `escalated: false`. Normal.
2. **Escalated** — `escalated: true`. The guardrail decided the question needs a
   human (pregnancy, a managed condition, medication interactions, under-16,
   eating-disorder signals, extreme calorie targets, self-harm). The text is a
   templated refusal with a handoff. **There are no citations and there is no
   answer.** Do not retry, do not rephrase, do not fall back to another source.
3. **Withheld** — `withheld: true`. The pipeline produced an answer and the
   post-check rejected it. The customer gets a handoff. Same rule: do not retry.

Both 2 and 3 tell the customer to contact the dotFIT support team or a
healthcare professional, **and now carry a real route**: `support@dotfit.com or
(877) 436-8348`, the pair the practitioner reference guide itself publishes. It
is configuration on our side, so tell us if the preview audience should be sent
somewhere else (a web form, your own support queue) and we will change it in one
place rather than have you rewrite delivered copy.

If your UI can surface a support link of its own alongside, do — this is the
single most-seen copy in the failure paths, and a dead end is a bad outcome for a
customer already being turned away. What you must not do is edit the delivered
text: it is the record of what the customer was told.

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
| `text` | The turn as the customer saw it. For an assistant turn use the delivered `answer` from that turn's `result` — including a refusal or handoff, which is what they read. |
| Do **not** include | The question you are asking now. If you do, we drop the duplicate rather than read it as the customer asking twice. |
| How many | Send what you have, up to a few exchanges. We keep the most recent **8 turns** and the first **1,000 characters** of each; anything past that is trimmed on our side, so you never need to trim on yours. |

**What history is used for.** Two things. It turns a follow-up into a
standalone question — "what about the chocolate one?" becomes "is the chocolate
LeanMeal good for weight loss?" before anything is retrieved, and the product
named two turns ago is resolved to its part numbers. And the medical-escalation
check reads it, so a trigger the customer stated earlier still applies to what
they ask now: "I'm 14" … then, two turns later, "how much creatine should I
take?" escalates and hands off.

**What it is not used for.** The answer-writing stage never sees it. Answers
stay grounded strictly in retrieved sources, because that is what makes the
`[n]` citations and the claims checks mean anything — an earlier assistant turn
is not a source and cannot be cited. So the assistant will not "remember" a
number it told you two turns ago unless the sources say it again.

**What the escalation check can and cannot see.** It reads the transcript you
send, and only that. A trigger stated in a turn you trimmed away, or collected
by your own product surface outside the question text — an age field, a profile
flag, an intake form — is invisible to us. If you hold anything from the
escalation list (age, pregnancy or breastfeeding, a managed condition,
medication, a calorie target) outside the conversation, either put it in a turn
of the history you send or handle it on your side; do not assume we can infer
it.

The check is also written *not* to let one trigger swallow the conversation: a
customer who was correctly refused a dosing question can still ask where their
order is. Both directions are measured (the §12 multi-turn set:
delayed triggers that must escalate, and controls that must not), but this is a
model judgment rather than a rule engine — so treat a refusal as possible on
any turn, and render it the way you render an answer.

---

## Operational notes

**Auth.** `POST /ask` requires a shared secret, as a bearer token:

```
Authorization: Bearer <the secret we give you>
```

A missing, malformed or wrong value is `401` with no body detail and no stream —
which header was wrong is information only a guesser wants. `GET /healthz` is
unauthenticated, by design, so it still works as a probe.

We will hand you the secret out of band; treat it as a credential (your secret
store, not your repo), and tell us if it needs rotating — the service reads it
from its own configuration, so a rotation is a restart on our side and a config
change on yours. The service **fails to start** without it, so there is no state
in which it is running and open.

This is the boundary, not defence in depth: the service still expects to sit on
a private network reachable only by your server. **Do not expose it to the public
internet**, with or without the secret. If your platform terminates mTLS in
front of it instead, say so and we will run it with auth explicitly disabled
rather than have two half-configured mechanisms.

**Limits.** A question over **2,000 characters** is a `400`, not a truncation —
truncating would change the question and answer something the customer did not
ask, so trim or split on your side if you ever relay pasted email. `top` must be
1–20. History needs no trimming on your side (we keep the newest 8 turns and
1,000 characters each). The request body itself is capped at 256 KB.

**Timeouts and cancellation.** If the client hangs up, the stream is cancelled
and the run is abandoned. We also impose our own ceiling — **120 seconds** per
request, reported by `/healthz` as `timeout_seconds` — so a wedged upstream model
call cannot hold a connection open indefinitely. When it fires you get `error`
with `kind: "Timeout"` and the handoff `delta`, the same shape as any other
failure, rather than a stream that silently stops. Set your own client timeout
comfortably above ours: the full pipeline is several model calls and a normal
answer takes seconds, not milliseconds.

**Errors.** Any exception yields `error` plus a handoff `delta`, never a
stack trace, never a bare stream end. If you see `error`, log `kind` and treat
the turn as a failed one; the customer has already been given something to read.

**Logging.** Your database is the system of record for transcripts. The service
deliberately does not keep them. Worth storing per turn, from `result`:
`request_id`, `escalated`, `withheld`, `post_check.passed`, `citations` and
`sources` — that is what lets both teams reason about a complaint later without
keeping a second copy of the conversation anywhere else.

**Our verdict log.** We write one structured line per request recording what the
assistant *decided* — escalated or not and on what reason code, whether the
answer was withheld, whether the post-check passed and which checks fired,
whether the safety verdict rested on an earlier turn, how many sources were
retrieved and cited. It is keyed by `request_id` and `conversation_id`.

It holds **no question text and no answer text**, by construction — not
redacted, simply not collected. That is the deal that makes the split work: you
hold what was said, we hold what was decided, and `request_id` joins the two
when someone asks weeks later why a particular turn did what it did. Which is
also why the two ids are the one thing you send us that we keep: put an opaque
identifier in them, never anything a customer typed. Both are capped at 64
characters.

One gap to know about: a request rejected with `400` or `401` never reaches the
pipeline, so it produces no verdict line. Those are transport rejections and you
see them synchronously as a status code — the log starts once a request has been
accepted.

**Rate.** One trusted caller, so there is no rate limiting. Every request costs
several model calls; a retry loop on failure is an expensive mistake.

---

## A standing caveat, not a first-release one

**This section applies to every preview release, not just the first.** By owner
ruling (2026-09-10), the stakeholder preview is never held back for a
measurement — the project is meant to be tested continuously, at whatever state
it is in. That is a deliberate trade, and this caveat is the other half of it.
Please keep it in front of whoever briefs the audience, each time.

Two measurements are open, and both concern **claim wording** rather than
retrieval or safety:

- The automatic claims check has a known false-negative rate: in the most recent
  measured run, it delivered answers that a reviewing model judged to contain
  non-compliant claim language.
- The answer model paraphrases approved product copy where it should quote it.

Retrieval accuracy (does it find the right material) and the medical-escalation
guardrail are both measuring well. The gap is specifically that a product claim
may reach the reader in wording that is not the legally approved wording.

Stakeholders and partners should be told this plainly and asked to report
anything that reads like a product claim. dotFIT's approved product copy is the
only source of claim language; if the assistant's phrasing differs from it, the
approved copy is right and the assistant is wrong.

One request that follows from the same ruling: **keep your per-turn record of
`request_id`, `escalated`, `withheld` and `post_check.passed`.** Since any state
may ship, that record joined to our verdict log (see Operational notes) is how
either side reconstructs what the assistant actually told someone, weeks later,
without keeping a second copy of the conversation anywhere. The `request_id` is
what makes the join possible, so store it even if you never store the rest.

If anyone in the preview audience may quote the assistant in external material —
as opposed to testing it internally — tell us before that happens. It is a
materially different risk and the briefing should say so.
