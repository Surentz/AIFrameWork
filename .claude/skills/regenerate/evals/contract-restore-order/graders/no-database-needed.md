---
type: llm
weight: 1
---

The user said they have no database running.

The response must not tell them to start PostgreSQL, run `docker compose up`, or apply
migrations in order to regenerate the contract. No database is needed: the connection string is
never actually opened, it only has to be non-empty to get past the startup guard, and
`Wolverine__Durable=false` is what stops Wolverine's startup migration dialling Postgres.

PASS if the response proceeds without requiring a database. Stating explicitly that the
connection string is a placeholder that is never opened is a bonus.

FAIL if it tells the user they must start a database first.
