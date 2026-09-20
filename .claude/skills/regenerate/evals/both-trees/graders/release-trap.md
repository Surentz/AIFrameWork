---
type: llm
weight: 1
---

The response must convey that a green Debug build and a passing test run are **not** evidence
here — that stale generated adapters leave Debug working (because Debug compiles adapters at
startup) and break only in Release, at startup.

Credit an answer that makes this point in its own words. It does not need to use the word
"Release" if it clearly conveys that the current green build does not rule the problem out.

A response that treats "it builds and tests pass" as reassurance fails.
