# The conversational smoke set

42 written items in `conversations.jsonl`, some of them multi-turn. Run them
live against Azure and read the transcripts.

```bash
cd runtime
dotnet run --project src/DotFit.Assistant.Cli -- smoke                  # every item
dotnet run --project src/DotFit.Assistant.Cli -- smoke --tier support   # one tier
dotnet run --project src/DotFit.Assistant.Cli -- smoke --tier safety --variant safety-early
```

Transcripts land in `assistant/smoke/runs/` (gitignored), named by time and
tier, and each one names the prompt variant and version it ran on — so two runs
on two variants can be read side by side.

## What this is

**A change-detector you read**, not a score. There is no rubric, no expected
answer and no pass rate: `looking_for` on each item says in a sentence what a
person should check, and the output is a markdown transcript a non-engineer can
read end to end. The model is not deterministic, so one run is a tendency, not a
proof; rerun an item before concluding it regressed.

It is not the primary signal either. Owners and staff using the assistant are.
This set exists so that a change made today can be checked against yesterday's
transcript without booking anyone's afternoon.

## The tiers

| Tier | Items | What it is for |
|---|---|---|
| `chat` | S-001…S-004 | Turns that need no retrieval. S-001: a greeting is never a support handoff. S-004 is the opposite failure: a greeting wrapped around a real question must still be searched and answered. |
| `product` | S-010…S-014 | The ordinary case. Watch for quoting approved copy rather than paraphrasing it, and for searching again when one result is thin. |
| `currency` | S-020…S-023 | Renames, replacements, discontinuations. Collapsing a rename and a replacement is a product claim about something that does not exist. |
| `multiturn` | S-030, S-031 | Follow-ups that only make sense against the previous turn. Tool results are not replayed, so S-030's third turn must search again. |
| `safety` | S-040…S-044 | The escalation list, handled conversationally. S-041 and S-043 are the pair that matters: a trigger stated once still binds, *and* it does not put every later turn behind a caveat. S-044 is the most important item in the set. |
| `claims` | S-050…S-053 | Claim traps: disease claims, a third party's claim, arithmetic across products, and strengthening "the majority" into "all". Nothing gates, so these are held by the prompt and the tools alone. |
| `scope` | S-062 | Off-topic is declined in a sentence with no search, and is not a safety event. |
| `support` | S-060, S-061, S-100…S-104 | Customer service from the customer-service reference: how-to and policy answered in the assistant's own words, fixed policy figures stated, product prices never stated, actions on the customer's own account routed to support. |
| `adversarial` | S-070, S-071 | The rules do not move because someone in the conversation says they have. |
| `retrieval` | S-080…S-082 | Whether `fetch` gets used when a source is truncated, whether podcast material stays attributed rather than becoming a claim, and whether the customer's spelling reaches the podcast. |
| `program` | S-090…S-094 | Supplement programs from the program guide. S-090 is the vague ask: the baseline plus two or three questions, not a questionnaire. S-091 and S-092 test the guide's exclusions (vegan, caffeine-sensitive); S-093 carries a medication across turns; S-094 is the overlap check with real arithmetic. |

## Reading a transcript

Each item records the question, what the assistant did (each tool call), the
answer, the sources and which were cited, and the timings. Check the mechanical
things before the prose:

1. **First-delta time.** Roughly: under 2 s with no lookup, 2–3 s for a
   customer-service answer, 4–7 s with a search or a program.
2. **What it did.** Nothing on a `chat` item is correct. Nothing on a `product`
   item means it answered from its own training, which it must not do. On a
   program or support item, the `guide` line should appear once, not per section.
3. **Citations.** A product claim with no `[n]`, or an `[n]` pointing at a
   CONTEXT ONLY source, is the failure nothing else catches. Answers from the
   program guide or the customer-service FAQ carry no number, by design.

Then read the answer against `looking_for`, and note what is wrong in a
sentence.

## Adding an item

Add the line with an id, a tier and `looking_for`: what a person should check and
why the item is there. An item with no stated failure it is watching for is an
item nobody will know how to read in three months. A new tier also goes in the
table above and in `SmokeSetTests`.
