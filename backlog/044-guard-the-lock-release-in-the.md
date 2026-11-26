# 044 Guard the lock release in the runner's abandon path

As the user, I want a failed run start to finish its clean-up even if releasing the update lock throws, so that a start failure never leaves PRWatch in a half-cleaned state.

Category: security

## What

In `ClaudeReviewRunner` (Abandon), `updateLock.Release()` and `Holder()` run unguarded. Both catch their own IO and permission errors today and the process is already killed before them. Wrap them so a future throwing path in either can't skip the rest of the clean-up or the `run_failed` record.

## Why

Raised by review-security 2 of job 442. Not reachable today, but a new throw in the lock code would cut the abandon path short.

## Notes

Proposed by job 442-kill-on-status-fail.bfff1b19358e4e3a87b1f0780f6e3808.

Proposed by job 442-kill-on-status-fail.bfff1b19358e4e3a87b1f0780f6e3808 (`factory/jobs/442-kill-on-status-fail.bfff1b19358e4e3a87b1f0780f6e3808/`).
