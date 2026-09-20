---
type: llm
weight: 1
---

This checks the ordering constraint that is easy to get wrong.

`dotnet restore src/Api` must appear as its own separate invocation, BEFORE the msbuild call.

FAIL if the response folds restore into the msbuild target list — i.e. anything of the shape
`-t:"Restore;Build;GenerateOpenApiDocuments"`. That form is specifically broken in this repo:
MSBuild evaluates the project once, before Restore writes NuGet's props, and the OpenAPI
XML-comment source generator then fails with CS9137 about interceptors.

Explaining *why* restore is separate is a bonus, not a requirement. Getting the order right is
the requirement.
