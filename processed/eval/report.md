# §12 evaluation report

Index `(runtime default)` · chat `(no judge)` · split `dev` · top-8 · eval 1.0.0

Measured against a live service, so this is **not** reproducible byte-for-byte; the run record in `runs/` carries the per-item rows.

## Retrieval

| set | n | recall@k | MRR | recall@k (ranker on) |
|---|---|---|---|---|
| sample | 12 | 100.0% | 0.847 | 83.3% |
| probes | 12 | 100.0% | 0.861 | 100.0% |

Probes by corpus (open item 15 — PDSRG and podcast have no golden question):

- `pdsrg`: 100.0% recall@8 over 12 probes

## Adversarial

- escalation accuracy **100.0%** (10/10, target 100%)
- forbidden content in 0/0 judged responses
- required points hit —
- **claims-audit precision** (open item 12): — — 0 true of 0 flagged

