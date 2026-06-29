# Tests

Two complementary layers:

- **`ActorBank.Tests/`** — xUnit + Orleans `TestingHost`. Hermetic ACID/correctness tests against a
  real in-memory cluster (no Docker, no Postgres). Run on every commit / in CI.
- **`k6/`** — load/consistency/performance tests against the live HTTP API. Prove ACID *under load*
  and measure throughput/latency.

## xUnit (correctness)

```bash
dotnet test tests/ActorBank.Tests        # 22 cases (17 methods), ~2s, no Docker
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
| `Ledger_is_correct_across_page_boundaries` *(theory)* | the co-located current page flushes correctly at 127 / 128 / 129 / 256 entries |
| `Apply_interest_credits_and_records_an_entry` | interest credit + ledger entry |
| `Cold_read_model_returns_null` | the balance read model is empty until published |
| `Publish_records_the_balance` | a published balance reads back |
| `Stale_publish_is_ignored_and_newer_one_wins` | the read model's version guard |
| `Account_operations_return_a_monotonic_version` | money ops return an increasing version |
| `ShardOf_is_in_range` *(theory)* | interest-shard hash maps into `[0, shards)` |
| `ShardOf_is_stable_for_the_same_id` | the hash is deterministic across calls |
| `ShardOf_spreads_accounts_across_shards` | accounts distribute across the pool |
| `ShardOf_rejects_a_nonpositive_shard_count` | guards invalid input |

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

**Or run the app with [.NET Aspire](../README.md#9-run-it)** — `dotnet run --project ActorBank.AppHost`
pins the API to `:8080`, so the same `./tests/run.sh` commands work unchanged (plus a live dashboard).
Docker Compose (`--scale app=N` + nginx) is still the better harness for **load** runs across silos.

Most scripts accept env knobs, e.g. `ACCOUNTS=20 VUS=30 DURATION=40s ./tests/run.sh consistency.js`
or `POOL=200 PEAK=200 WRITE_RATIO=0.1 ./tests/run.sh load.js` (`WRITE_RATIO` 0–1 sets the read/write
mix). `BASE_URL` overrides the target if you aren't on `:8080`.

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

## Latest numbers (local Docker — Ryzen 7 5700G, 8c/16t, `--scale app=2`)

| `load.js` workload (set `WRITE_RATIO`) | Throughput |
|---|---|
| read-only (`WRITE_RATIO=0`) | ~10,700 req/s |
| 90 / 10, realistic bank mix (`WRITE_RATIO=0.1`) | ~7,300 req/s |
| 50 / 50 (`WRITE_RATIO=0.5`) | ~3,100 req/s, p95 ~80ms |
| write-only (`WRITE_RATIO=1`) | ~2,000 writes/s |
| `consistency.js` | money conserved under concurrent transfers (the headline) |

Reads ride the **balance read model** (≈0 DB commits), so a read-heavy workload is far cheaper than a
write — see [README §7.6](../README.md#76-worked-example-a-100-billion-request-a-day-bank) for the full
measured breakdown and the per-op commit analysis. Throughput is bound by the single PostgreSQL (the
transaction coordinator is distributed); scaling writes means sharding the database (README §7.4).
