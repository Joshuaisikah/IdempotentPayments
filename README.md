# IdempotentPayments

A payment API where a retried request must never charge twice.
Project 08 of the .NET ladder (see `~/dotnet/PROJECTS.md`).

Picks up the gap documented in Project 06 (`JobRunner/gaps.md`): handlers that are not
idempotent.

## Status

Milestones M0–M5 are done: 117 tests pass against real PostgreSQL. What's left is the
oral check.

| Milestone | What it added |
|---|---|
| M0 | Housekeeping: one repo, Docker Compose, minimal API |
| M1 | Payments and a fake provider, with no idempotency yet |
| M2 | `Idempotency-Key`: `400` when missing, replay of the stored response, `422` on a changed body |
| M3 | 20 concurrent requests → 1 charge (a unique index plus write-ahead claims) |
| M4 | Crash or timeout mid-payment never leaves a payment stuck: stale-claim takeover, provider timeouts, a background sweep |
| M5 | Keys expire 24h after first use (`TimeProvider`, reclaiming expired keys, sweep deletes old keys) |

## Running it

```
docker compose up -d          # PostgreSQL 17 on localhost:5433
./project-tools.sh migrate    # apply EF Core migrations
./project-tools.sh run        # API; Scalar UI at /scalar in Development
./project-tools.sh test       # all tests (needs the database running)
```

The tests use a separate database, `payment_test`, on the same server. They migrate it
themselves.

## API

`POST /api/payments` with an `Idempotency-Key` header (1–255 characters) and a body of
`{ "amount": 10.00, "currency": "USD" }`.
`GET /api/payments/{id}` returns one payment.

| Situation | Response |
|---|---|
| New key | `201` + payment + `Location` |
| Same key, same body, within 24h | the **stored** response (same status, body and `Location`), no charge |
| Same key, different body, within 24h | `422` |
| Same key while the first request is still running | `409` + `Retry-After: 5` |
| Provider did not answer within `ProviderTimeout` | `503` + `Retry-After: 5` |
| Missing, blank or over-long key; invalid body | `400` (not stored, so the same key can be fixed and retried) |
| Same key more than 24h after its first use | treated as a **new** payment (see below) |

"Same body" means the same fingerprint (amount and currency, normalized), so `10` and
`10.00`, or `usd` and `USD`, count as the same request.

## Advice for client developers

**Retry a payment whose outcome you don't know within 24 hours, using the same key.**
Within that window, a retry can never charge twice: you get the original answer.

After 24 hours the server has forgotten the key. Any request then is a new payment,
**whatever key you use**. If the first attempt succeeded but its response was lost, a
retry after 24h charges the customer again. A fresh key does not help, because a new key
*is* a new payment.

A client that lost the response also has no payment id, and the API offers no lookup by
key, so after 24h it cannot find out what happened by itself. It has to go to support.
That is a known gap (below).

## How it works

- **Claim before charging (write-ahead).** The first request inserts the key row,
  carrying a new payment id, *before* calling the provider. The primary key on
  `IdempotencyKeys.Key` is what makes two simultaneous first requests impossible. Doing
  a `SELECT` and then an `INSERT` would leave a gap between the check and the write
  (TOCTOU). The insert fails for the loser, who then reads the winner's row.
- **The payment id is the provider reference.** A retry that finds a claim with no
  stored response can ask the provider about that exact id. It never charges blind.
- **Stale-claim takeover.** A claim older than `StaleAfter` with no response belongs to a
  request that died. One retry takes it over with a conditional `UPDATE … WHERE ClaimedAt
  = <the value I read>`. Under PostgreSQL's READ COMMITTED isolation, a second
  simultaneous `UPDATE` waits for the first, then re-checks its `WHERE` against the new
  row, matches nothing, and loses.
- **Every provider call has a time limit** (`ProviderTimeout`). `StaleAfter` must be
  more than 2 × `ProviderTimeout` + 10s, so a live request is never mistaken for a dead
  one. The app refuses to start otherwise.
- **The sweep** is a background service that runs every 30s. It settles payments stuck in
  `Pending`/`Processing` (asking the provider when needed) even if no client ever
  retries, and it deletes expired keys.
- **Expiry** is measured from `CreatedAt` (first use). It is not measured from
  `ClaimedAt`, because takeovers reset that. The expiry check runs *before* the
  fingerprint check; otherwise an expired key would still return `422` or replay the old
  response. An expired key is reclaimed with `UPDATE … WHERE CreatedAt = <the value I
  read>`, which resets every column. The single winner then takes the normal new-payment
  path. A loser reads the row again and resolves against it: replay, `422` or `409`.
