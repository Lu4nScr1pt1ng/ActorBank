# Tests

Two complementary layers:

- **`ActorBank.Tests/`** — xUnit + Orleans `TestingHost`. Hermetic ACID/correctness tests against a
  real in-memory cluster (no Docker, no Postgres). Run on every commit / in CI.
- **`k6/`** — load/consistency/performance tests against the live HTTP API. Prove ACID *under load*
  and measure throughput/latency.

## xUnit (correctness)

```bash
dotnet test tests/ActorBank.Tests        # 8 tests, ~1s, no Docker
```

| Test | Asserts |
|------|---------|
| `Deposit_and_withdraw_track_balance` | basic balance maths |
| `Overdraw_is_rejected_and_balance_unchanged` | insufficient funds throws, state untouched |
| `Transfer_moves_money_atomically` | both legs commit |
| `Transfer_to_unopened_account_rolls_back_the_debit` | **atomicity** — failed credit rolls back the debit, no phantom ledger entry |
| `Money_is_conserved_under_concurrent_transfers` | **isolation** — 120 concurrent transfers, total conserved |
| `Concurrent_deposits_have_no_lost_updates` | 100 concurrent deposits → exact balance |
| `Ledger_pages_across_many_transactions` | paging across the 128-entry page boundary |
| `Apply_interest_credits_and_records_an_entry` | interest credit + ledger entry |

Transfers are composed the same way as the API (one transaction over both account grains, legs in
id order) via a small test-only coordinator grain.

## k6 (load / performance)

Run against the live API (default `http://localhost:8080`) via the `grafana/k6` Docker image —
no host install needed.

### Run

Everything goes through **one URL** — the nginx load balancer on `:8080` — which fans out across
the silo replicas. There's nothing to change between single-node and cluster runs.

```bash
docker compose up -d                 # 1 silo behind nginx on :8080
# or: docker compose up -d --scale app=3   # 3 silos behind nginx (nginx auto-discovers them)

./tests/run.sh smoke.js         # functional + auth + ACID rollback (1 pass, all checks must hold)
./tests/run.sh consistency.js   # ACID under load — asserts money is conserved
./tests/run.sh load.js          # throughput / latency with pass-fail thresholds
./tests/run.sh auth.js          # every authorization rule, under concurrency
./tests/run.sh stress.js        # ramp VUs to find the breaking point
```

Most scripts accept env knobs, e.g. `ACCOUNTS=20 VUS=30 DURATION=40s ./tests/run.sh consistency.js`
or `POOL=40 PEAK=60 ./tests/run.sh load.js`. `BASE_URL` overrides the target if you aren't on `:8080`.

## What each script proves

| Script | Proves |
|--------|--------|
| `smoke.js` | Every endpoint works; `401` no-token, `403` cross-account, `409` overdraw, and a transfer to an unopened account returns `404` **and rolls the debit back**. |
| `consistency.js` | Many VUs fire random concurrent transfers; teardown sums all balances and asserts the total is **unchanged** — atomicity + isolation under load. |
| `load.js` | 50/50 read/write mix, ramping VUs; gates on error rate `<2%` and p95 latency. Reports throughput. |
| `auth.js` | Under 10 concurrent VUs, every authorization rule holds (`401`/`403`/`409`, tampered token, balance untouched by attacks) — `checks` must be 100%. |
| `stress.js` | Ramps to 150 VUs, aborting if errors exceed 10%, to find the capacity limit. |

## What the load test found (and we fixed)

The first `consistency.js` run **surfaced a deadlock**: `Transfer` used to run *on the source
account's grain* and call the target grain, so two opposing transfers (A→B and B→A) each held their
own activation while waiting for the other's — a classic Orleans turn-based deadlock. Throughput
collapsed to ~3 req/s with 30s timeouts. Money was still conserved (failed transactions roll back),
but the behaviour was unacceptable. Fixed by orchestrating the transfer transaction from the API via
`ITransactionClient` (account grains never call each other).

## Latest numbers (local Docker)

| | 1 silo | 3 silos (`--scale app=3`, via nginx) |
|---|--------|--------------------------------------|
| `consistency.js` | conserved, ~157 transfers/s, p95 ~160ms | conserved, ~225 transfers/s, p95 ~243ms |
| `load.js` | ~900 req/s, p95 ~60ms, 0 failures | ~985 req/s, p95 ~73ms, 0 failures |

Throughput scales with nodes but isn't 3× — Postgres and the transaction coordinator are a shared
back end. Going through nginx costs ~10% vs hitting nodes directly (open-source nginx can't keep a
keepalive pool to a dynamically-discovered upstream); irrelevant at this load, where the proxy is
nowhere near its limit.
