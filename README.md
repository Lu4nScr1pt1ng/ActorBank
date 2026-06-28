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
7. [How it scales](#7-how-it-scales)
8. [Project layout](#8-project-layout)
9. [Run it](#9-run-it) · [Testing](#10-testing) · [Configuration](#11-configuration) · [Roadmap](#12-roadmap) · [Requirements](#13-requirements)

· [Key concepts (glossary)](#key-concepts-glossary) · [FAQ](#frequently-asked-questions)

---

## Key concepts (glossary)

A one-line definition of every term used in this document. The sections that follow expand on each.

| Term | What it is |
|---|---|
| **Actor** | A unit of computation with **private state**, **behavior**, and a **mailbox**. It shares no memory and processes **one message at a time**, so its state can never be touched concurrently — no locks, no races. |
| **Grain** | Orleans' **virtual actor** — the building block of ActorBank. Identified by *type + key* (e.g. `AccountGrain` with key `"alice"`), it conceptually always exists, is activated on demand, deactivated when idle, and runs single-threaded. You call it by identity; the runtime finds it. |
| **Activation** | The live, in-memory instance of a grain on some silo. Orleans guarantees **exactly one activation per grain id** across the whole cluster — that's what makes "one message at a time" hold cluster-wide. |
| **Account grain** | The `AccountGrain` for one account — *the account itself as an actor*. Its single-threaded turns are why a balance is updated correctly without locks. |
| **Ledger page** | A bounded chunk (128 entries) of an account's transaction history. The **current** page lives inside the account grain; once full it is flushed to an archive `LedgerPageGrain`. Keeps writes O(1) regardless of history length. |
| **Read model** | A **non-transactional projection** of an account's balance, updated *after* each commit. `GetBalance` reads it without opening a transaction, so the read-heavy majority of traffic stays cheap. The authoritative balance still lives on the account grain. |
| **Silo** | One server process that hosts many grains. Each ActorBank process is a silo **and** the web API, co-hosted. |
| **Cluster** | A set of silos that share a `ClusterId` and a backing database; they discover each other and coordinate (placement, transactions). Adding silos to a cluster adds compute. |
| **Shard / sharding** | Splitting grain **storage** across several databases so writes don't all land on one — each grain's state goes to a database chosen by a **stable hash** of its key. It stays a single cluster, so transfers are still ACID. |
| **Stable hash** | A deterministic key→shard mapping (FNV-1a here) that is identical across processes and restarts — unlike `string.GetHashCode`, which is randomized per process. |
| **Hot (shared) account** | A single account that *many* parties hit at once — e.g. a central **clearing/settlement** account. Because one account is single-threaded, it's a serialization point; the fix is **escrow stripes**. |
| **Escrow stripes** | Splitting a hot account into *N* sub-balance grains, each holding a slice of the money, so credits and debits run in parallel — coordinating only when the balance nears zero. |
| **Transaction (2PC)** | Orleans' **distributed ACID transaction**: it makes a change across several grains commit or roll back together, coordinated **in the cluster** (two-phase commit), not by the database. A transfer is one such transaction over two accounts. |
| **Transactional vs persistent state** | `ITransactionalState<T>` participates in 2PC (accounts, ledger pages); `IPersistentState<T>` is a cheaper single-grain write (credentials, read model, interest sweep). |
| **Reminder** | A **durable**, persisted scheduled callback (survives restarts) — used for interest. (Orleans **timers** are the cheaper, in-memory, non-durable alternative.) |
| **Stateless edge** | The API / load-balancer layer. It holds no state, so any replica can serve any request; the **stateful** grains live in the silos behind it. |

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
small and **bounded** — a few scalar fields plus the current ledger page (at most 128 entries):

```csharp
[GenerateSerializer]
public sealed class AccountState
{
    [Id(0)] public bool    IsOpen;
    [Id(1)] public string  Owner;
    [Id(2)] public decimal Balance;
    [Id(3)] public long    LedgerCount;                 // total entries ever written
    [Id(4)] public List<TransactionRecord> CurrentPage; // latest ≤128-entry page; older pages archived
}
```

The money path serializes only this bounded state, so a write stays O(1) no matter how much history
an account accumulates — completed pages are flushed out to archive grains (next).

### `LedgerPageGrain` — archived history, page by page
*Key = `"{accountId}/{page}"`, 128 entries per page.* History is append-only and split into bounded
pages so it never grows the account's hot state. The *current* page lives inside the account grain
itself — so a new entry commits together with the balance as a **single participant** — and once it
fills, it is flushed (in the same transaction) to a `LedgerPageGrain` that holds that completed page
forever. A write stays bounded no matter how long the account has been active, and a rolled-back
operation can never leave a phantom entry.

### `CredentialGrain` — one actor per user
*Key = username.* Holds a salted **PBKDF2-SHA256** password hash bound to an account id, used to issue
post-quantum (ML-DSA-65) bearer tokens at the API edge. Single-threaded access means `Register` and
`Authenticate` can never race; no transaction is needed because a credential only ever touches itself.

### `InterestSweepGrain` — coordinator actors for scheduled work
*Key = shard index.* Rather than one durable reminder per account (which would mean millions of
reminders), a small fixed pool of sweep coordinators each own **one** reminder. On each tick a sweep
grain credits every account enrolled in its shard by calling `AccountGrain.ApplyInterest`. Accounts map
to shards by a stable FNV-1a hash (`InterestSharding`).

### `AccountReadModelGrain` — a cheap, non-transactional balance
*Key = account id.* A projection of the balance, updated *after* each committed operation and read
**without** opening a transaction — so balance checks, the bulk of a bank's traffic, never touch the
transaction coordinator. The authoritative balance still lives on the `AccountGrain` (and still backs
the overdraft check); the read model is eventually consistent, fresh within the publish lag.

### Turns, transactions, and deadlock-freedom

- **One turn at a time.** Every grain method is a *turn*. While a turn `await`s an outgoing call, the
  grain does not start another message — its state can't be observed half-updated.
- **ACID across grains.** Account methods are `[Transaction(TransactionOption.CreateOrJoin)]` over
  `ITransactionalState<T>`. A deposit or withdrawal commits as a **single participant** (balance and
  ledger entry live in the account's own state); a transfer enlists **two** accounts. Either everything
  commits or everything rolls back — **no manual compensation logic**.
- **Transfers are orchestrated by the API, not by grains.** `AccountEndpoints` opens one transaction
  via `ITransactionClient` and calls the two legs (`DebitForTransfer` + `AcceptTransfer`) itself, in a
  **fixed account-id order**. Account grains never call each other.

  > **Why the ordering matters.** If account grains called each other directly, two opposing transfers
  > (A→B and B→A) could each hold their turn while waiting on the other — a turn-based **deadlock**.
  > Composing both legs from the coordinator in a consistent id order means locks are always acquired
  > in the same direction, so no cycle can form.

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
                   │  -300 + ledger     │    │  +300 + ledger     │
                   │  entry (one state) │    │  entry (one state) │
                   └────────────────────┘    └────────────────────┘
            Both accounts enlisted in ONE transaction → commit together or roll back together.
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
- **It's why the balance has a read model.** A `GetStatement` reads the account's pages, so while it
  runs a concurrent `Deposit` to the same account *queues behind it* — reads and writes to one account
  serialize. Balance checks are far more frequent, so `GetBalance` is served from a separate,
  non-transactional **read model** (§7.3) — off the activation entirely — instead of competing for the
  account's turns.

### Two flavours of persistence — and why each grain picks one
Orleans separates *storage* from *consistency*, and ActorBank uses both deliberately:

| Grain | State holder | Why |
|---|---|---|
| `AccountGrain`, `LedgerPageGrain` | `ITransactionalState<T>` | They participate in **multi-grain ACID** transactions (a transfer spans two accounts; a page flush spans an account and its archive page). Transactional state is what lets Orleans run two-phase commit across them. |
| `CredentialGrain`, `AccountReadModelGrain`, `InterestSweepGrain` | `IPersistentState<T>` | They only ever touch **their own** state. Plain persistent storage (`WriteStateAsync`) is cheaper — no transaction machinery needed. |

The lesson: don't pay for distributed transactions where a single-grain write is correct. Reach for
`ITransactionalState<T>` only when an operation must be atomic across *several* grains.

### Distributed ACID transactions, not DB transactions
A transfer's atomicity does **not** come from PostgreSQL. Orleans runs its own **two-phase commit**: a
transaction coordinator (here the API via `ITransactionClient`, or a `TestTransferGrain` in the suite)
enlists the participating grains, each prepares its transactional state, and the commit is agreed
in-cluster before anything is durably written. `TransactionOption.CreateOrJoin` means each account
method *joins* an ambient transaction if one exists, or *creates* one if called standalone — so the
same `Deposit` method is correct whether called directly or as a leg of a transfer. Because the
coordinator lives in the cluster, a transaction can span grains whose state sits in *different*
databases — which is what lets you shard storage across many databases while keeping transfers ACID (§7.4).

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

### The ledger is as consistent as the money

Each ledger entry commits *inside the same transaction* as the balance change that produced it, so the
audit log is exactly as consistent as the money: a rolled-back transfer can never leave a phantom
entry, and a committed one is always recorded. Because the current page lives in the account's own
state, that guarantee costs almost nothing on the hot path — a deposit or withdrawal is a single
transaction participant, and only a transfer spans two accounts.

---

## 7. How it scales

You scale ActorBank the way you scale anything on Kubernetes: **one cluster, and you add silos —
instances (pods) across many machines, each a process full of grains.** Orleans spreads the grains
across the silos and rebalances them automatically; to grow, you add silos. It's the same shape that
let **WhatsApp** carry tens of billions of messages a day on Erlang. For a bank, the independent unit
is the **account**.

### 7.1 The unit of parallelism is the account

Two different accounts are two different actors, placed (potentially) on different silos; they never
touch each other's state, so a workload of *N* active accounts is *N*-way parallel. The **edge is
stateless** — any API replica behind the load balancer serves any request — while the grains are
**stateful**, placed across the silos. And **idle accounts cost nothing**: an actor not in use is
deactivated, so a bank with 100M accounts but 50k active at once pays RAM for only the 50k.

### 7.2 The native path: one cluster, add silos

A **cluster** is a set of silos sharing a `ClusterId` and a backing store; they find each other through
the membership table. **You scale it by adding silos — exactly like scaling a Kubernetes Deployment:**
each silo is a pod, the scheduler spreads pods across machines, and Orleans **rebalances grain
activations across the new silos automatically.** You never place or route grains by hand — you call
them by identity and the cluster finds them.

```
   one Orleans cluster  —  scale out by adding silo pods (kubectl scale / HPA)
   ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐
   │  silo 1  │ │  silo 2  │ │  silo 3  │ │  silo N  │   each: a process full of grains,
   │  (pod)   │ │  (pod)   │ │  (pod)   │ │  (pod)   │   scheduled onto any machine
   └──────────┘ └──────────┘ └──────────┘ └──────────┘
        grains are spread + rebalanced across all silos, automatically
```

Today that's `docker compose up --scale app=N`; in production it's a Kubernetes Deployment with
`Microsoft.Orleans.Clustering.Kubernetes`. **A single cluster runs to hundreds of silos** — adding
compute is transparent, and it is the first (and usually the only) scaling move you need.

> **A silo is a *process*, not a machine.** Each silo is one instance of the app (in ActorBank, the API
> and the Orleans server co-hosted), normally one per pod; k8s schedules those pods onto nodes
> (machines). Pods on *different* machines still join the *same* Orleans cluster — they find each other
> through the shared membership table and connect pod-to-pod (each advertising its pod IP). So **the
> Orleans cluster (a set of silos) is not the same thing as the Kubernetes cluster (a set of
> machines)** — you run the former as pods on top of the latter.

> **What adding pods does and doesn't scale.** Pods add **compute**, so they scale the **read half**
> (balance/statement traffic, served from the read model) and grain-call capacity. They do **not** scale
> **writes** — every write commits to PostgreSQL, and that database is the write ceiling, not CPU. To
> scale writes you shard the **storage** across more databases (§7.4) — still one cluster. So: *pods for
> compute and reads; sharded storage for writes; both under one cluster.*

### 7.3 What stays cheap as the bank grows

The grain design keeps the per-operation cost flat no matter how big the bank or how long an account
has lived:

- **Small, bounded hot state.** An account's transactional state is a few scalar fields plus its current
  ledger page (≤128 entries); the money path never serializes a growing history.
- **Single-participant writes.** A deposit or withdrawal appends its ledger entry into the account's
  own state, so it commits as one participant; only a transfer spans two accounts. Completed ledger
  pages are flushed to append-only archive grains, so a write stays bounded for the life of the account.
- **Reads off the money path.** Balance reads are served from a non-transactional projection (the
  read model), updated after each commit — so the read-heavy majority of a bank's traffic (balance
  checks, statements) never touches the transaction coordinator and stays fast under any load.
- **A fixed reminder footprint.** Interest runs on a small, fixed pool of sweep coordinators rather
  than one reminder per account, so scheduled work doesn't grow with the customer base.

### 7.4 Scaling the database (writes)

Adding silos scales compute, but the first thing to actually saturate is the **database** — every
commit lands in one Postgres, and a single primary is the write ceiling (below it, the CPU sits idle).
Two traps to clear first: **read replicas don't help** — they're read-only, so every write still hits
the one primary — and **a single primary can't be write-scaled by adding nodes**. You scale writes in
two moves, cheapest first.

**1. Scale up and tune the one primary.** A bigger instance (cores, RAM, fast NVMe / high IOPS) plus
tuning — WAL and checkpoint settings, group commit, and **PgBouncer** connection pooling so many silos
share few connections — takes a single Postgres to *tens of thousands* of writes/sec. Durability stays
on (a bank can't trade `synchronous_commit`), so there's a ceiling, but it's high and most systems stop
here.

**2. Shard the grain storage across N independent Postgres.** Grain storage is a key-value store
addressed by grain id, with no cross-row joins on the write path — ideal for sharding. A custom
`IGrainStorage` provider routes each grain's state to one of *N* primaries by a **stable hash** of its
key, so aggregate write throughput is *N* × per-primary, without bound. How you shard it under Orleans
is what matters:

- **Keep each shard a plain Postgres; let Orleans be the only coordinator.** A transfer's two accounts
  may live on different primaries, but the 2PC across them is *Orleans'* job — each Postgres is just a
  local-transaction participant. One coordinator, full ACID, no sagas, still one cluster.
- **Don't put a distributed database (Citus, CockroachDB, …) under the transactional store.** Orleans
  already does cross-grain 2PC; a distributed engine would run its *own* 2PC across its workers
  underneath — two stacked coordinators on the money path — and Orleans' storage schema isn't built for
  that distribution. (Those engines are the right tool for the **reporting** store, §12 — analytical,
  cross-row queries with nothing stacked under them — not the write path.)
- **Use logical-shard indirection so growth is cheap.** Hash into many fixed logical buckets (e.g.
  1024) mapped onto the physical primaries; adding a primary remaps a few buckets instead of rehashing
  everything.
- **Replicate each shard for HA.** Sharding (throughput) and replication (durability/failover) are
  orthogonal — give every primary a synchronous standby (Patroni/streaming).

Result: write throughput grows with the number of primaries, transfers stay a single Orleans 2PC, and
it is all still **one cluster**. More silos (§7.2) + sharded storage (here) + the read model (§7.3) take
one cluster as far as you need — you never need a second cluster. *(Storage sharding is on the roadmap;
today the cluster uses a single Postgres.)*

### 7.5 Consistency: one account, one truth

The natural worry: with many silos on many machines, won't two copies of an account drift apart? They
can't — because within a cluster **an account is never copied.** Three guarantees enforce it:

- **One activation, cluster-wide.** Orleans keeps a distributed **grain directory** mapping each active
  account id to the *single* silo hosting it. Every request for `"alice"`, from any silo, routes to that
  one activation, which processes one message at a time. Two silos can't hold different copies — there
  is only ever one, so divergence is impossible *by construction*, not by syncing.
- **The database is the source of truth.** An activation is a cache: it loads committed state when it
  activates and persists on write. If a silo crashes, the account simply reactivates on another silo
  from the latest committed state — nothing is lost.
- **Membership prevents split-brain.** Silos agree, through the shared membership table, on exactly who
  is alive; a silo that loses contact is evicted and stops serving rather than risk a duplicate
  activation. That single membership view is the one piece that must stay centralized in a cluster.

A transfer between two accounts is a single **Orleans transaction** — 2PC over the two grains — so both
legs commit or both roll back, **even if their state lives on different storage shards** (§7.4). Strong,
classic ACID, automatically.

Two things are inherently serial — properties of an exact ledger, not flaws:

- **A single account is single-threaded.** That *is* the correctness guarantee. A central "hot" account
  everyone hits (a clearing/settlement account) is therefore a serialization point; you'd split it into
  **escrow stripes** — *N* sub-balance grains taking credits and debits in parallel, coordinating only
  when the balance nears zero (the one point where serialization is mathematically required).
- **One membership view per cluster** (above).

And the **read model** is eventually consistent — a balance read can be a few milliseconds stale — but
the *authoritative* balance on the account grain is always single and exact, and the overdraft check
always uses it. A stale read is not a desynced ledger.

### 7.6 Worked example: a 100-billion-request-a-day bank

Big-bank scale, worked end to end.

**The target.** 100B requests/day is **~1.16M req/s** on average (100,000,000,000 ÷ 86,400). Real
traffic peaks — payday, bill runs — so plan for a **5× daily peak ≈ 5.8M req/s**.

**The mix makes it tractable.** A retail bank is overwhelmingly reads (balance checks, statements, app
refreshes) versus payments. At a conservative **90 / 10**:

- reads: ~**5.2M req/s** at peak — served from the **read model**, non-transactional and in-memory, so
  the read-heavy 90% barely touches the expensive path;
- writes: ~**580k req/s** at peak — single-participant ACID transactions.

So 100B/day is not a "1.16M req/s" problem; it's a **~580k transactional-writes/s** problem — and that
is a sharding problem.

**Measured reference (this build).** Benchmarked on one **Ryzen 7 5700G (8c/16t), 32 GB**, running
*everything together* — Postgres + two silos + the load generator — through the load balancer:

| Workload | Throughput | Cost per op | |
|---|---|---|---|
| read-only | **~10,700 req/s** | ~0 DB commits/read | served entirely from the read model |
| 90 / 10 (realistic) | **~7,300 req/s** | ~0.4 commits/op | the read model carries the read half |
| 50 / 50 | **~3,100 req/s** | ~2 commits/op | |
| write-only | **~2,000 writes/s** | ~4 commits/write | one single-participant 2PC write |

Two facts drive the whole design: a **read costs ≈0 transactions** (read model), and a **write costs
~4 Postgres commits** (the two-phase commit). So the binding number is the **write rate**, set by the
database — ~2,000 writes/s on this one shared desktop, while the read half rides along nearly for free.

**Sizing the (single) cluster.** Writes are the binding constraint (~580k/s at peak). You meet it
inside **one cluster**, on two axes:

- **Silos for compute** — add silo pods (§7.2) until grain-call CPU is no longer the limit. The
  read-heavy 90% is served from the read model, so this scales cheaply.
- **Database shards for writes** — shard the grain storage (§7.4) across enough Postgres instances that
  their combined write rate clears 580k/s:
  - desktop-class Postgres (~2,000 writes/s each): 580,000 ÷ 2,000 ≈ **~290 shards**;
  - production-class Postgres (~10k–20k writes/s each): 580,000 ÷ 10k–20k ≈ **~30–60 shards**.

Run at ~60–70% utilization with HA replicas → on the order of **40–80 production database shards** (a
few hundred desktop-class ones) behind **one** cluster. These are real reference numbers; a production
database isn't sharing a CPU with the silos and the load generator, so its true per-shard capacity is
meaningfully higher. A transfer across shards is still a single Orleans 2PC — it remains one cluster.

**The rest of the footprint.**

- **Edge:** ~5.8M req/s of HTTP across a horizontally-scaled, **stateless** API / load-balancer tier
  (token verification is mostly cache hits). At this scale you separate the edge from the grain silos.
- **Storage:** grain storage sharded across the databases above, each with HA replicas — so a storage
  incident is contained to one shard of customers, not the whole bank.

**Where ActorBank is on this path.** Today it runs as one cluster on a single Postgres, and the pieces
that make a cluster efficient are already in place: account-as-actor with ACID transfers, the
co-located ledger (single-participant writes), the balance read model (cheap reads), the sharded
interest sweep, and post-quantum auth with a verification cache. Reaching 100B/day is **more of the
same cluster, not a redesign** — add silos, shard the grain storage (§7.4), escrow-stripe the clearing
accounts, and run the edge on Kubernetes (see the [Roadmap](#12-roadmap)).

---

## Frequently asked questions

### A user opens an account that's been dormant for two years — does it time out?

No — it just **wakes up on first touch**, with a one-time cost of a few milliseconds. A grain is
*virtual*: it always conceptually exists, so "dormant" means *not currently in memory*, **not deleted**
— the account's state has been sitting in PostgreSQL the whole time. The first request **lazily
reactivates** it: Orleans places the grain on a silo, the storage provider does **one primary-key read**
to load its state, `OnActivateAsync` runs (ActorBank does nothing heavy there), and the call is
answered. Every call after that hits RAM (sub-millisecond) until the account goes idle again (~2 h by
default) and quietly deactivates.

The age doesn't matter, and neither does table size — it's an **indexed point lookup**, the same speed
at 80M rows as at 1,000. Idle accounts cost only disk, which is exactly what lets the bank hold 80M
accounts while paying RAM for only the ~20M active at any instant. It would only be slow if the
**database itself were overloaded** (a capacity problem, not a dormancy one), or if something woke
*millions* of dormant accounts at once (a manageable burst of cheap reads, spread across the silos).

### Can I query all accounts directly in PostgreSQL?

You can list account **ids** (the grain key is a column), but not their **data**. Orleans grain storage
is a **key-value store** — balance and owner live inside a serialized payload, not queryable columns,
and once storage is sharded they're spread across many databases. For real queries (`WHERE balance > …`,
statements, analytics) you project account state into a **relational reporting store** fed by the event
stream — the OLTP/reporting split on the [Roadmap](#12-roadmap). You address grains by identity; you
don't query them.

### Why the actor model instead of a classic relational / MVC app?

It trades **easy global querying and operational simplicity** for **lock-free per-entity correctness
and write-scaling past one database**. It's the right call when the hard problem is high-contention,
exact, per-entity writes (a bank's money path); a relational/MVC app is simpler and better for
query-heavy, lower-contention workloads. At very large scale you use **both** — an actor core for the
writes and a relational/columnar plane for the queries (§7).

### What about a "hot" account that everyone hits at once?

A single account is single-threaded — that's the correctness guarantee, but it caps *that* account's
throughput. For a central **clearing/settlement** account you split it into **escrow stripes** (§7.5):
N sub-balance grains that take credits and debits in parallel, coordinating only when the balance nears
zero. Ordinary accounts keep the simple single-grain model.

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
│  ├─ Accounts/  IAccountGrain.cs, IAccountReadModelGrain.cs, IInterestSweepGrain.cs, InterestSharding.cs
│  ├─ Ledger/    ILedgerPageGrain.cs, LedgerPaging.cs
│  ├─ Auth/      ICredentialGrain.cs
│  ├─ Models/    AccountStatement, TransactionRecord, TransactionType, BalanceUpdate
│  └─ Exceptions/ BankException + domain errors
│
├─ ActorBank.Grains/              # the implementation (the actors)
│  ├─ Accounts/  AccountGrain, AccountState, AccountReadModelGrain (balance read model),
│  │             InterestSweepGrain (sharded interest sweep)
│  ├─ Ledger/    LedgerPageGrain, LedgerPageState
│  └─ Auth/      CredentialGrain, CredentialState, PasswordHasher
│
├─ ActorBank.Api/                 # the host (silo + web API, co-hosted)
│  ├─ Endpoints/       AccountEndpoints, AuthEndpoints   (route definitions)
│  ├─ Auth/            PqcTokenService, PqcBearerHandler, AccountOwnershipFilter
│  ├─ Contracts/       request/response DTOs
│  ├─ Infrastructure/  OrleansConfiguration, BankExceptionHandler, InterestOptions
│  ├─ Serialization/   ApiJsonSerializerContext (source-gen JSON)
│  └─ Program.cs       (thin composition root)
│
├─ ActorBank.AppHost/             # .NET Aspire orchestrator (local dev): provisions Postgres,
│                                 # runs the silo, and serves the dashboard — `dotnet run` here
└─ ActorBank.ServiceDefaults/     # Aspire shared defaults: OpenTelemetry, health checks, resilience
```

---

## 9. Run it

**With .NET Aspire** (recommended for local dev — one command brings up Postgres + the silo, with a
live dashboard for logs, traces, and metrics):

```bash
dotnet run --project ActorBank.AppHost     # opens the Aspire dashboard; needs Docker for Postgres
```

The AppHost provisions a PostgreSQL container (initialised with the Orleans schema from `db/init`),
injects its connection string into the API, and runs the co-hosted silo. Add `.WithReplicas(N)` in
`AppHost.cs` to run several silos in one cluster. The dashboard URL is printed on startup; OpenTelemetry
traces let you follow a transfer across grains.

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
unchanged — proving money is conserved and that opposing transfers stay deadlock-free (§4). See
[`tests/README.md`](tests/README.md).

---

## 11. Configuration

Connection string in `ActorBank.Api/appsettings.json` → `ConnectionStrings:Orleans`
(defaults to `Host=localhost;Port=5432;Database=orleans;Username=orleans;Password=orleans`).
Interest schedule is bound from the `Interest` section (`PeriodMinutes`, `RatePercentPerPeriod`,
`SweepShards`).

---

## 12. Roadmap

**Built**

- **Balance read model** — `GetBalance` served from a non-transactional, post-commit projection (§7.3).
- **Co-located ledger** — the current page lives in the account grain, so writes are single-participant (§7.3).
- **Sharded interest sweep**, post-quantum (ML-DSA-65) auth, ADO.NET clustering, and a Docker stack.
- **.NET Aspire dev orchestration** — `AppHost` provisions Postgres + runs the silo with OpenTelemetry and a live dashboard.

**Next**

1. **Sharded grain storage** — route grain state to N databases within the cluster; transfers stay ACID (§7.4).
2. **Escrow-striped hot accounts** — parallel credits and debits for a central clearing account (§7.5).
3. **Queryable read model (reporting database)** — Orleans grain storage is a key-value store, so you
   address grains by identity, not query them. For bank-wide queries (`SELECT … WHERE balance > …`,
   statements, analytics), project account state into a **relational reporting store** fed by the
   event stream — the standard OLTP/reporting split at bank scale, keeping reporting load off the
   transactional cluster. (The `AccountReadModelGrain` already publishes post-commit; this points those
   updates at queryable tables.)
4. **Kubernetes** — `Microsoft.Orleans.Clustering.Kubernetes`, with the silo advertising its pod IP.
5. **Token revocation + refresh** and **rate limiting** on `/auth`.

---

## 13. Requirements

- .NET 10 SDK
- Docker (for PostgreSQL)
- Orleans 10.2.1 (restored from NuGet)
