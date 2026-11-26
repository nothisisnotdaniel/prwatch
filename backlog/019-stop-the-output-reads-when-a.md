# 019 Stop the output reads when a run is killed

As the user, I want a killed run to end completely, so that leftover child processes cannot hang PRWatch.

Category: security

## What

Killing a run does not stop the output reads, so leftover child processes holding the pipes can hang them. Cancel the reads or close the pipes in End. This changes the kill and liveness paths and is high risk.

## Why

Raised by review-security 1 of job 423. A hung run can block the watcher for good.

## Notes

Proposed by job 423-headless-claude-runner.4541c8db4c7748da877aaebbf92c84dd (`factory/jobs/423-headless-claude-runner.4541c8db4c7748da877aaebbf92c84dd/`).
