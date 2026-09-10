# Task 2 — Confirm a privacy check on 234 old emails

**Who:** support lead
**Effort:** about half a day — 20 records need reading, the rest is bulk
**Blocking:** yes — 211 of these are already searchable by the assistant
**Tracked as:** open item 13 (which now also covers what used to be item 16)

> **Updated 10 September 2026 and the ask has grown.** On 9 September the whole
> corpus was re-transcribed with a larger, sharper model. It notices more, so it
> flags more: the queue went from 220 to **234**, and the group that needs
> reading went from **2 records to 20**. Nothing got worse — the same emails
> were always there, and the earlier "2" was measured with a model that saw
> less. Please read the section-A list below rather than the old one.

## What this is

The customer Q&A emails contain real people's names, addresses and phone
numbers. Before anything else happened to them, every file went through an
automatic scrub that removes personal details and replaces them with markers
like `[NAME]` and `[EMAIL]`.

The scrub is deliberately cautious. When it is confident, it removes. When it
is unsure, it removes *and* raises a flag for a human to confirm. This is the
pile of flags: **234 records**.

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

**211 of the 234 are live and searchable by the assistant right now.**

That is by design. The flag does not mean the record is unsafe — the scrub
already removed what it flagged, and we have now verified that record by
record rather than assuming it. Holding all 234 back would have removed a large
amount of genuinely useful expert content for no safety benefit.

## What we actually need from you

### A. Twenty records where the flagged name is still there. Please read these.

These are the ones where the flagged detail is genuinely still present in the
record the assistant uses. **This is the group that grew** — it was 2 before the
re-transcription and is now 20, because the larger model spots names the smaller
one read straight past.

They are not all the same situation, so we are not going to characterise them
for you in bulk. The pattern we know about from last round still applies to at
least two of them — **someone else mentioned by name inside an ordinary
sentence**, which no automatic rule can remove without mangling real prose:

1. `2023/Injury program level-3, complete list and foreign pickup note.docx` —
   a customer says they heard about dotFIT through a named person's LinkedIn
   page. *(Not currently searchable.)*
2. `2025/Clinical needs -reference to Kat& attending Doctors.docx` — a customer
   names a relative who referred them.

The other eighteen need the same question asked of each: **is this a private
individual whose name should come out, or someone whose name is fine to keep?**
Run the tool below to get the full list with filenames; 15 of the 20 are
currently live in the assistant.

Reading twenty short emails is perhaps two hours. It is the bulk of this task
now, and it is the part that genuinely needs a person.

**The Zane record is done.** Last round this section carried a third record —
`2026/Reverse dieting.docx`, a customer asking whether Neal and Zane had covered
a topic on the podcast. Zane was recorded as staff, and the full rebuild that
made the ruling take effect has now happened. It is gone from this list and
needs nothing further.

### B. One decision that covers 153 records at once.

For **153 records** (139 of them live), the flagged detail was removed from what
the assistant uses — but it is still present in an intermediate working copy of
the email that we keep in our code repository.

To be clear about the risk: **the assistant cannot retrieve these.** They are
not part of what it searches. This is a question about what is allowed to sit
in our internal files, not about what a customer could ever see.

We need one ruling: is it acceptable for the internal working copies to retain
this, or should we scrub them too and regenerate? One answer covers all 153.

This group grew too (121 → 153) for the same reason as section A. The ruling you
give does not change with the count — it is the same question about the same
kind of file — which is exactly why it is worth answering once rather than
per record.

### C. Bulk sign-off — 61 records.

These need no individual reading.

- **9** were flagged for a name, and we have confirmed the name is gone from
  both the assistant's copy and the internal working copy.
- **47** are a deterministic 5% sample, pulled as a spot-check. Nothing is known
  to be wrong with them — that is the point of a sample.
- **5** are ones where the automatic system rated its own confidence as low.
  Worth a skim; the tool prints each one's confidence score.

The "confirmed clean" group shrank from 42 to 9 — those records did not become
unsafe, they moved into sections A and B where the sharper model can now see
what it had missed. That movement is the whole reason this document was updated
rather than left alone.

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
flagged 234 cases for human eyes — and we now know that process had blind
spots twice over: once in how it grouped the flags, and once in what the
smaller model could see at all. Section A is twenty short reads and section B
is a single ruling; between them they are the whole of what genuinely warrants
a person.
