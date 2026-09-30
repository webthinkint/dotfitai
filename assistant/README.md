# The assistant's content: prompt, references, sources, smoke set

Everything in this folder is written by people and read by the assistant at
startup. None of it needs a code change: edit, check, restart.

```
assistant/
  sources.yaml        what the assistant can read, and what each source is for
  prompt/             the system prompt, one markdown file per part
    variants/         templates that assemble the parts
  references/         dotFIT's own guidance the assistant reads on demand
  smoke/              written test conversations, run live and read by a person
```

## How to check a change

From `runtime/`:

```bash
dotnet run --project src/DotFit.Assistant.Cli -- prompt              # the assembled prompt
dotnet run --project src/DotFit.Assistant.Cli -- config              # shows the prompt version
dotnet run --project src/DotFit.Assistant.Cli -- ask "…" --trace     # one live question
dotnet run --project src/DotFit.Assistant.Cli -- smoke --tier support
dotnet test                                                            # structure checks
```

A broken edit — an include that does not resolve, a missing file, a registry
entry that names an unknown source — fails `prompt`, fails the tests, and stops
the service from starting. It never reaches a customer half-applied.

On the preview server, an edit takes effect after `git pull` and a restart of
`dotfit-service`. Every turn logs the prompt version it ran on.

## The prompt

`prompt/*.md` are the parts: `posture` (who it is, scope, tone), `authority`
(sources and the claims rule), `tools`, `programs`, `safety`, and
`escalation-list`. A variant in `prompt/variants/` assembles them with
`{{name}}` includes; `default.md` is what runs unless configured otherwise.

- **Comments** `<!-- … -->` are stripped before the model sees anything. Put the
  reason for a rule in a comment beside it.
- **Includes** `{{name}}` pull in `name.md`. Parts may include parts.
- **Generated blocks** are filled in by the runtime and cannot be files:
  - `{{currency-facts}}` — renamed, replaced and discontinued products, from the alias table
  - `{{support-route}}`, `{{support-team}}` — the configured support contact
  - `{{source-list}}`, `{{authority-by-domain}}`, `{{reference-library}}` — from `sources.yaml`
- **Required**: every variant must include `{{escalation-list}}`, directly or
  through another part. Move it, reword around it, but it cannot be dropped.

**To try a different arrangement**, copy `variants/default.md` to a new name
(lowercase, digits, dashes), change it, and run it with `--variant <name>`, or
set `DOTFIT_ASSISTANT_PROMPT_VARIANT=<name>` in `.env`. Running the same smoke
tier on two variants gives two transcripts to compare.

Wording is not tested — only structure is. What tells you a wording change works
is reading the smoke transcripts and using it.

## References

A reference is dotFIT's own guidance that the assistant reads with
`read_reference`, whole or one section: today the supplement program guide and
the customer-service FAQ. The assistant uses a reference **in its own words and
never cites it**; it is not a numbered source.

**To add one** (for example a dotFIT program FAQ):

1. Put the markdown file in `references/`. Use `#`/`##` headings: the library in
   the prompt lists them as sections, so the assistant can read one section.
2. Add an entry under `references:` in `sources.yaml`: `file`, `title`, and
   `use_when` (it reads "Read it for …"). Add `policy_figures: true` only if the
   prices, fees and thresholds in it are fixed dotFIT policy the assistant may
   state.
3. If it should win disagreements on a topic, add its id to that topic under
   `authority:`.
4. Run `prompt` to see the library entry, then a few `ask` questions.

Rules for reference content:

- **No product prices.** Product pricing is per customer; the assistant never
  states it. Fixed policy figures belong only in a `policy_figures` reference.
- **Product claims come from approved copy.** A reference may say what dotFIT
  recommends; what a product contains, does or is for stays with the approved
  product pages.
- **Part numbers in brackets** (`[1333]`) must be current products in the alias
  table; a test checks every reference.
- **No real customer text.** Write examples, never paste them.

## `sources.yaml`

- `corpora:` — the searchable collections in the search index. The ids are the
  index's source types and must match it exactly; edit `name` and `about`
  freely, they are how the prompt describes each one.
- `references:` — as above.
- `authority:` — per topic, which source wins when two disagree, most
  authoritative first. Every id must be a corpus or a reference.

## The smoke set

See `smoke/README.md`.
