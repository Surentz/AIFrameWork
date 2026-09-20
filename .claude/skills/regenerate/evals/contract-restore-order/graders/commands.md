---
type: llm
weight: 1
---

The response must give all three steps of the contract regeneration, in this order:

1. `dotnet restore src/Api`
2. `dotnet msbuild src/Api -t:"Build;GenerateOpenApiDocuments"` with BOTH environment variables
   set — a `ConnectionStrings__Default` value and `Wolverine__Durable=false`
3. `npm run generate:api --prefix frontend`

Missing any of the three, or omitting either environment variable, is a fail.
