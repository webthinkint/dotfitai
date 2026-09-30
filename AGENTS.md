# AGENTS.md

dotFIT Assistant: a frontier model given dotFIT's nutrition and supplement
material as tools, which decides for itself what to look up and what to say.
Judge every change by one question: does it make the assistant faster, less
refusal-prone, or closer to the owners' preferred prose?

## Read before you change

- The loop, tools, source numbering, prompt assembly, service, logs, cost →
  `docs/architecture.md`, which also holds the runtime's rules.
- Anything the caller sees (an event, a `result` or `/healthz` field, a limit) →
  `docs/website-integration.md`. The website team builds against it, not the
  code, so it changes in the same commit.
- The prompt, references, `sources.yaml`, the smoke set → `assistant/README.md`
  and the prompt part itself (`assistant/prompt/`).
- The pipeline and the index → `pipeline/README.md`, "Rules the pipeline keeps".
- Running and deploying the runtime → `runtime/README.md`.
- Before calling anything "next" → `docs/open.md`.
- Code comments state each contract where it is enforced: read the file you are
  about to change.

## Commands

```bash
cd pipeline && uv sync && uv run pytest     # Python 3.12, uv only
cd runtime && dotnet build && dotnet test   # .NET 10
```

`ask`, `chat` and `smoke` in the `dotfit` CLI cost Azure calls. Corpus filenames
contain spaces, commas and `&`, so always quote paths.

## Rules that hold everywhere

- No model call outside the loop: no pre-check, rewrite or post-check. Nothing
  blocks, withholds or retracts an answer.
- `original-data/` is read-only. `QAs/` is real customer mail: real corpus text
  never goes in code, tests, comments, docs or commit messages. Use synthetic text.
- A product claim is quoted from approved copy or attributed, never strengthened.
  No product diagnoses, treats, cures or prevents a disease. A product's price
  is never stated.
- The turn log has no field for question or answer text.
- A source number never changes meaning within a turn, and its `source` event
  reaches the caller before any text cites it.
- `pipeline-output/` is derived: regenerate it, never hand-edit it. Pipeline
  reruns are byte-identical.
- Azure credentials live only in the gitignored root `.env`; `.env.example` is
  the committed contract.

## Working

- Tests are minimal on purpose: the deterministic layer is pinned, the loop gets
  shape tests, prompt structure is tested and prompt wording is not.
- Docs and comments are present tense. When a rule changes, edit it in place and
  say why in the commit message. There is no history log.
- Each file keeps the line endings it was committed with (runtime LF, some
  pipeline files CRLF). Check `git diff --stat` for whole-file rewrites.
- Commits: `area: description`, lowercase.
