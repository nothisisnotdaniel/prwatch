# 018 Kill the claude process when the status update fails after start

As the user, I want a run to stop if PRWatch cannot record it, so that no claude process keeps running unseen and spending tokens.

Category: bug

## What

If status.Update throws after Start, the claude process is left running. Guard that call so the process is stopped on failure. This adds a kill path, so it needs its own review.

## Why

Raised by review-code 4 of job 423. An unseen run can spend tokens with nothing tracking it.

## Notes

Proposed by job 423-headless-claude-runner.4541c8db4c7748da877aaebbf92c84dd (`factory/jobs/423-headless-claude-runner.4541c8db4c7748da877aaebbf92c84dd/`).
