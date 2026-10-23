# IdempotentPayments

A payment API where a retried request must never charge twice.
Project 08 of the .NET ladder (see `~/dotnet/PROJECTS.md`).

Picks up the gap documented in Project 06 (`JobRunner/gaps.md`): handlers that are not
idempotent.

## Status

Just started. No code yet; milestones will be planned before the first line is written.

## Requirements

- `POST /api/payments` with a required `Idempotency-Key` header
- Same key + same body → returns the **original** response, does not re-charge
- Same key + **different** body → `422`
- Concurrent requests with the same key → one processes, the other waits or gets `409`
- Payments are `pending` / `succeeded` / `failed`
- A fake provider that can be made slow, flaky, or failing
- Keys expire after 24h
- A test firing 20 simultaneous requests with one key, asserting exactly one charge

Optional: idempotent refunds, webhook callbacks on completion, partial capture.

## Acceptance criteria

- [ ] 20 concurrent identical requests → exactly 1 provider call; every response is either the original `201` or `409 Conflict`; no `5xx`
- [ ] Sequential retry after success → cached response, no second charge
- [ ] Same key with a changed amount → `422`
- [ ] Crash mid-payment, restart, retry with the same key → resolves without double charge
- [ ] Missing `Idempotency-Key` → `400`
- [ ] Provider timeout doesn't leave a row stuck in `pending` forever

## Concepts this project is about

Idempotency keys · request fingerprinting · storing and replaying responses ·
TOCTOU (why check-then-act loses and a unique index wins) · concurrent requests on the
same key · why exactly-once delivery over a network is impossible, and what to do instead.

## Stack

ASP.NET Core, PostgreSQL, Dapper or EF Core, xUnit, Docker Compose for the database.
No Hangfire, MassTransit or Quartz.
