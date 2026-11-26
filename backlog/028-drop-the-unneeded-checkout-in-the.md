# 028 Drop the unneeded checkout in the release job

Category: security

## What

The `release` job in .github/workflows/release.yml checks out the repo although it builds nothing; it is kept only for parity with ControlRoom. Remove it (and do the same in ControlRoom if it fits) so the write-scoped job touches as little as possible.

## Why

Review-security 2 of job 427: fewer steps in the only job that holds a write token means less that can go wrong there; harmless today with persist-credentials: false.

## Notes

Proposed by job 427-release-workflow.0cba18d4252349ddbd7fa28e2394eb26 (`factory/jobs/427-release-workflow.0cba18d4252349ddbd7fa28e2394eb26/`).
