# 0004. Version the schema as SQL, not as an EF Core model

Status: accepted

## Context

The schema was a single script mounted into the Postgres entrypoint, which runs
only on an empty data directory. Adding `attempts` to the outbox proved what that
costs: the running database kept its old shape, every dispatch tick failed with
`42703: column "attempts" does not exist`, the health check went on reporting the
instance healthy, and the only recovery was destroying the volume.

Every module still to come — the ledger, webhook deduplication, provider records —
changes the schema again. So the schema needs versioning before anything else is
built on it.

The obvious candidate is EF Core migrations. The stack already implies it: the
role asks for "Dapper or Entity Framework", and this project uses Dapper.

## Decision

Numbered SQL files, applied in order by a small runner that records what it has
applied in a `schema_migrations` table.

The runner takes a PostgreSQL advisory lock before it starts, so several
instances beginning at once cannot apply the same migration twice. Each file runs
in its own transaction and is recorded in that same transaction, which
PostgreSQL's transactional DDL makes possible: a migration that fails halfway
leaves nothing behind.

## Consequences

* The schema stays the source of truth. Constraints, partial indexes and the
  reasoning in the comments live in one place and say exactly what the database
  will do, rather than in a model that has to be read to work out what SQL it
  will generate.
* Anything PostgreSQL can express is available: `FOR UPDATE SKIP LOCKED`,
  partial indexes, `CHECK` with a regular expression, advisory locks. None of
  these need a provider to support them first.
* Nothing generates the migrations, so each one is written by hand. That is more
  typing and one more chance to make a mistake, paid back by there being no
  question about what will run.
* No detection of drift between code and schema. Dapper already has none, so this
  adds no exposure that was not there.
* The README stops claiming EF Core, which was never referenced by any code.

## Alternatives rejected

* **EF Core migrations.** Generated, checked against a model, and the familiar
  answer. Rejected because the model would become a second description of a
  schema that is already the contract this system is built around: the `CHECK`
  on `payments.status` is the reason statuses are mapped by hand, and the
  named unique constraint is matched by name in the create path. Adding a model
  that also describes those means two things to keep in step, and the generated
  SQL still has to be read before every deployment.
* **A migration library such as DbUp or FluentMigrator.** Roughly what the runner
  here does. Rejected because that runner is about sixty lines including the
  advisory lock, and a dependency whose behaviour has to be explained anyway is
  worth less than sixty lines that can be read.
* **Leaving the entrypoint script.** Only works on an empty database, which makes
  every schema change a data loss event.
