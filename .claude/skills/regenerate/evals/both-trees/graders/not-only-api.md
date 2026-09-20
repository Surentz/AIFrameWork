---
type: llm
weight: 1
---

This checks a specific wrong answer.

There are two independent generated trees in this repo: `src/Api/Internal/Generated` and
`src/Worker/Internal/Generated`. The change described is a **job** handler, so the worker's tree
is the one that must be regenerated.

FAIL if the response names only `dotnet run --project src/Api -- codegen write` and never the
worker equivalent — that is the exact mistake this case exists to catch.

PASS if it names the worker command. Naming both commands is also a pass: regenerating both is
explicitly the recommended move when unsure, and the extra command is a harmless no-op.
