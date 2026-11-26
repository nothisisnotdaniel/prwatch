# 020 Close the time-of-check/time-of-use gap when starting claude

As the user, I want the checked claude binary to be the one that runs, so that swapping the file between check and start cannot run something else.

Category: security

## What

There is a short window between resolving the claude path and starting it. Design an fd-based start so the checked file is the one executed.

## Why

Raised by review-security 2 of job 423. The file could be replaced in that window; the risk is low but real.

## Notes

Proposed by job 423-headless-claude-runner.4541c8db4c7748da877aaebbf92c84dd (`factory/jobs/423-headless-claude-runner.4541c8db4c7748da877aaebbf92c84dd/`).
