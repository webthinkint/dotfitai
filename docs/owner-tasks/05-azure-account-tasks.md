# Task 5 — Two Azure account chores

**Who:** whoever holds the Azure account
**Effort:** about an hour of actual work, then waiting on Microsoft
**Blocking:** partly — see each item
**Tracked as:** item 1 (section 5a). Item 10 (section 5b) resolved 2026-09-08

Both of these are account administration rather than engineering. Neither needs
anyone to touch the code.

---

## 5a — Request more capacity for the larger model

**Blocking: partly.** Everything works today; the quality ceiling is lower than
it should be.

The assistant uses two AI models: a small fast one for routine steps (checking
whether a question is medical, tidying up the search query) and a larger one for
writing the actual answer.

Right now **both jobs are being done by the small model**, because we do not
have capacity approved for the larger one. The system was designed for this and
runs fine — but answer quality is being held back by a model chosen for speed.

**What to do:** request a quota increase for the larger chat model in the Azure
portal, in the same region as the rest of the setup.

**Why it is worth doing before launch:** the tests we run tell us the assistant
finds the right material and refuses the right questions. Those results were
measured on the small model. When capacity arrives we should re-run them, since
some answer-quality findings may change — possibly for the better.

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
