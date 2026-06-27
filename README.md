# ActorBank

A small but production-shaped bank built on the **virtual actor model** with
[Microsoft Orleans](https://learn.microsoft.com/dotnet/orleans/) `10.2.1` on **.NET 10**.

Every bank account is a virtual actor (an Orleans **grain**). Orleans processes **one message at a
time per grain**, so a balance is updated by exactly one logical thread of control — **no locks, no
race conditions, no lost updates** — and money transfers between two accounts are **ACID** via
Orleans transactions. This document explains the actor model, the runtime machinery that makes it
work, exactly how ActorBank uses it, and — in depth — how the same idea scales from one laptop to
WhatsApp-class throughput.

---

## Table of contents

1. [The actor model — a primer](#1-the-actor-model--a-primer)
2. [The Erlang/BEAM → WhatsApp lineage](#2-the-erlangbeam--whatsapp-lineage)
3. [Virtual actors: Orleans grains](#3-virtual-actors-orleans-grains)
4. [How ActorBank uses the actor model](#4-how-actorbank-uses-the-actor-model)
5. [The machinery up close](#5-the-machinery-up-close)
6. [What it does — functionality & API](#6-what-it-does--functionality--api)
7. [How it scales — from one box to a billion messages](#7-how-it-scales--from-one-box-to-a-billion-messages)
8. [Project layout](#8-project-layout)
9. [Run it](#9-run-it) · [Testing](#10-testing) · [Configuration](#11-configuration) · [Roadmap](#12-roadmap) · [Requirements](#13-requirements)

---

## 1. The actor model — a primer

The **actor model** (Carl Hewitt, 1973) is a model of concurrent computation with one primitive: the
**actor**. An actor is:

- **Private state** — memory only the actor itself can touch. Nobody reads or writes it directly.
- **Behavior** — code that runs when the actor receives a message.
- **A mailbox** — an inbox of messages, processed **one at a time**.

Actors communicate **only** by sending each other asynchronous messages. There is **no shared
memory**. In response to a message an actor may: update its own state, send messages to other
actors, or create new actors.

That single rule — *one message at a time, no shared state* — is what makes the model so powerful for
correctness:

> If only one message is ever processed at a time, and nobody else can touch your state, then
> **there is nothing to lock**. Race conditions, torn reads, and lost updates are impossible *by
> construction*, not by careful discipline.

**Why a bank loves this.** The classic concurrency bug is the lost update: two threads read a balance
of `100`, each adds `50`, and both write `150` — a deposit vanishes. The usual fix is a lock around
the balance. In the actor model there is no lock because there is no concurrency *inside* an actor: an
account that processes one message at a time **is** a serialized balance. Correctness falls out of the
model itself.

```
        message in                                 message in
   ────────────────────►  ┌────────────────┐  ◄────────────────────
        deposit(50)       │     Account    │       withdraw(30)
                          │  ── mailbox ── │  ← messages queue here
   one at a time  ───────►│   Balance=100  │  one at a time
                          │    (private)   │
                          └────────────────┘
              processes deposit → 150, THEN withdraw → 120
              never both at once → no lock, no lost update
```

---

## 2. The Erlang/BEAM → WhatsApp lineage

The actor model's most famous production home is **Erlang** and its virtual machine, **BEAM**.
Erlang's "processes" are actors: extremely cheap (millions per node), fully isolated (a crash in one
can't corrupt another), scheduled pre-emptively, and communicating only by message passing. Erlang
added the **"let it crash"** philosophy: don't defensively code every error path — isolate work in a
process and let a *supervisor* restart it cleanly.

This is what let **WhatsApp** move on the order of **tens of billions of messages per day** with a
famously tiny engineering team, routinely holding **~2 million live connections on a single server**.
Each connection and conversation was just another lightweight actor; the BEAM scheduler spread
millions of them across cores, and supervision trees kept the system healthy. The lesson that carries
straight into ActorBank:

> When your domain is naturally made of **many independent entities** — chat sessions, or bank
> accounts — modelling each as its own actor makes the workload *embarrassingly parallel*. Two
> different accounts never contend, so adding hardware adds throughput almost linearly.

**What Orleans keeps, and what it changes.** ActorBank uses the same actor semantics (isolation,
message passing, single-threaded turns), but Orleans is a **virtual** actor runtime — it removes the
parts of the Erlang model that are hardest to operate at scale (manual process spawning, placement,
and supervision). That's the next section.

---

## 3. Virtual actors: Orleans grains

Classic actors (Erlang, Akka) are objects you must explicitly **create, place, supervise, and
destroy**. You worry about *where* an actor lives, whether it's still alive, and how to find it again.

Orleans introduced the **virtual actor** (the **grain**) to make all of that the runtime's job:

| Classic actor | Orleans grain (virtual actor) |
|---|---|
| You spawn it; it exists until it crashes/stops | **Always "exists"** — addressable by identity at any time |
| You manage its lifecycle | **Activated on demand**, **deactivated when idle** (the runtime decides) |
| You track which node it's on | **Location-transparent** — you call it by identity; Orleans routes the message |
| You wire up persistence yourself | **Integrated persistence** — declarative state providers |
| You build supervision/restart | Activations are recreated automatically on failure/rebalance |

A grain has an **identity** = *grain type* + *key*. `IAccountGrain` with key `"alice"` is a specific
account that conceptually always exists; the first message to it causes Orleans to **activate** an
instance on some silo, load its state, and run. Crucially, Orleans guarantees **single-threaded
execution per activation**: one message (one "turn") at a time. You get the actor model's correctness
guarantee with none of the manual lifecycle bookkeeping.

A **silo** is one server process hosting many grains; a set of silos sharing a `ClusterId` forms a
**cluster**. You never address a silo — you address a grain, and the cluster places it for you.

---

## 4. How ActorBank uses the actor model

ActorBank is built almost entirely out of grains. Each grain type uses the single-threaded guarantee
to make a specific correctness property *free*.

### `AccountGrain` — the account is the actor
*Key = account id (the owner's username, e.g. `"alice"`).* The heart of the system. Because Orleans
serializes every message to an account, deposits, withdrawals, and transfer legs are applied one at a
time — **no lost updates, ever**, with no locks in the code. Its transactional state is deliberately
tiny and **constant-size**:

```csharp
[GenerateSerializer]
public sealed class AccountState
{
    [Id(0)] public bool   IsOpen;
    [Id(1)] public string Owner;
    [Id(2)] public decimal Balance;
    [Id(3)] public long   LedgerCount;   // history lives elsewhere (see below)
}
```

The money path only ever serializes those four fields, so it stays O(1) no matter how much history an
account accumulates.

### `LedgerPageGrain` — append-only history, sharded into actors
*Key = `"{accountId}/{page}"`, 128 entries per page.* A naïve account would keep its whole transaction
list in state — but then every write re-serializes a list that grows forever (O(n) per write). Instead,
history is split into bounded **page actors**. An append rewrites only the *current* page, so write
cost stays bounded for the life of the account. Each append commits **inside the same transaction** as
the balance change that caused it, so a rolled-back transfer can never leave a phantom ledger entry.

### `CredentialGrain` — one actor per user
*Key = username.* Holds a salted **PBKDF2-SHA256** password hash bound to an account id, used to issue
post-quantum (ML-DSA-65) bearer tokens at the API edge. Single-threaded access means `Register` and
`Authenticate` can never race; no transaction is needed because a credential only ever touches itself.

### `InterestSweepGrain` — coordinator actors for scheduled work
*Key = shard index.* Rather than one durable reminder per account (which would mean millions of
reminders), a small fixed pool of sweep coordinators each own **one** reminder. On each tick a sweep
grain credits every account enrolled in its shard by calling `AccountGrain.ApplyInterest`. Accounts map
to shards by a stable FNV-1a hash (`InterestSharding`).

### Turns, transactions, and deadlock-freedom

- **One turn at a time.** Every grain method is a *turn*. While a turn `await`s an outgoing call, the
  grain does not start another message — its state can't be observed half-updated.
- **ACID across grains.** Account methods are `[Transaction(TransactionOption.CreateOrJoin)]` over
  `ITransactionalState<T>`. A deposit enlists the account *and* its current ledger page in one
  two-phase commit; a transfer enlists up to four grains (both accounts + both pages). Either
  everything commits or everything rolls back — **no manual compensation logic**.
- **Transfers are orchestrated by the API, not by grains.** `AccountEndpoints` opens one transaction
  via `ITransactionClient` and calls the two legs (`DebitForTransfer` + `AcceptTransfer`) itself, in a
  **fixed account-id order**. Account grains never call each other.

  > **Why the ordering matters.** If account grains called each other directly, two opposing transfers
  > (A→B and B→A) could each hold their own turn while waiting on the other — a classic turn-based
  > **deadlock**, which a load test actually caught. Issuing both legs from the coordinator in a
  > consistent id order means locks are always acquired in the same direction, so no cycle can form.

- **A grain never calls itself.** That's why scheduled interest lives on a *separate* grain
  (`InterestSweepGrain`) that calls the account — a grain awaiting a call to its own identity would
  deadlock on its own turn.

```
A transfer, as one ACID transaction (2PC), composed by the API:

   POST /accounts/alice/transfer  ──┐
                                    ▼
                         ┌─────────────────────────────┐
                         │ API transaction coordinator │   (ITransactionClient)
                         └─────────────────────────────┘
              id-ordered legs │                       │
                              ▼                        ▼
                   ┌────────────────────┐    ┌────────────────────┐
                   │ AccountGrain alice │    │  AccountGrain bob  │
                   │    Balance -300    │    │    Balance +300    │
                   └─────────┬──────────┘    └─────────┬──────────┘
                             ▼                          ▼
                   ┌────────────────────┐    ┌────────────────────┐
                   │  LedgerPageGrain   │    │  LedgerPageGrain   │
                   │  alice/3  +entry   │    │  bob/7   +entry    │
                   └────────────────────┘    └────────────────────┘
            All four enlisted in ONE transaction → commit together or roll back together.
```

---

## 5. The machinery up close

The four bullets above are the *what*. This section is the *how* — the Orleans mechanics that make the
actor guarantees real, and the design choices ActorBank makes against each one. These are the details
that matter most for understanding (and later scaling) the system.

### Single activation, cluster-wide
The single-threaded guarantee would be worthless if two silos could each spin up their own copy of
account `"alice"`. They can't: Orleans keeps a **grain directory** mapping each active grain id to the
*one* silo currently hosting it. A call to `"alice"` from anywhere in the cluster is routed to that one
activation. So "one message at a time" holds **across the whole cluster**, not just within a process —
this is exactly what makes a balance safe without distributed locks.

### Non-reentrancy and what a "turn" really costs
ActorBank's grains are **non-reentrant** (the default; none are marked `[Reentrant]`). A grain runs one
request to completion — *including every `await` inside it* — before it dequeues the next. Two
consequences worth internalising:

- **It's why a self-call deadlocks.** If `AccountGrain` awaited a call to its own id, the second turn
  could never start until the first finished, and the first is waiting on the second. Hence the
  separate `InterestSweepGrain`.
- **It's why reads aren't free.** `GetStatement` awaits reads of one or more `LedgerPageGrain`s; for
  the duration, the account activation is busy and a concurrent `Deposit` to the same account *queues
  behind it*. Reads and writes to one account are serialized against each other — fine per-account,
  but the reason a read-heavy hot account wants the read-model in §7.

### Two flavours of persistence — and why each grain picks one
Orleans separates *storage* from *consistency*, and ActorBank uses both deliberately:

| Grain | State holder | Why |
|---|---|---|
| `AccountGrain`, `LedgerPageGrain` | `ITransactionalState<T>` | They participate in **multi-grain ACID** transactions (transfers, balance+ledger). Transactional state is what lets Orleans run two-phase commit across them. |
| `CredentialGrain`, `InterestSweepGrain` | `IPersistentState<T>` | They only ever touch **their own** state. Plain persistent storage (`WriteStateAsync`) is cheaper — no transaction machinery needed. |

The lesson: don't pay for distributed transactions where a single-grain write is correct. Reach for
`ITransactionalState<T>` only when an operation must be atomic across *several* grains.

### Distributed ACID transactions, not DB transactions
A transfer's atomicity does **not** come from PostgreSQL. Orleans runs its own **two-phase commit**: a
transaction coordinator (here the API via `ITransactionClient`, or a `TestTransferGrain` in the suite)
enlists the participating grains, each prepares its transactional state, and the commit is agreed
in-cluster before anything is durably written. `TransactionOption.CreateOrJoin` means each account
method *joins* an ambient transaction if one exists, or *creates* one if called standalone — so the
same `Deposit` method is correct whether called directly or as a leg of a transfer. This is why the
transaction can span grains whose state lives in *different* databases (the basis for storage sharding
in §7).

### Reminders vs timers
Scheduled interest must survive restarts, so it uses Orleans **reminders** — durable, persisted to the
reminders table, re-fired by the cluster even after a crash or rebalance. (Orleans also has in-memory
**timers**, which are cheaper but vanish on deactivation; wrong choice for "credit interest every
period, guaranteed".) §5's sweep design is about keeping the *number* of these durable reminders fixed
regardless of account count.

### Versioned serialization
Every grain message and stored state is marked `[GenerateSerializer]` with explicit `[Id(n)]` field
tags (see `AccountState`, `TransactionRecord`, `AccountStatement`). The tags give a **forward/backward-
compatible wire and storage format**: you can add a field with a new id and roll silos one at a time
without breaking in-flight messages or stored state — the prerequisite for zero-downtime upgrades of a
running cluster.

### Co-hosted silo + API, with a stateless edge
Each app process is **both** an Orleans silo *and* the ASP.NET Core API (`Program.cs` calls
`AddActorBankSilo()` and then builds the web host). `docker compose up --scale app=N` therefore
launches N processes that all join one cluster. **nginx** round-robins HTTP across them (re-resolving
Docker DNS every few seconds, so new replicas are picked up with no config change). The HTTP edge is
**stateless** — any replica can serve any request — while the **stateful** grains are placed across the
silos by Orleans. A request that lands on silo 2 but needs account `"alice"` on silo 1 just does a
cross-silo grain call; location transparency makes that invisible to the endpoint code.

```
        ┌─────────┐     HTTP (stateless, round-robined)
client ─┤  nginx  ├──────────────┬───────────────┬───────────────┐
        └─────────┘              ▼               ▼               ▼
                          ┌─────────────┐  ┌─────────────┐  ┌─────────────┐
                          │   app #1    │  │   app #2    │  │   app #3    │
                          │ API + silo  │  │ API + silo  │  │ API + silo  │
                          └─────┬───────┘  └─────┬───────┘  └──────┬──────┘
                                └─────── one Orleans cluster ────┘
                                  grains placed across silos;
                                  calls routed by the grain directory
```

---

## 6. What it does — functionality & API

**Capabilities**

- **Accounts** — open an account (with an optional opening deposit), check balance, read a statement.
- **Money movement** — deposit, withdraw (overdraft-protected), and **ACID transfer** between accounts.
- **Audit ledger** — every money event is appended to a strongly-consistent, paged history.
- **Scheduled interest** — a sharded sweep credits interest on a recurring tick.
- **Auth** — credentials issue post-quantum (ML-DSA-65) bearer tokens; `/accounts` requires a valid
  token whose subject equals the account id (you can only touch your own account).
- **Durability** — all grain state persists to PostgreSQL and survives restarts.

**Auth** (anonymous):

| Method & path | Body | Result |
|---------------|------|--------|
| `POST /auth/register` | `{ "username", "password" }` | 201 (you control the account named after your username) |
| `POST /auth/token` | `{ "username", "password" }` | 200 + `{ accessToken, ... }` / 401 |
| `GET  /auth/jwks` | — | 200 + `{ algorithm, keyId, publicKey }` |

**Accounts** (require `Authorization: Bearer <token>`; the token's subject must equal `{id}`):

| Method & path | Body | Result |
|---------------|------|--------|
| `POST /accounts/{id}/open` | `{ "owner": "Alice", "openingDeposit": 1000 }` | 201 + statement |
| `POST /accounts/{id}/deposit` | `{ "amount": 250, "description": "Paycheck" }` | 200 + balance |
| `POST /accounts/{id}/withdraw` | `{ "amount": 75 }` | 200 + balance / 409 |
| `POST /accounts/{id}/transfer` | `{ "toAccountId": "bob", "amount": 300 }` | 204 / 404 / 409 |
| `GET  /accounts/{id}/balance` | — | 200 + balance |
| `GET  /accounts/{id}/statement` | `?take=N` | 200 + recent transactions |

Errors: `401` no/invalid token or bad credentials, `403` token doesn't own the account,
`404` account not open, `409` insufficient funds / invalid operation, `400` invalid amount.

### Example

```bash
B=http://localhost:5080
# The account you control is your username — here, "alice".
curl -s -XPOST $B/auth/register -H 'content-type: application/json' \
  -d '{"username":"alice","password":"correct-horse"}'
TOKEN=$(curl -s -XPOST $B/auth/token -H 'content-type: application/json' \
  -d '{"username":"alice","password":"correct-horse"}' | jq -r .accessToken)

curl -s -XPOST $B/accounts/alice/open -H "authorization: Bearer $TOKEN" \
  -H 'content-type: application/json' -d '{"owner":"Alice","openingDeposit":1000}'
curl -s $B/accounts/alice/statement -H "authorization: Bearer $TOKEN"
```

A transfer to a non-existent / unopened account returns `404` **and the debit is rolled back** — the
transaction guarantees both legs commit together or not at all.

### The ledger trade-off (consistency vs. participants)

Committing ledger appends *inside* the balance transaction enlists more participants in the 2PC (a
deposit = account + its page; a transfer = up to four grains). That is the deliberate price of a
**strongly consistent audit log**: a rolled-back transfer can never leave a phantom entry, and a
committed one is always recorded. The only lever to reduce participants would be an *eventually
consistent* (post-commit) ledger — which trades away audit atomicity, something a bank should not do.

---

## 7. How it scales — from one box to a billion messages

The actor model gives ActorBank an unusually clean scaling story, *and* a small set of honest ceilings.
This section walks the whole picture: the model, a layer-by-layer bottleneck map, the concrete lever
for each layer, a back-of-envelope to a billion operations/day, and the floors you genuinely cannot
cross.

### 7.1 The scaling model

- **The account is the unit of parallelism.** Two different accounts are two different actors on
  (potentially) two different silos — they never contend. A workload of *N* independent accounts is
  *N*-way parallel. This is the same property that made BEAM/WhatsApp scale: independent per-entity
  actors are embarrassingly parallel.
- **The edge is stateless; the silos are stateful.** Add API capacity by adding replicas behind nginx;
  add grain capacity by adding silos to the cluster. They scale independently because they're the same
  process today but separable tomorrow (you can split "frontend" silos from "grain" silos).
- **Idle accounts cost nothing.** A virtual actor that isn't being used is deactivated; only *active*
  accounts occupy memory. A bank with 100M accounts but 50k active at any instant pays for the 50k —
  exactly how WhatsApp kept millions of *possible* sessions cheap.

### 7.2 Where the work actually serializes (bottleneck map)

| Layer | What can serialize / saturate | Lever to scale it | Status |
|---|---|---|---|
| HTTP edge | CPU on token verify + JSON | add app replicas behind nginx; verification is already lock-free per-thread | ✅ done |
| Compute (grains) | a silo's CPU/RAM | add silos; Orleans rebalances activations | ✅ done |
| **One account** | a single activation is serial (incl. its reads) | **escrow-striped account** (§7.4) | 🔜 lever known |
| **Storage** | every commit writes **one PostgreSQL** | **shard grain storage across N DBs** (§7.3) | 🔜 lever known |
| Reminders | one durable reminder per account would flood | fixed pool of `InterestSweepGrain` shards | ✅ done |
| Read load | reads also ride the transaction subsystem | **CQRS read model** (§7.5) | 🔜 lever known |
| **Transactions** | **per-cluster 2PC coordination — the *measured* ceiling** (a few k ops/s, CPU idle) | fewer participants per op + **shard into independent cells** (§7.6) | 🔬 measured |

The headline ceiling is **transaction coordination**: §7.6 shows a single cluster + single Postgres
tops out at a few thousand transactional ops/sec *with the CPU almost idle*, because the limiter is
2PC round-trips, not compute. Raising it means doing **fewer transactions per op** and **running more
clusters**, not buying a bigger box.

### 7.3 Scaling storage: shard grain state across N databases

Today every transactional commit lands in one Postgres, so the cluster ultimately tops out at roughly
one database. The lever is a **sharded grain-storage provider**: route each grain's state to one of
*N* physical Postgres instances by a **stable hash of the grain key**, behind the same provider names
the grains already bind to (`accountStore`, `ledgerStore`, `credentialStore`). Key design points:

- **Stable hashing, not `string.GetHashCode`.** A grain must always map to the same shard across
  processes and restarts (FNV-1a or similar), or its state would "move" on every boot.
- **Co-locate an account with its own ledger pages.** Route on the account-id *prefix* (`"alice"` and
  `"alice/3"` → same shard) so a single account's deposit is a **one-database commit**. Only
  cross-account transfers between different shards become two-database commits — still ACID, because
  Orleans coordinates the 2PC in-cluster regardless of which DB backs each grain (§5).
- **Logical-shard indirection.** Hash into many fixed logical buckets (e.g. 1024), then map buckets →
  physical DBs by range. Growing from 3 to 6 databases remaps ranges instead of rehashing every key.
- **The control plane stays central.** Cluster **membership** and **reminders** need one global view,
  so they live on a single small "control" DB. They're low-traffic bookkeeping, not the write path —
  centralising them is a non-issue for throughput.

Result: write throughput scales with the number of shard databases — *provided* storage is the
bottleneck. The benchmark in §7.6 shows that on this build the limiter is actually the per-cluster
**transaction coordinator**, not the database, so sharding storage under one cluster helps only up to
that coordinator's ceiling. Past it, you shard the whole *cluster* into cells (§7.6).

### 7.4 Scaling a hot account: escrow / striped balances

A central account that *everyone* hits (a clearing or settlement account) is one activation = serial,
no matter how many silos you add. Plain striping (split into N sub-balances, credits fan out) fixes
*credits* but not *debits*, because a debit must check `amount <= balance`. The technique that wins
**both** directions is the **escrow method** (O'Neil, 1986): split the account into *N* **stripe
grains, each holding a real slice of the balance**.

- **Credit** → pick a stripe, add locally. Parallel.
- **Debit** → pick a stripe; if *that* stripe has enough, subtract locally. Parallel, zero coordination.
- **Slow path** (rare): only when a stripe is too low does the operation run a coordinated transaction
  across stripes to gather funds or genuinely reject.

So a well-funded hot account is fully parallel in both directions; it only serializes when the balance
approaches zero — which is the one regime where serialization is *mathematically* required (you cannot
let N actors independently spend the last dollar). Money stays exact (sum of stripes = true balance);
no false overdrafts. Make it **opt-in per account** so normal accounts keep the simple single-grain
model.

### 7.5 Scaling reads: a CQRS read model

Because reads run *on* the account activation (§5), a read-heavy account makes reads and writes queue
against each other. The lever is **CQRS**: have each committed transaction also publish to an
eventually-consistent **read model** (a projection grain, cache, or relational table) that
`GetBalance`/`GetStatement` serve from **without** touching the account's transactional state. Writes
stay strongly consistent; reads get cheap and never block the money path. The cost is read staleness on
the order of the projection lag — acceptable for statements, not for the overdraft check (which stays
on the authoritative grain).

### 7.6 Scaling to a billion requests a day

A billion requests a day is ≈ **11,600 req/sec** on average, with real peaks 3–5× that. Here is the
honest path, grounded in an actual benchmark of the current build (8-core/16-thread box, NVMe,
single cluster + single Postgres, 50/50 read-write account ops through the load balancer):

**What one cluster actually does (measured).**

| Probe | Result | What it tells us |
|---|---|---|
| Bare `/` direct to a silo | **~100,000 req/s** | the .NET/Orleans HTTP layer is not the limit |
| Bare `/` via the load balancer | **~31,700 req/s** | the edge is not the limit either |
| One request, no contention (VU=1) | **~2.6 ms** | the per-op code path is cheap and healthy |
| 50/50 account ops, under load | **~2,000–3,800 req/s** | the real ceiling for transactional ops |
| Read-only vs write-only | **~2,000 vs ~1,625 req/s** | reads are *not* cheaper — they ride the transaction subsystem too |
| Postgres commits per op | **~4.5** (idle baseline ≈ 0) | every op is a distributed transaction |
| CPU at the ceiling | **~1.3 of 16 cores** | **coordination-bound, not compute-bound** |

> **This is the architectural ceiling, stated plainly: a single Orleans cluster + single Postgres caps
> at a few thousand transactional ops/sec regardless of spare cores, because the limiter is 2PC
> coordination round-trips.** Throwing hardware at one cluster does nothing — the cores sit idle.
> 11,600 req/s of transactional operations is therefore *not* reachable on one cluster.

**The path to a billion/day** — in order of leverage:

1. **Take the cheap half off the transaction path (read model, §7.5).** A real bank is read-heavy
   (balance checks, statements, history) — most requests don't move money. Serving those from an
   eventually-consistent projection (updated *post-commit*, never blocking the money path) turns ~50%+
   of traffic into edge-speed reads and multiplies effective throughput several ×. The overdraft check
   stays on the authoritative grain, so no correctness is lost.
2. **Do fewer 2PC participants per write.** Co-locate an account's *current* ledger page inside the
   account grain so a deposit/withdrawal is a **single-participant** transaction and a transfer is two
   (not four); full pages flush to archive `LedgerPageGrain`s. This is flaw-free only while the balance
   stays transactional (transfers require it) — making the balance fully non-transactional would break
   transfer isolation, so that is explicitly *not* on the table.
3. **Shard into independent cells — the real horizontal lever.** Because the limiter is *per-cluster*
   coordination, you scale by running **K independent clusters**, each its own silos + Postgres, and
   routing every account to one cell by a stable hash. The account is the natural shard key, so all
   single-account ops stay inside one cell, and K cells deliver ≈ K× the per-cell ceiling. With today's
   ~3,000 ops/sec/cell, ~14 cells clear the 11,600/s average with peak headroom; combined with levers 1
   and 2 (raising a cell to ~10k+ ops/sec), **~4–6 cells** suffice. The stateless edge scales separately
   behind the load balancer.
   - *Cross-cell transfers* (two accounts in different cells) need a **saga** (reserve → commit, with
     compensation) or a per-cell settlement account; same-cell transfers stay a local 2PC. That is the
     one genuine new tradeoff sharding introduces, and it touches only transfers, not balances.

Net: a billion/day is a **sharding-into-cells** problem plus **moving reads off transactions** — both
behavior-preserving — not a "buy a bigger machine" problem. Every layer has an independent horizontal
lever; the only thing that *must* serialize is a single account near a zero balance.

### 7.7 The irreducible floors (honest limits)

Some things cannot be parallelized away, by the nature of distributed correctness — these are floors,
not defects:

- **A global membership view.** All silos must agree on who is in the cluster. Tiny and low-traffic,
  but centralized.
- **Cross-shard commit cost.** A transfer between accounts on different storage shards commits to two
  databases. It stays ACID; it just isn't free. (Co-location keeps single-account ops to one DB.)
- **The near-zero boundary.** When a striped account is nearly empty, debits must coordinate so two
  actors don't both spend the last dollar. Near the boundary, you *must* serialize.

The takeaway: ActorBank already scales out across silos for compute and across replicas for the edge;
storage sharding and escrow striping are the remaining levers to push the *write* ceiling arbitrarily
high, and the floors above are the small, unavoidable price of keeping the books exactly correct.

---

## 8. Project layout

```
ActorBank/
├─ Directory.Build.props          # shared MSBuild settings (TFM, nullable, analyzers)
├─ Directory.Packages.props       # central NuGet versions (one source of truth)
├─ Dockerfile                     # API image (Alpine 3.23 runtime — OpenSSL 3.5 for ML-DSA)
├─ docker-compose.yml             # PostgreSQL + scalable app/silo + nginx load balancer
├─ nginx/nginx.conf               # LB; auto-discovers app replicas via Docker DNS
├─ db/init/                       # Orleans PostgreSQL schema: storage, clustering, reminders
├─ tests/                         # k6 load/consistency tests + xUnit ACID suite
│
├─ ActorBank.Abstractions/        # the contract (grain interfaces + models)
│  ├─ Accounts/  IAccountGrain.cs, IInterestSweepGrain.cs, InterestSharding.cs
│  ├─ Ledger/    ILedgerPageGrain.cs, LedgerPaging.cs
│  ├─ Auth/      ICredentialGrain.cs
│  ├─ Models/    AccountStatement, TransactionRecord, TransactionType
│  └─ Exceptions/ BankException + domain errors
│
├─ ActorBank.Grains/              # the implementation (the actors)
│  ├─ Accounts/  AccountGrain, AccountState, InterestSweepGrain (sharded interest sweep)
│  ├─ Ledger/    LedgerPageGrain, LedgerPageState
│  └─ Auth/      CredentialGrain, CredentialState, PasswordHasher
│
└─ ActorBank.Api/                 # the host (silo + web API, co-hosted)
   ├─ Endpoints/       AccountEndpoints, AuthEndpoints   (route definitions)
   ├─ Auth/            PqcTokenService, PqcBearerHandler, AccountOwnershipFilter
   ├─ Contracts/       request/response DTOs
   ├─ Infrastructure/  OrleansConfiguration, BankExceptionHandler, InterestOptions
   ├─ Serialization/   ApiJsonSerializerContext (source-gen JSON)
   └─ Program.cs       (thin composition root)
```

---

## 9. Run it

**Everything in Docker** (Postgres + API + nginx load balancer):

```bash
docker compose up -d --build           # 1 silo behind nginx on http://localhost:8080
docker compose up -d --scale app=3     # 3 silos, one cluster; nginx auto-discovers them
```

**Or just Postgres in Docker, API on the host** (faster inner loop):

```bash
docker compose up -d postgres
dotnet run --project ActorBank.Api --urls http://localhost:5080
```

Interactive docs: **`/scalar/v1`**; raw OpenAPI: `/openapi/v1.json`.

> The API container uses an **Alpine 3.23** runtime on purpose — it ships **OpenSSL 3.5**, which
> provides ML-DSA. The default `noble`/`azurelinux` .NET images are still on OpenSSL 3.0/3.3 and the
> PQC auth would fail to start there. The host enables Server GC + Concurrent GC + Tiered PGO
> (`ActorBank.Api.csproj`) — the right profile for a long-running silo.

---

## 10. Testing

Two layers of tests:

- **xUnit ACID suite** (`tests/ActorBank.Tests`) — runs against a real in-memory Orleans cluster with
  the transaction subsystem enabled: deposits/withdrawals, overdraft rejection, atomic transfers,
  rollback leaves no phantom entry, **money conservation under concurrent transfers**, ledger paging,
  interest, and the interest-shard mapping. Run with `dotnet test`.
- **[k6](https://k6.io) load/consistency tests** (`tests/`) — drive the live API via Docker:

```bash
./tests/run.sh smoke.js         # functional + auth + ACID rollback
./tests/run.sh consistency.js   # asserts money is conserved under concurrent transfers (ACID)
./tests/run.sh load.js          # throughput / latency thresholds
```

The consistency test is the headline: many concurrent transfers, then assert the total balance is
unchanged. It's how the transfer deadlock described in §4 was found and the fix verified. See
[`tests/README.md`](tests/README.md).

---

## 11. Configuration

Connection string in `ActorBank.Api/appsettings.json` → `ConnectionStrings:Orleans`
(defaults to `Host=localhost;Port=5432;Database=orleans;Username=orleans;Password=orleans`).
Interest schedule is bound from the `Interest` section (`PeriodMinutes`, `RatePercentPerPeriod`,
`SweepShards`).

---

## 12. Roadmap

**TODO (the scaling levers from §7, highest leverage first per the §7.6 benchmark)**

1. **CQRS read model** — take the read half off the transaction path (§7.5, §7.6 lever 1).
2. **Fewer 2PC participants per write** — co-locate the current ledger page in the account grain
   (§7.6 lever 2).
3. **Shard into independent cells** — route accounts to K clusters by stable hash; the real
   horizontal lever (§7.6 lever 3). Cross-cell transfers via a saga.
4. **Escrow-striped hot accounts** — lift the hot-single-account ceiling (§7.4).
5. **Kubernetes** — `Microsoft.Orleans.Clustering.Kubernetes`, with the silo advertising its pod IP.
6. **Token revocation + refresh** and **rate limiting** on `/auth`.

---

## 13. Requirements

- .NET 10 SDK
- Docker (for PostgreSQL)
- Orleans 10.2.1 (restored from NuGet)
