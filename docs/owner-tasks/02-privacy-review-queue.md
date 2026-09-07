# Task 2 — Confirm a privacy check on 222 old emails

**Who:** support lead
**Effort:** half a day, most of it a bulk sign-off
**Blocking:** yes — 203 of these are already searchable by the assistant
**Tracked as:** open item 13

## What this is

The customer Q&A emails contain real people's names, addresses and phone
numbers. Before anything else happened to them, every file went through an
automatic scrub that removes personal details and replaces them with markers
like `[NAME]` and `[EMAIL]`.

The scrub is deliberately cautious. When it is confident, it removes. When it is
unsure, it removes *and* raises a flag for a human to confirm. This is the pile
of flags: **222 records**.

Nothing here is known to be wrong. This is the confirmation step that turns
"the automatic checks did not complain" into "a person checked."

## Why it matters now

**203 of the 222 are live and searchable by the assistant right now.**

That is by design, and worth understanding. The flag does not mean the record
is unsafe — the scrub already removed what it flagged. Holding all 222 back
would have removed a large amount of genuinely useful expert content from the
assistant for no safety benefit.

But it does mean this queue is a real audit to work, not a formality to file.

## What we actually need from you

The 222 split into two very different piles.

### Pile A — 3 records. Please read these.

These were flagged as containing a personal name, but the saved text has no
`[NAME]` or `[CUSTOMER]` marker in it. That means one of two things:

- the removal did not work, and a real name is sitting in a live record, or
- the flag was a false alarm and there was never a name there.

We cannot tell which from the outside. Three records, a few minutes each:

1. `2023/Lean pack 90, all at once or ind, full note, take as long as
   needed.docx`
2. `2026/Lead in protein -complete and best answer.docx`
3. `2026/Post workout carbs.docx`

For each: read it, and say whether there is a real person's name in it. If yes,
we remove it and re-publish. If no, we record it as a false alarm.

### Pile B — 219 records. Bulk sign-off.

These do not need reading one by one.

- **165** were flagged for a personal name *and* the marker is visibly there in
  the text. The system did what it said it did. Confirm the approach, not the
  individual records.
- **48** are a random 5% sample, pulled deliberately as a spot-check. Nothing is
  known to be wrong with them — that is the point of a sample. Read a handful
  if you want reassurance.
- **5** are ones where the automatic system rated its own confidence as low.
  Worth a skim.
- **1** is not a privacy issue at all. It is a check that the cleaned-up answer
  still matches the original expert answer, and this one drifted further than
  the threshold allows. Worth one read to confirm the meaning was not changed.

## How to see the list

Someone technical can run this and hand you the output:

```
cd pipeline
uv run python scripts/stage2_queue_triage.py
```

It prints the four piles with the file names, and takes a second. Add `--json`
for the full list rather than a preview.

## Something we deliberately did not put in front of you

The tool does not show you the specific text that triggered each flag.

That is on purpose. Those snippets are the personal details themselves, and we
do not copy them into reports, tracking files, or anything that gets committed
to the code repository. They stay in one working file that never leaves the
machine. If you need to see the text for the 3 records in Pile A, open the
original files directly.

## Background you may find useful

A previous round of this ran in September and closed cleanly. The pattern then
was that most flags were dotFIT staff first names appearing in email
signatures — not customer data at all. Those are now removed automatically
without flagging. If this round looks similar, that is expected.

## What happens if this waits

The assistant keeps working and the records stay searchable.

The risk is not that something breaks. It is that we would be saying "customer
data is handled properly" on the strength of an automatic process that itself
flagged 222 cases for human eyes. The 3 records in Pile A are the ones that
genuinely warrant a look.
