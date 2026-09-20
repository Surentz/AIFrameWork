---
max_turns: 10
allowed_tools: [Read, Glob, Grep, Skill]
---

I've just added a new job handler to this repo — `ArchiveStaleOrdersHandler`, in
`src/Application/Orders/`, handling an `ArchiveStaleOrders` job on the Heavy lane. It builds
fine and `dotnet test` is green.

What do I need to do before I commit this?
