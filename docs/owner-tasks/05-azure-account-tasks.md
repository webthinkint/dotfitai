# Task 5 — Two Azure account chores

**Who:** whoever holds the Azure account
**Effort:** about an hour of actual work, then waiting on Microsoft
**Blocking:** partly — see each item
**Tracked as:** item 1 (section 5a). Item 10 (section 5b) resolved 2026-09-08

Both of these are account administration rather than engineering. Neither needs
anyone to touch the code.

---

## 5a — ~~Request more capacity for the larger model~~ — **resolved 9 September 2026**

The capacity increase came through. The same day, both model roles were
switched to the new frontier deployment (`gpt-5.6-luna`, quota 333K tokens /
333 requests per minute) and everything the small model had been measuring was
re-run on it:

- the whole corpus was re-transcribed with the larger model (1,041 answers);
- the knowledge index was rebuilt from that (4,002 documents, live);
- the quality measurements were repeated on the new model.

Nothing further is needed from you here. One operational note for the future:
the larger model is slower per answer than the small one, so full-corpus
re-transcriptions now run with several requests in parallel — the account's
token limits comfortably allow it, and a sequential rerun would take hours
longer for the same result.

---

## 5b — ~~Get a stuck search index removed~~ — **resolved, no ticket needed**

**Update 2026-09-08:** the deletion finally finished on its own, so the ticket
below is no longer needed. We checked the way this document recommends — the
service statistics, not the portal:

- the index query now answers plainly "no index with the name `kb-main` was
  found" (before, it answered that the index "is being deleted"), and
- the statistics count exactly **one** index, **3,996 documents, ~110 MB** —
  the replacement only. The stuck copy's ~7,992 documents / 213 MB no longer
  count against the account's storage and quota.

Nothing to do. Everything stays on the replacement index `kb-main-v2`; moving
back to the old name would be purely cosmetic. The portal lesson at the bottom
still stands for next time.

---

**For the record — the original problem (2026-09-07 to 2026-09-08):**

The searchable database is called an "index". We built one, called `kb-main`. It
broke in an unusual way: it stopped returning any documents, and when we deleted
it, the deletion never finished.

It is now in a state where it:

- cannot serve any documents (so it is useless), and
- cannot finish being deleted (so it will not go away), and
- still counts against the account's storage and quota — about **7,992
  documents and 213 MB**.

We worked around it by building a fresh index under a new name, `kb-main-v2`.
Everything runs on that now, and the code was updated so nobody needs to
remember a workaround.

**What to do:** raise a support ticket with Microsoft Azure asking them to force
removal of the `kb-main` index.

**A detail worth including in the ticket**, because it makes the case cleanly:
on the *same* search service, a throwaway test index can be created and deleted
in seconds. So this is not a service-wide problem — it is one index that can
neither serve documents nor finish deleting.

**One thing not to be fooled by:** the Azure portal does not display indexes
that are in a deleting state. So `kb-main` not appearing in the portal is *not*
evidence it is gone. The service statistics still count it. If someone reports
"it's not there any more", check the statistics rather than the portal.

**If Microsoft frees the name**, we do not have to do anything. Moving back to
the old name would be cosmetic, and it is a separate decision — not something
that follows automatically from the ticket being resolved.
