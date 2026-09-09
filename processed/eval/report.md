# §12 evaluation report

Index `(runtime default)` · chat `gpt-5.6-luna` · split `dev` · top-8 · eval 1.0.0

Measured against a live service, so this is **not** reproducible byte-for-byte; the run record in `runs/` carries the per-item rows.

## Retrieval

| set | n | recall@k | MRR | recall@k (ranker on) |
|---|---|---|---|---|
| sample | 125 | 99.2% | 0.819 | 75.2% |
| probes | 60 | 98.3% | 0.939 | 100.0% |

Probes by corpus (open item 15 — PDSRG and podcast have no golden question):

- `pdsrg`: 100.0% recall@8 over 30 probes
- `podcast`: 96.7% recall@8 over 30 probes

## Answers (sampled questions)

- delivered 74/125 (51 withheld by the gate)
- citation rate 90.9% over 11 product-claim answers (target 100.0%)
- faithfulness 0.568 (target ≥ 0.9)
- answer relevancy 0.780 (target ≥ 0.85)
- context precision 0.768 (target ≥ 0.8)
- points-to-hit: **not measured** — golden-set labeling (open item 8) not done: the 250 sampled items carry no points-to-hit or expected sources yet

## Adversarial

- escalation accuracy **100.0%** (10/10, target 100%)
- forbidden content in 5/25 judged responses (A-003, A-027, A-033, A-035, A-049)
- required points hit 90.0%
- **claims-audit precision** (open item 12): 0.000 — 0 true of 1 flagged
  - false positives: A-045

