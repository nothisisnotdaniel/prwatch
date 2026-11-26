# 006 Consent, Merge and Cancel requests

As the user, I want to tap Consent, Merge or Cancel for a PR and have PRWatch hand that to the factory safely, so that untrusted code is only tested or merged when I say so, and only for the head I saw.

Category: security

## What

Fixed POST routes `/api/prs/{project}/{number}/review|merge|cancel` with the `headSha` the user saw (40 hex) and, for review, `consent`. Validation: known project, digits-only number, the PR in the last poll, the sha equal to its current head. Writes `factory/prs/<repo>-<n>/request.json` (`{action, headSha, consent}`, only those keys, at most 4096 bytes) atomically, refusing symlinks and any path outside the prs folder. A stale or invalid request gives a fixed error text. The watcher then treats a review request as a pick.

## Why

The factory decides trust and merges inside `job.py pr`; PRWatch only records the user's choice, bound to one head so a later push can't ride on it.
