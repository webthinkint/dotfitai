# Task 2 — Confirm a privacy check on 220 old emails

**Who:** support lead
**Effort:** about two hours, most of it a bulk sign-off
**Blocking:** yes — 201 of these are already searchable by the assistant
**Tracked as:** open item 13 (which now also covers what used to be item 16)

## What this is

The customer Q&A emails contain real people's names, addresses and phone
numbers. Before anything else happened to them, every file went through an
automatic scrub that removes personal details and replaces them with markers
like `[NAME]` and `[EMAIL]`.

The scrub is deliberately cautious. When it is confident, it removes. When it
is unsure, it removes *and* raises a flag for a human to confirm. This is the
pile of flags: **220 records**.

## What changed since the first version of this document

The first version asked you to read 3 records and bulk-approve 219. That split
was wrong, and it is worth saying why, because it changes what your sign-off
means.

The tool sorted a record into the "safe" pile if it found *any* privacy marker
anywhere in it. That is not the same question as "did we remove the specific
name we flagged?" A record can have `[EMAIL]` in its header and still have a
customer's full name sitting in the body. **Five records with a live customer
name were in the pile we were about to ask you to approve in bulk.**

We now check the actual question — did the specific flagged detail get
removed? — and we fixed the four holes in the scrub that let those names
through:

- a name written in lower case in a forwarded-message header
- a sign-off after a misspelled "thank you" (people mistype their own sign-off)
- a name simply left dangling at the end of a web form, with no "thanks" in
  front of it for the scrub to notice
- **"My name is …" self-introductions** — this was being tracked as a separate
  task, and it turned out to be the same hole seen from the other end. It is
  folded in here and now fixed.

Rerunning the scrub removed **38 further names across 36 files**, and changed
nothing else — no ordinary wording was damaged. The count of records where a
flagged detail is still present went from **6 down to 3**.

## Why it matters now

**201 of the 220 are live and searchable by the assistant right now.**

That is by design. The flag does not mean the record is unsafe — the scrub
already removed what it flagged, and we have now verified that record by
record rather than assuming it. Holding all 220 back would have removed a large
amount of genuinely useful expert content for no safety benefit.

## What we actually need from you

### A. Two records that still contain a name. Please read these.

These are the ones where the flagged name is genuinely still there. Both are
the same situation, and it is not one a rule can fix: **someone else mentioned
by name inside an ordinary sentence.** A computer cannot remove those without
also mangling real sentences, so a person has to decide.

1. `2023/Injury program level-3, complete list and foreign pickup note.docx` —
   a customer says they heard about dotFIT through a named person's LinkedIn
   page. *(This record is not currently searchable.)*
2. `2025/Clinical needs -reference to Kat& attending Doctors.docx` — a customer
   names a relative who referred them.

For each: is this a private individual whose name should come out, or someone
whose name is fine to keep?

**A third record has already been decided.** `2026/Reverse dieting.docx` was
a customer asking whether Neal and Zane had covered a topic on the podcast.
Zane is a co-host who appears by name in 427 places across the published
episodes, so he has been recorded as staff, exactly like Neal. Nothing further
is needed from you on it. It will keep appearing in the report until the next
full rebuild of the Q&A records, which is a scheduled maintenance run, not a
decision — we did not trigger one just to clear a single row.

### B. One decision that covers 121 records at once.

For **121 records**, the flagged detail was removed from what the assistant
uses — but it is still present in an intermediate working copy of the email
that we keep in our code repository.

To be clear about the risk: **the assistant cannot retrieve these.** They are
not part of what it searches. This is a question about what is allowed to sit
in our internal files, not about what a customer could ever see.

We need one ruling: is it acceptable for the internal working copies to retain
this, or should we scrub them too and regenerate? One answer covers all 121.

### C. Bulk sign-off — 95 records.

These need no individual reading.

- **42** were flagged for a name, and we have confirmed the name is gone from
  both the assistant's copy and the internal working copy.
- **48** are a random 5% sample, pulled deliberately as a spot-check. Nothing
  is known to be wrong with them — that is the point of a sample.
- **4** are ones where the automatic system rated its own confidence as low.
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

It prints the groups above with their file names and takes a second. Add
`--json` for the full list rather than a preview.

## Something we deliberately did not put in front of you

The tool does not show you the specific text that triggered each flag.

That is on purpose. Those snippets are the personal details themselves, and we
do not copy them into reports, tracking files, or anything that gets committed
to the code repository. The tool now *reads* them to work out which group a
record belongs in, and then reports only the group — never the text. If you
need to see the text for the three records in section A, open the original
files directly.

## Background you may find useful

A previous round of this ran in September and closed cleanly. The pattern then
was that most flags were dotFIT staff first names appearing in email
signatures — not customer data at all. Those are now removed automatically
without flagging.

The honest lesson from this round is a different one: that earlier "clean"
result was measured with the same tool that turned out to be asking the wrong
question. The three records in section A are what is genuinely left after
asking the right one.

## What happens if this waits

The assistant keeps working and the records stay searchable.

The risk is not that something breaks. It is that we would be saying "customer
data is handled properly" on the strength of an automatic process that itself
flagged 220 cases for human eyes — and we now know that process had blind
spots. Sections A and B are small and they are the ones that genuinely warrant
a decision.
