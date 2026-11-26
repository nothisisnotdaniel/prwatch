# 024 Make sure job.py pr request always consumes request.json

Category: security

## What

Check every failure path of `job.py pr request` (not only exit 8) and make sure request.json is removed or marked used, so a request left behind does not make the PRWatch watcher start a new run on every poll.

## Why

review-security 3 of job 425: a request now bypasses the same-head rule; the token brake caps the cost, but a stuck file would burn runs up to the brake.

## Notes

Proposed by job 425-consent-merge-requests.d848652104824497849170322e175f66 (`factory/jobs/425-consent-merge-requests.d848652104824497849170322e175f66/`).
