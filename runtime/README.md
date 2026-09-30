# dotFIT assistant runtime

.NET 10. `DotFit.Assistant` is the library (the loop, the tools, the prompt
assembly, the turn log); `DotFit.Assistant.Cli` is `dotfit`; and
`DotFit.Assistant.Service` is `dotfit-service`, the SSE endpoint the website
calls. How it works is in `docs/architecture.md`; the caller contract is
`docs/website-integration.md`.

## Build and test

```bash
cd runtime
dotnet build && dotnet test
```

Tests are hermetic: a scripted chat client, fake search, synthetic fixtures, no
Azure. A few read committed repo files (the prompt folder, the source registry,
the alias table, the smoke set) to check their structure.

## Configuration

Everything comes from the gitignored root `.env` (template: `.env.example`),
found by walking up from the working directory. Beside it the runtime reads
`pipeline-output/aliases/alias_table.json`, `assistant/prompt/` and
`assistant/sources.yaml`. The whole configuration is validated at startup,
including the prompt variant and the registry: if it starts, it is configured.

## The CLI

```bash
dotnet run --project src/DotFit.Assistant.Cli -- config          # resolved config, keys masked, prompt version
dotnet run --project src/DotFit.Assistant.Cli -- prompt          # the assembled system prompt
dotnet run --project src/DotFit.Assistant.Cli -- search "…"      # the search tool alone, no model
dotnet run --project src/DotFit.Assistant.Cli -- ask "…" --trace --log
dotnet run --project src/DotFit.Assistant.Cli -- chat            # keeps history; `reset`, `exit`
dotnet run --project src/DotFit.Assistant.Cli -- smoke --tier support
```

`config`, `prompt` and `search` make no chat calls. `ask`, `chat` and `smoke`
cost Azure calls; `smoke` without `--tier` runs every item. `--variant <name>`
runs any of them on another prompt variant. `--trace` shows each tool call, its
arguments and what it returned; `--log` prints the turn-log line.

## The service

`POST /ask` streams one turn as Server-Sent Events; `GET /healthz` reports the
configuration. Auth is a shared secret (`DOTFIT_SERVICE_API_KEY`) and the
service refuses to start without one unless `DOTFIT_SERVICE_AUTH=none` says so
explicitly. Every request writes one `dotfit.turn` JSON line to stdout: what the
model did, never what it or the customer said. `DOTFIT_ASSISTANT_DEBUG_TRANSCRIPT=1`
adds a `dotfit.transcript` line with the words, for preview debugging only.

## Deploying (preview VM)

`runtime/deploy/install-user-service.sh` installs `dotfit-service` as a systemd
*user* unit on port 5299: it publishes to `~/.local/share/dotfit/service`,
generates the shared secret into `.env` if absent, installs the unit, enables
linger and waits for `/healthz`. Re-running it is the redeploy. The unit's
working directory is the repo checkout, so a prompt or reference edit takes
effect after `git pull` and a restart; a code change needs the script.

The script also installs `dotfit-turn-log`, the traffic view of the journal:

```bash
dotfit-turn-log -n 20                # one line per request
dotfit-turn-log -f                   # follow live
dotfit-turn-log --queries -n 20      # search queries unclipped
dotfit-turn-log --transcripts -n 5   # the debug transcript blocks
dotfit-turn-log --raw -n 1           # the JSON line, untouched
```

Anything unrecognized passes to journalctl (`--since "1 hour ago"`, `-b`).
`--transcripts` output holds the words themselves: treat it like customer data.
