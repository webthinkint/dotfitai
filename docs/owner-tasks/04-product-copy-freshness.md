# Task 4 — Own the monthly product-copy check

**Who:** needs a named person
**Effort:** 30 minutes to set up, then ~15 minutes a month
**Blocking:** no, but the risk grows quietly
**Tracked as:** open item 2

## What this is

When the assistant makes a claim about a product — what it does, what is in it,
how to take it — it quotes the approved website copy word for word. It never
rewords it. That is deliberate: the approved copy is what legal has signed off,
and a paraphrase is a new claim nobody approved.

That copy lives in a file that was exported from the website once. The website
keeps changing. The file does not.

So the risk is quiet: marketing updates a product page, legal approves new
wording, and the assistant carries on confidently quoting last quarter's text as
though it were current. Nothing errors. Nothing looks wrong.

## What we need

Two decisions, not a project:

1. **Who owns it.** One named person who re-exports the product file and runs
   the comparison.
2. **How often.** Monthly is the proposal. If product copy changes less often
   than that, quarterly is fine — the point is that it is scheduled rather than
   remembered.

## The tool exists

You are not being asked to eyeball two files. There is a comparison tool that
reports what actually changed, in the terms that matter:

```
cd pipeline
uv run python scripts/products_diff.py OLD-products.json NEW-products.json
```

It reports, in priority order:

- **Changed claim sections** — which products had their approved wording move,
  and which section. This is the one that matters: a changed description is
  changed approved copy, and the assistant needs to be updated before it quotes
  the old version again.
- **Added and removed products** — a new product needs its names taught to the
  system so customers' shorthand still finds it. A removed product needs a
  decision rather than a deletion (see below).
- **Renamed products** — the tool flags these but cannot tell a rename from a
  reformulation that kept the same product number. It says so, and asks you to
  confirm. That distinction matters: a rename is the same product with a new
  name, and the assistant should say "LeanMR, now LeanMeal". A reformulation is
  a different product, and old advice about it may no longer hold.
- **Changed web addresses** — every one is a link the assistant gives customers.

It also tells you how many documents need re-processing as a result, which is
what the engineering side needs to know.

## A note on discontinued products

When a product goes away, the instinct is to delete it. Please do not.

Customers still ask "what happened to X?" and "what do I take instead?" The
system deliberately keeps discontinued products searchable, marked as
discontinued, with guidance about what replaces them. Removing them means the
assistant cannot answer a question customers definitely ask.

So a removed product is a conversation, not a deletion.

## What happens if this waits

The assistant keeps quoting the copy it has. It stays accurate for exactly as
long as the website does not change.

The failure mode is silent and gets worse with time — there is no alarm for
"this claim is eighteen months out of date." Right now the file is recent enough
that this is low risk. That is precisely why it is worth assigning while it is
cheap.
