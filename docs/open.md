# Open items

What is not settled. Edit an item in place as it changes and delete it when it
is closed; the commit message records why.

## Before public customer traffic

- **Residual PII in the Q&A corpus.** 178 live records carry the flag. By the
  weekly report's rule, 9 may show the flagged text in what is served; all 9 have
  been read and are public names (study and article authors, quoted experts, a
  journalist). The rest have it only in committed files, the Stage 0 and Stage 1
  text and `source_file` paths: not served, but in git, which is the question in the
  next item. Every live record has had one read for a private person's details,
  and a detail found later is redacted in `REDACTED_SPANS` or its record ruled out.
  In new records, a person reads the flagged ones each week; a name the model never
  noticed goes unread until then and can reach customers.
- **Real customer text in git history.** Comments and a test fixture quoted a
  customer's name and a street fragment from the corpus; the files are clean
  now, but the history still holds them. Committed pipeline output is the same:
  the Stage 0 and Stage 1 text of every excluded record, and every span redacted
  since, stays in the commits before the ruling. Decide whether that is acceptable
  for who can read the repository, or rewrite it.
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
- **A Claude answer written after a tool result arrives in one burst.** On
  `claude-sonnet-5-5` an answer the model writes without a lookup streams
  (first text ~1 s on the Messages endpoint, against ~6 s through the Responses
  API, which buffers every Claude answer). An answer written after a tool result
  arrives in one burst on both endpoints: all deltas within ~40 ms, first text
  ~5.5 s after the round trip starts. Probed on 2026-10-27 against the bare
  endpoints, so it is upstream of the loop; `thinking.display` `updates` or
  `summarized`, `between_tools` and low effort all left it unchanged. The likely
  cause is Sonnet 5.5 returning text between tool calls as progress-update
  thinking blocks, which needs the text finished before it can be classified.
  Unconfirmed whether Foundry or the model holds it; a first-party API key
  would settle that.
- **The turn budgets are estimates** (8 tool calls, 60 s). Replace them with the
  distribution the turn log shows.
- **Tool results are not replayed**, so a follow-up sometimes searches again.
  The fix is server-side sessions, which changes the website contract.
- **The search price is a placeholder** in the cost sheet; the chat and
  embedding prices should be confirmed against the invoice. A new price means a
  new `DOTFIT_PRICE_SHEET` id in the same edit.

## Content and behaviour

- **The Stage 4 threshold predates the Claude small model.** 0.88 was scanned on
  the previous model's question wording, and `stage4_cluster_scan.py` has not been
  rerun on Haiku's. Its questions match more often, and the merge coverage rule
  limits what a wrong merge can remove.
- **The low-confidence cutoff.** Haiku scores itself lower than the previous model:
  25 live records fall below 0.7, with the same fidelity as the rest. Decide whether the
  queue reason still earns a read at 0.7.
- **Seven Q&A records wait on their source documents.** Each is ruled out "until
  the source is fixed" in `EXCLUDED_DOCUMENTS`: six doses or limits written in the
  wrong unit, and a missing "not" that reverses a product's sport certification. The
  owner corrects the doc in Dropbox; it keeps its path and id, so the ruling is
  removed by hand once the Monday fetch brings the fix. Three live records carry a
  unit slip judged harmless and should be corrected the same way
  (`d1fe4d28342037f9`, `b70bbee238c40644`, `6ffd3d368416e884`).
- **The weight-loss and liver product's own copy is framed around liver fat.** The
  PDSRG says it mobilizes unhealthful liver fat, and the product script says it
  supports the liver's fat processing. Q&A answers that went further (clearing liver
  fat, healing liver tissue, protecting against liver disease) are ruled out, but
  the approved wording stays quotable. Whether it holds is the owners' call.
- **Two current answers still recommend VeganMV**, which is discontinued. The
  currency judge called the line incidental despite the prompt rule (the previous
  model also left two). A disposition or a rerun would settle them.
- **The product summaries deck is extracted but not indexed.** Indexing needs a
  new `source_type` and an owner go-ahead for the index rebuild. Also open:
  whether `get_product` should return the deck's section for the product, and
  whether the held-back trainer scripts become a trainer-facing tool.
- **Script and label dosing disagree for nine products.** The product copy
  carries both the script's "How do you use it?" and the label's directions;
  the prompt gives the script's and names the label's difference. The script
  authors are confirming each row of `docs/video-script-discrepancies.md`;
  their answers go into the script's `CORRECTIONS` or a new document, and the
  prompt rule goes once none remain. EnergyAid is in the document but skipped
  until it is on the website, and Sleep Aid's overview is not written yet.
- **The menu guide awaits the owners.** Its calorie formula, floors, youth and
  medical rules, shake limit and the gluten- and lactose-free product question
  are proposals; `docs/menu-guide-review.md` lists each.
- **Product links are unmeasured.** Whether the model links every product it
  recommends, only those, and with a real part number is unread in any smoke
  run. The turn log's `products` and `product_links_unresolved`, and the smoke
  table's product list, show it.
- **Pre-tool narration streams to the customer.** If the model writes before
  calling a tool, that text streams. Decide from real transcripts whether it
  reads as conversation or noise.
- **Vegan program exclusion (S-091).** One smoke run recommended dotFIT Vitamin
  D-3, which contains gelatin, to a vegan; another run excluded it. Watch it; if
  it recurs, tighten `programs.md`.
- **Which medications limit a program.** The guide's screening cuts a program to
  a multi and protein for "medications that may interact", with no list. A statin
  is ruled out of it; every other medication is the model's judgment until the
  owners supply the list.
- **Refund window (S-100).** The assistant merged the FAQ's 30-day exchange
  window into its refund answer. Ask the customer-service team whether refunds
  share that window; if so, one line in the FAQ settles it.
