# prompt-doc: the prompt and references in a Google Doc

Lets someone without git read and edit the assistant's prompt parts and
references. `export` writes every `.md` file directly in `assistant/prompt/` and
`assistant/references/` into one Google Doc, one tab per file, titled with its
path (`prompt/posture.md`). `import` writes the doc's edits back into the files.
`prompt/variants/` stays out: it is include plumbing, not prose.

```bash
# from prompt-doc/
uv sync
uv run pytest
uv run prompt-doc export            # --all rewrites every tab, --force overwrites
uv run prompt-doc import --dry-run  # the diff, nothing written
uv run prompt-doc import            # --force takes the doc's side of a conflict
```

## Rules the tool keeps

- **The tab is the file's exact text.** Markdown stays visible as typed: `##`,
  `**`, `<!-- -->`, `{{name}}`. Import writes the tab's text byte for byte, apart
  from line breaks: Docs' soft line break (Shift+Enter) and any `\r` become `\n`,
  and the file ends in one newline.
- **Styles are a reading aid, applied on export and never read.** Headings get
  heading styles, which also gives the doc an outline. Bold and italic text is
  bold and italic, comments are green, `{{includes}}` purple, inline code
  monospace, and markdown symbols grey. An editor's own formatting changes
  nothing. A rewrite resets each tab's styles, so text typed next to a styled
  range looks like that range until the next rewrite.
- **Neither direction overwrites the other side's unsynced edits.** The Read me
  tab records the commit the export came from. That gives a three-way check per
  file (see `sync.py`): export refuses a tab with unimported edits or open
  suggestions, and import skips a file changed in both places. `--force` takes
  the side the command writes to.
- **Export needs a clean tree** under `assistant/prompt` and
  `assistant/references`, so the stamped commit is what the doc holds.
- **Export rewrites only the tabs the repo changed.** A rewrite detaches the
  doc's margin comments on that tab. `--all` rewrites every tab, to refresh
  styles after a round of edits.
- **Import never commits.** Review `git diff`, run `prompt` and `dotnet test` in
  `runtime/` (they catch a broken include or a dropped `{{escalation-list}}`),
  then commit. Then export, so the doc is stamped with the new commit.
- **Only accepted text is imported.** Open suggestions read as rejected, and
  import says which tabs have them. Tables, images and smart chips cannot
  become markdown, so a tab holding one is skipped with a message.
- **Files only, never new ones.** A tab whose title matches no file is ignored.
  To add a reference, add the file and its `sources.yaml` entry in the repo and
  export.

## Setup

1. In a Google Cloud project, enable the Google Docs API and create a service
   account with a JSON key. Keep the key outside the repository.
2. A person creates an empty Google Doc and shares it as Editor with the service
   account's email and with the editor. New service accounts have no Drive
   storage, so they cannot own the doc. Keep sharing to named people: the
   references are dotFIT's own guidance.
3. In the root `.env`: `DOTFIT_PROMPT_DOC_ID` (the doc's URL or ID) and
   `DOTFIT_PROMPT_DOC_CREDENTIALS` (the key file's path, absolute or
   repo-relative).
4. `uv run prompt-doc export`. The doc's empty first tab becomes the Read me tab.

Tests use a fake doc (`tests/conftest.py`) that applies the requests against
each tab's UTF-16 length, so they need no Google account.
