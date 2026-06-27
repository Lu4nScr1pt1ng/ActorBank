# ActorBank

A small but production-shaped bank built on the **virtual actor model** with
[Microsoft Orleans](https://learn.microsoft.com/dotnet/orleans/) `10.2.1` on **.NET 10**.

Each bank account is an Orleans **grain** (a virtual actor). Orleans processes one request at a
time per grain, so balance updates are serialized by design — **no locks, no race conditions** —
and money transfers between two accounts are **ACID** via Orleans transactions.

## What's inside

- **Virtual actors** — one `AccountGrain` per account.
- **ACID transfers** — `[Transaction]` + `ITransactionalState<T>`: a transfer debits one account and
  credits another atomically. If the credit fails, the debit rolls back automatically.
- **Durable storage** — Orleans **ADO.NET** grain storage on **PostgreSQL**. State survives restarts.
- **Web API** — ASP.NET Core minimal API co-hosting the Orleans silo; clean RFC 7807 error responses.
- **Interactive docs** — OpenAPI document + **Scalar** UI at `/scalar/v1`.
- **Quantum-resistant auth** — bearer tokens signed with **ML-DSA-65** (NIST FIPS 204); per-account authorization.
- **Scale-out clustering** — ADO.NET membership in PostgreSQL; many silos form one cluster.
- **Scheduled interest** — durable Orleans reminders credit interest on a recurring tick.
- **Runs in Docker** — one `docker compose up` brings up Postgres + the API.
- **.NET 10 performance** — source-generated JSON & logging, `TypedResults`, Server GC + Tiered PGO.

## Project layout

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
├─ ActorBank.Abstractions/        # the contract
│  ├─ Accounts/  IAccountGrain.cs, IAccountScheduleGrain.cs
│  ├─ Ledger/    ILedgerPageGrain.cs, LedgerPaging.cs
│  ├─ Auth/      ICredentialGrain.cs
│  ├─ Models/    AccountStatement, TransactionRecord, TransactionType
│  └─ Exceptions/ BankException + domain errors
│
├─ ActorBank.Grains/              # the implementation
│  ├─ Accounts/  AccountGrain, AccountState, AccountScheduleGrain (interest reminder)
│  ├─ Ledger/    LedgerPageGrain, LedgerPageState
│  └─ Auth/      CredentialGrain, CredentialState, PasswordHasher
│
└─ ActorBank.Api/                 # the host
   ├─ Endpoints/       AccountEndpoints, AuthEndpoints   (route definitions)
   ├─ Auth/            PqcTokenService, PqcBearerHandler, AccountOwnershipFilter
   ├─ Contracts/       request/response DTOs
   ├─ Infrastructure/  OrleansConfiguration, BankExceptionHandler
   ├─ Serialization/   ApiJsonSerializerContext (source-gen JSON)
   └─ Program.cs       (thin composition root)
```

## Run it

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
> PQC auth would fail to start there.

## Performance & conventions (.NET 10)

- **Source-generated JSON** (`ApiJsonSerializerContext`) — no per-request reflection; enums render
  as strings. Registered via `ConfigureHttpJsonOptions`.
- **Source-generated logging** (`[LoggerMessage]`) — allocation-free, no boxing on the hot path.
- **`TypedResults`** in endpoints — concrete result types, better perf and OpenAPI metadata.
- **Server GC + Concurrent GC + Tiered PGO** enabled on the host for throughput.
- **Central Package Management** + **`Directory.Build.props`** — versions and shared settings in one
  place; `AnalysisLevel=latest-recommended` keeps the code honest (builds warning-clean).

## The ledger: keeping the hot path O(1)

A naïve account would keep its whole transaction history in grain state — but that means every
deposit/withdraw/transfer re-serializes a list that grows forever (O(n) per write, O(n²) over time),
right inside the transaction's two-phase commit. To avoid that:

- **`AccountGrain` transactional state is tiny and constant-size**: `{ IsOpen, Owner, Balance, LedgerCount }`.
  The ACID money path never touches history.
- **History is append-only and sharded** across `LedgerPageGrain`s, one per 128-entry page
  (key `"{accountId}/{page}"`). An append rewrites only the current page — **bounded cost** regardless
  of how long the account has been active.
- Ledger appends run **inside the same Orleans transaction** as the balance change, so a rolled-back
  transfer leaves **no phantom ledger entry**, and a committed one is always recorded.
- **`GetStatement(maxTransactions)`** reads only the page(s) holding the most recent window (default 50),
  so the common read is also bounded. Pass `?take=N` for more.

### Trade-off: consistency vs. transaction participants

Keeping ledger appends inside the balance transaction means each write now enlists more
participants in the two-phase commit: a deposit/withdraw enlists the account **and** its current
ledger page; a transfer touches up to four grains (both accounts + both current pages). That is the
deliberate price of a **strongly consistent audit log** — a rolled-back transfer can never leave a
phantom ledger entry, and a committed one is always recorded. For any account with real history this
still wins decisively over the old design, which re-serialized the entire history (O(n)) on every
single write. If you ever needed to shave participants, the lever would be an *eventually consistent*
(post-commit) ledger — but that trades away audit atomicity, which a bank should not do.

## API

**Auth** (anonymous):

| Method & path | Body | Result |
|---------------|------|--------|
| `POST /auth/register` | `{ "username", "password", "accountId" }` | 201 |
| `POST /auth/token` | `{ "username", "password" }` | 200 + `{ accessToken, ... }` / 401 |
| `GET  /auth/jwks` | — | 200 + `{ algorithm, keyId, publicKey }` |

**Accounts** (require `Authorization: Bearer <token>`; the token's subject must equal `{id}`):

| Method & path | Body | Result |
|---------------|------|--------|
| `POST /accounts/{id}/open` | `{ "owner": "Alice", "openingDeposit": 1000 }` | 201 + statement |
| `POST /accounts/{id}/deposit` | `{ "amount": 250, "description": "Paycheck" }` | 200 + balance |
| `POST /accounts/{id}/withdraw` | `{ "amount": 75 }` | 200 + balance / 409 |
| `POST /accounts/{id}/transfer` | `{ "toAccountId": "bob-002", "amount": 300 }` | 204 / 404 / 409 |
| `GET  /accounts/{id}/balance` | — | 200 + balance |
| `GET  /accounts/{id}/statement` | `?take=N` | 200 + recent transactions |

Errors: `401` no/invalid token or bad credentials, `403` token doesn't own the account,
`404` account not open, `409` insufficient funds / invalid operation, `400` invalid amount.

### Example

```bash
B=http://localhost:5080
curl -s -XPOST $B/auth/register -H 'content-type: application/json' \
  -d '{"username":"alice","password":"correct-horse","accountId":"alice-001"}'
TOKEN=$(curl -s -XPOST $B/auth/token -H 'content-type: application/json' \
  -d '{"username":"alice","password":"correct-horse"}' | jq -r .accessToken)

curl -s -XPOST $B/accounts/alice-001/open -H "authorization: Bearer $TOKEN" \
  -H 'content-type: application/json' -d '{"owner":"Alice","openingDeposit":1000}'
curl -s $B/accounts/alice-001/statement -H "authorization: Bearer $TOKEN"
```

A transfer to a non-existent / unopened account returns `404` **and the debit is rolled back** —
the transaction guarantees both legs commit together or not at all.

## Quantum-resistant authentication

Classic JWT signatures (RSA/ECDSA) are broken by a quantum computer running Shor's algorithm.
ActorBank instead signs tokens with **ML-DSA-65** (NIST FIPS 204, a.k.a. CRYSTALS-Dilithium) via
.NET 10's `System.Security.Cryptography.MLDsa` — a post-quantum signature scheme.

- **Token format** — a compact JWS (`header.payload.signature`) with `alg: "ML-DSA-65"`. Looks like a
  JWT, but the ~3.3 KB signature is quantum-resistant.
- **`PqcTokenService`** — loads/persists the ML-DSA private key (PKCS#8) so tokens survive restarts;
  signs on `/auth/token`, verifies in the `PqcBearer` auth handler (checks signature + `exp`/`nbf`/
  `iss`/`aud`). All crypto runs under a single lock — correct and race-free.
- **Credentials** — a `CredentialGrain` per username stores a salted **PBKDF2-SHA256** (210k iter)
  hash bound to an account id. (Hash-based KDFs stay safe under quantum given good parameters.)
- **Authorization** — `/accounts` requires a valid token *and* an ownership filter ensures the
  token's `sub` equals the account id. The account you control **is** your username (server-enforced
  at registration), so nobody can register a credential that claims someone else's account.

**What this does and doesn't protect.** A request with no token, a tampered token, or a token for a
*different* account is rejected (401 / 401 / 403 — all tested). What it deliberately does **not** do:

- The grains trust the cluster — auth lives only at the API edge. The Orleans silo ports (11111/30000)
  must be **network-isolated**; anyone who can reach a silo directly can call grains unauthenticated.
- **No token revocation or refresh** — a token is valid until `exp` (60 min). A leaked token works
  until then. A production system would add a revocation list (e.g. a `jti` grain) + refresh tokens.
- **No rate limiting / lockout** on `/auth/token` — PBKDF2 slows each guess but nothing blocks a
  brute-force campaign. Add ASP.NET Core rate limiting before exposing this publicly.

> Note: `MLDsa` ships as an **experimental** API in .NET 10 (`SYSLIB5006`, suppressed in the API
> project) and needs OpenSSL 3.5+ on Linux. `GET /auth/jwks` publishes the public key for verifiers.

## How it works

- **Transactional state.** `AccountGrain` holds its state in `ITransactionalState<AccountState>`
  bound to the `"accountStore"` provider; every method is `[Transaction(CreateOrJoin)]`.
- **Transfers** are composed into one transaction **by the API** via `ITransactionClient`, which
  calls the two legs (`DebitForTransfer` + `AcceptTransfer`) in a fixed id order. Account grains
  never call each other — this avoids a turn-based deadlock between opposing transfers (A→B vs B→A)
  that a load test caught; see [`tests/README.md`](tests/README.md). If either leg fails, the whole
  transaction rolls back — no manual compensation.
- **Storage.** `AddAdoNetGrainStorage("accountStore", Invariant="Npgsql")` persists state to the
  `OrleansStorage` table. `silo.UseTransactions()` enables the transaction subsystem.
- **Schema.** Orleans ships SQL per database; `./db/init` runs (on first boot) the official
  `PostgreSQL-Main`, `-Persistence`, `-Clustering`, `-Reminders` scripts plus the `Clustering-3.7.0`
  migration (which adds the `CleanupDefunctSiloEntries` query the 10.2.1 runtime requires).
- **Clustering.** `UseAdoNetClustering` + a `ClusterId`/`ServiceId` — silos discover each other through
  the membership tables, so the same image scales to many nodes against one database.
- **Reminders.** Each account's interest runs on a durable reminder owned by a separate
  `AccountScheduleGrain` (a grain calling *itself* would deadlock), which ticks and calls
  `AccountGrain.ApplyInterest` — a transactional credit that also writes a ledger entry.

## Configuration

Connection string in `ActorBank.Api/appsettings.json` → `ConnectionStrings:Orleans`
(defaults to `Host=localhost;Port=5432;Database=orleans;Username=orleans;Password=orleans`).

## Testing

[k6](https://k6.io) load/consistency/performance tests live in [`tests/`](tests/README.md) and run
against the live API via Docker:

```bash
./tests/run.sh smoke.js         # functional + auth + ACID rollback
./tests/run.sh consistency.js   # asserts money is conserved under concurrent transfers (ACID)
./tests/run.sh load.js          # throughput / latency thresholds
```

The consistency test is the headline: many concurrent transfers, then assert the total balance is
unchanged. It's how the transfer deadlock above was found and the fix verified.

## Done

- ~~**ADO.NET clustering**~~ — `UseAdoNetClustering`; silos form one cluster via PostgreSQL membership.
- ~~**Containerize**~~ — `Dockerfile` (Alpine 3.23) + the API in `docker-compose`.
- ~~**Reminders**~~ — scheduled interest via a durable reminder on `AccountScheduleGrain`.
- ~~**AuthN/Z**~~ — post-quantum (ML-DSA-65) bearer tokens + per-account authorization.

## Next steps (roadmap)

1. **Token revocation + refresh** and **rate limiting** on `/auth` (see the auth caveats above).
2. **Kubernetes** — `Microsoft.Orleans.Clustering.Kubernetes`, with the silo advertising its pod IP.
3. **Multi-account customers** — a customer identity that can own several accounts, instead of the
   current 1 user = 1 account model.
4. **Queryable read model** — project the opaque grain state into relational tables for reporting.

## Requirements

- .NET 10 SDK
- Docker (for PostgreSQL)
- Orleans 10.2.1 (restored from NuGet)
