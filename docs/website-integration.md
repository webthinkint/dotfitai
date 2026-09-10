# Integrating the dotFIT knowledge assistant (SSE)

For the website engineering team. This is the contract between your server and
`dotfit-agent-service`, the §11 runtime behind an HTTP endpoint.

**Deployment shape this document assumes**, as agreed: your server calls the
service, your server relays the stream to the browser, and your database is the
system of record for the conversation. The service holds no state between
requests and stores nothing. The first release is for stakeholders and approved
partners, not public traffic.

**Status.** Everything in this document is built and live, including multi-turn
(2026-09-10). Anything agreed but not yet implemented is marked **planned** in
place — do not build against it until we tell you it has landed.

---

## Endpoints

### `GET /healthz`

Returns `200` with the index name and the three deployment names it is
configured against. Deployment names are configuration, not secrets; no key is
ever read on this path. Use it as your liveness and readiness check.

The service validates its whole configuration at **startup** — a missing key or
deployment fails the boot rather than the first question. If it is up, it is
configured.

### `POST /ask`

One customer question in, a Server-Sent Events stream out.

```json
{
  "question": "How much creatine should I take?",
  "conversation_id": "1b9f...",
  "history": [
    {"role": "user", "text": "is LeanMeal good for weight loss?"},
    {"role": "assistant", "text": "..."}
  ],
  "top": 8
}
```

| Field | Required | Meaning |
|---|---|---|
| `question` | yes | The customer's question, verbatim. Empty or whitespace gets `400`. |
| `conversation_id` | no | **Absent means "new conversation"**, which is what controls the disclosure — see Disclosure below. Send a stable id for every turn after the first. |
| `history` | no | Earlier turns of this conversation, oldest first, **not including** `question`. See Multi-turn. Absent or empty is a standalone question. |
| `top` | no | Sources retrieved and fed to the answer. Default 8. Leave it unset unless we ask you to change it. |

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
| `error` | `{message, kind}` | The pipeline threw. Terminal, and always followed by `delta`s carrying a handoff message. |

`stage` events exist so you have something to render while the answer is held.
That is the whole reason gating is affordable — see below.

### The `result` payload

```json
{
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
healthcare professional. Today that is prose. If you have a support route,
surface it alongside — it is the single most-seen piece of copy in the failure
paths and a dead-end message is a bad outcome for a customer already being
turned away.

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

**What history is used for.** One thing: turning a follow-up into a standalone
question. "What about the chocolate one?" becomes "Is the chocolate LeanMeal
good for weight loss?" before anything is retrieved, and the product named two
turns ago is resolved to its part numbers. That is the whole effect.

**What it is not used for.** The answer-writing stage never sees it. Answers
stay grounded strictly in retrieved sources, because that is what makes the
`[n]` citations and the claims checks mean anything — an earlier assistant turn
is not a source and cannot be cited. So the assistant will not "remember" a
number it told you two turns ago unless the sources say it again.

**One limitation to design around — safety is still judged one turn at a
time.** The medical-escalation guardrail sees only the current question, not
the history. A conversation where the trigger arrives in an earlier turn ("I'm
14" … then, two turns later, "how much creatine should I take?") will not
escalate on the later turn. We are working on it. Until it lands, if your own
product surface collects anything from the escalation list (age, pregnancy, a
managed condition, medication) outside the question text, do not rely on us to
catch it from the transcript.

---

## Operational notes

**Auth and network.** The service has no authentication of its own. It expects
to sit on a private network reachable only by your server, with a shared secret
or mTLS at the boundary. Do not expose it to the public internet.

**Timeouts and cancellation.** If the client hangs up, the stream is cancelled
and the run is abandoned. Give a request a generous timeout — the full pipeline
is several model calls and a normal answer takes seconds, not milliseconds.

**Errors.** Any exception yields `error` plus a handoff `delta`, never a
stack trace, never a bare stream end. If you see `error`, log `kind` and treat
the turn as a failed one; the customer has already been given something to read.

**Logging.** Your database is the system of record for transcripts. The service
deliberately does not keep them. Worth storing per turn, from `result`:
`escalated`, `withheld`, `post_check.passed`, `citations` and `sources` — that
is what lets both teams reason about a complaint later without keeping a second
copy of the conversation anywhere else.

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
`escalated`, `withheld` and `post_check.passed`.** Since any state may ship, that
record plus our own verdict log is how either side reconstructs what the
assistant actually told someone, weeks later, without keeping a second copy of
the conversation anywhere.

If anyone in the preview audience may quote the assistant in external material —
as opposed to testing it internally — tell us before that happens. It is a
materially different risk and the briefing should say so.
