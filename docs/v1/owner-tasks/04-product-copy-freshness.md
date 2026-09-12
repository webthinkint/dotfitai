# Task 4 — Keep the product copy current (decided)

**Who:** was "needs a named person" — **decided 2026-09-10**: nobody owns a
schedule, because there is no schedule
**Effort:** none on your side unless a drop changes *claims* (rare)
**Status:** **decided** — this document is now the record of the arrangement
**Tracked as:** open item 2 (closed)

## The ruling

Product-copy exports arrive **whenever an update is known** — a new product, a
reworded page, a renamed flavor — and not on any calendar. When one arrives,
engineering runs the update chain the same day; nothing waits on a monthly
tick, and nothing runs pointlessly when nothing changed.

The quiet risk this closes is real but was never going to be solved by a
rota: the assistant quotes approved website copy word for word, so it stays
accurate for exactly as long as that copy is current. An export that arrives
when something actually changed serves that better than a fixed cadence.

## What happens at each drop

1. **A before/after report is produced first.** The comparison tool shows
   what actually changed, in the terms that matter: changed claim sections
   (the one that matters — that is approved wording moving), added and
   removed products, renames it asks you to confirm, and changed web
   addresses.
2. **Engineering runs the update chain** — the product names, the answer
   corpus and the live search index are regenerated from the new file. This
   is now a recorded, repeatable procedure, and the first drop (two new
   dotBAR flavors, 10 September) ran it end to end: new flavors retrievable
   live the same day.
3. **Your eyes are needed only for two things**, and only if the report
   shows them:
   - **Changed claims** — you confirm the new wording is the approved one
     (the export is supposed to reflect the website, but the check is cheap).
   - **A removed or renamed product** — the tool cannot tell a rename from a
     reformulation, and the distinction matters (a rename is the same product
     with a new name; a reformulation is a different product, and old advice
     may no longer hold). Removed products are **never deleted** — customers
     still ask "what happened to X?", so removal is a conversation, not a
     deletion.

## The tool, for reference

```
cd pipeline
uv run python scripts/products_diff.py OLD-products.json NEW-products.json
```

It writes nothing; it is the report described above, ready to hand over.