- **Why the reclaim checks `CreatedAt` and not just `ResponseBody != null`.** Between
  B's read and B's update, C can reclaim the key, charge and complete it. The row has a
  `ResponseBody` again, so a check on that column cannot see the change (the ABA
  problem). `CreatedAt` is rewritten on every reclaim and never returns to a value
  anyone has read before. A test forces this interleaving
  (`KeyExpiryTests.Reclaim_that_lost_to_a_completed_reclaim_does_not_charge_again`).
- **All time comes from `TimeProvider`**, so tests move the clock 24h with
  `FakeTimeProvider` instead of waiting.

## Configuration

| Setting | Default | Rule |
|---|---|---|
| `Idempotency:ProviderTimeoutSeconds` | 30 | |
| `Idempotency:StaleAfterSeconds` | 90 | must be more than 2 × `ProviderTimeout` + 10s |
| `Idempotency:KeyLifetimeHours` | 24 | must be more than `StaleAfter` |

## Acceptance criteria

- [x] 20 concurrent identical requests → exactly 1 provider call; every response is
  either the original `201` or `409 Conflict`; no `5xx`
  (`ConcurrentIdempotencyTests.Acceptance_20_concurrent_identical_requests_get_the_original_201_or_409`)
- [x] Sequential retry after success → cached response, no second charge
  (`IdempotencyEndpointTests.Replay_returns_identical_status_body_and_Location_and_charges_once`)
- [x] Same key with a changed amount → `422`
  (`IdempotencyEndpointTests.Same_key_with_different_request_is_422_and_does_not_charge_again`)
- [x] Crash mid-payment, restart, retry with the same key → resolves without double charge
  (`TakeoverTests`, e.g. `Timeout_after_charge_then_stale_retry_recovers_without_a_second_charge`)
- [x] Missing `Idempotency-Key` → `400`
  (`IdempotencyEndpointTests.Missing_key_is_400_and_does_not_charge`)
- [x] Provider timeout doesn't leave a row stuck in `pending` forever
  (`SweepTests`, `ProviderTimeoutTests`, `EndpointTimeoutTests`)
- [x] Keys expire after 24h (`KeyExpiryTests`)

## Known gaps

- **No lookup by key.** A client that lost the response has no payment id, and after 24h
  it cannot find out what happened (see "Advice for client developers").
- **Sweep delete throughput.** Each pass deletes at most 100 expired keys, and passes run
  every 30s. That is about 288,000 keys a day; heavier traffic would let the backlog grow
  without limit. There is also no index on `CreatedAt`, so each pass scans the whole key
  table. A partial index on finished keys would fix the scan.
- **Expired keys that never finished are never expired.** Keys with no stored response,
  including old claims with no payment id, are skipped by both the reclaim and the
  delete. Those old no-id claims answer `409` forever.
- **A sweep delete can make a free key answer `409`.** If the sweep deletes a key between
  a request's read and its reclaim, the request finds nothing and returns `409`. Its
  retry then succeeds.
- **Timestamps used as version checks.** `ClaimedAt` and `CreatedAt` double as
  "has this row changed?" checks. That works because each write stores a value nobody has
  read before. A dedicated version column (an optimistic concurrency token, or
  PostgreSQL's `xmin`) would guarantee it without relying on the clock.
- **The loser's re-resolve recursion** is bounded only because `KeyLifetime` must be more
  than `StaleAfter`. That rule is enforced in `IdempotencyOptions`, not where the
  recursion happens.
- `Retry-After` is a fixed 5 seconds.

## Concepts this project is about

Idempotency keys · request fingerprinting · storing and replaying responses ·
TOCTOU (why check-then-act loses and a unique index wins) · write-ahead claims ·
conditional `UPDATE` as compare-and-swap · READ COMMITTED re-checking a `WHERE` · the
ABA problem · why exactly-once delivery over a network is impossible, and what to do
instead (at-least-once delivery plus idempotent handling).

## Stack

ASP.NET Core 10 minimal APIs, PostgreSQL 17, EF Core (Npgsql), xUnit, FluentAssertions,
`Microsoft.Extensions.TimeProvider.Testing`, Docker Compose for the database.
No Hangfire, MassTransit or Quartz.
