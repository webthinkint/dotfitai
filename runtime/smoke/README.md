# The conversational smoke set (design §11)

29 written turns across 9 conversations' worth of shapes, in
`conversations.jsonl`. Run them live, read the transcripts.

```bash
cd runtime
dotnet run --project src/DotFit.Agentic.Cli -- smoke --out ../processed/agentic/smoke
```

## What this is, and what it is not

It is **a change-detector you read**, not a score. There is no rubric, no
expected answer and no pass rate — `looking_for` on each item says what a human
should check, in a sentence, and the output is a markdown transcript a
non-engineer can read end to end.

It is not the golden set. The 250-item golden set was drawn, re-drawn, and
never labeled by a nutritionist, so nothing computed against it says anything
about correctness, and this branch does not quote it (decision D7). The 179
retrieval probes in `processed/golden/` are the one label-free measurement that
survives, and they measure the index and the search tool — run those too.

It is also not the primary signal. The owners' complaint was about *feel*, and
feel is measured by the owners using it (§11.1). This set exists so that a
change made on Tuesday can be checked against Monday's transcript without
booking an owner's afternoon.

## The tiers, and why each item is there

| Tier | Items | What it is for |
|---|---|---|
| `chat` | S-001…S-004 | Turns that need no retrieval. S-001 is the v1 defect that started this branch — "hi there" was withheld and handed to support. S-004 is the opposite failure: a greeting wrapper must not make a real question read as small talk. |
| `product` | S-010…S-014 | The ordinary case. Watch for quoting approved copy rather than paraphrasing it, and for searching twice when one result is thin. |
| `currency` | S-020…S-023 | Renames, replacements, discontinuations. The rename/replacement distinction (§6) is a product claim about a thing that does not exist if it collapses. |
| `multiturn` | S-030, S-031 | Follow-ups that only make sense against the previous turn. Also the cost of not replaying tool results (open item 4): S-030's third turn must re-search. |
| `safety` | S-040…S-044 | The escalation list, handled conversationally (§8.1). S-041 and S-043 are the pair that matters: a trigger stated once still binds, *and* it does not put every later turn behind a caveat. S-044 is the most important item in the set. |
| `claims` | S-050…S-052 | Claim traps. With no gate, these are held only by the prompt and the tool surface — open item 2 in person. |
| `scope` | S-060, S-061 | Off-topic is not a safety event. S-061 is adjacent-but-really-support, which v1's classifier read as out-of-scope. |
| `adversarial` | S-070, S-071 | The rules do not move because someone in the conversation says they have. |
| `retrieval` | S-080, S-081 | Whether `fetch` gets used when a source is truncated, and whether podcast material stays attributed rather than becoming a claim. |

## Reading a transcript

Each item records the question, the stages (so you can see what it searched
for), the answer, the sources it cited, and the timings. Three things to check
before the prose:

1. **First-delta time.** The targets are in §6: under ~1.5 s with no tool call,
   under ~4 s with one search. This is what the branch is for.
2. **Tool calls.** Zero on a `chat` item is correct. Zero on a `product` item
   means it answered from training data, which is the one thing it must not do.
3. **Citations.** A product claim with no `[n]`, or an `[n]` pointing at a
   CONTEXT ONLY source, is the failure mode that has no gate behind it any more.

Then read the answer against `looking_for`. Note what is wrong in a sentence and
put it in the progress log — that list is the tuning backlog.

## Adding an item

Add the line, say what a human should look for, and say why it is there. An item
with no stated failure it is watching for is an item nobody will know how to
read in three months.
