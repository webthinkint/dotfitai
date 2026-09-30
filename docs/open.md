# Open items

What is not settled. Edit an item in place as it changes and delete it when it
is closed; the commit message records why.

## Before public customer traffic

- **Residual PII in the Q&A corpus.** 20 records to read, one ruling covering
  153 more, 2 records that name a third party inside a customer's sentence, and
  121 committed Stage 0 text files that still carry flagged spans (not served,
  but in git). `pipeline/scripts/stage2_queue_triage.py` groups the queue.
- **Real customer text in git history.** Comments and a test fixture quoted a
  customer's name and a street fragment from the corpus; the files are clean
  now, but the history still holds them. Decide whether that is acceptable for
  who can read the repository, or rewrite it.
- **The debug transcript is on in the preview unit**
  (`DOTFIT_ASSISTANT_DEBUG_TRANSCRIPT=1` in `runtime/deploy/dotfit-service.service`)
  and must be removed before public traffic.

## Latency and cost

- **First-token latency misses the targets**: under 1.5 s with no tool call,
  under 4 s with one search, under 8 s with two or three, measured to first
  delta. The 2026-09-30 smoke run: no-tool turns 0.6–2.1 s, one or two
  lookups 2.4–6.3 s, program turns that look up several products 9.0–14.5 s
  (S-094 reran at 6.2 s, so run-to-run variance is several seconds). Tool
  time is not the cause: a round trip's calls run concurrently and a
  `get_product` takes 200–250 ms. The time is in the model round trips — a
  program turn makes three (read the guide, look up the products, answer),
  and three is accepted, since references load on demand. What is
  unattributed is each round trip's share between the deployment's own
  time-to-first-token, reasoning and prompt size; neither the turn log nor
  the smoke table records per-round-trip timing.
- **The turn budgets are estimates** (8 tool calls, 60 s). Replace them with the
  distribution the turn log shows.
- **Tool results are not replayed**, so a follow-up sometimes searches again.
  The fix is server-side sessions, which changes the website contract.
- **The search price is a placeholder** in the cost sheet; the chat and
  embedding prices should be confirmed against the invoice. A new price means a
  new `DOTFIT_PRICE_SHEET` id in the same edit.

## Content and behaviour

- **The product summaries deck is extracted but not indexed.** Indexing needs a
  new `source_type` and an owner go-ahead for the index rebuild. Also open:
  whether `get_product` should return the deck's section for the product, and
  whether the held-back trainer scripts become a trainer-facing tool.
- **Pre-tool narration streams to the customer.** If the model writes before
  calling a tool, that text streams. Decide from real transcripts whether it
  reads as conversation or noise.
- **Vegan program exclusion (S-091).** One smoke run recommended dotFIT Vitamin
  D-3, which contains gelatin, to a vegan; another run excluded it. Watch it; if
  it recurs, tighten `programs.md`.
- **Refund window (S-100).** The assistant merged the FAQ's 30-day exchange
  window into its refund answer. Ask the customer-service team whether refunds
  share that window; if so, one line in the FAQ settles it.
