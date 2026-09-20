---
max_turns: 10
allowed_tools: [Read, Glob, Grep, Skill]
---

I added an optional `giftMessage` field to `PlaceOrderRequest` in `src/Api/Orders/`, and a
matching `[ProducesResponseType]` change on the action.

Give me the exact commands to bring the committed API contract back in sync. I have no database
running locally right now.
