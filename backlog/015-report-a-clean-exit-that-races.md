# 015 Report a clean exit that races the poll timeout as success

Category: security

## What

In PollRunner.RunAsync a job.py poll that exits cleanly just as the timeout fires is reported as poll_timeout. Prefer the clean exit when the process has already exited with code 0 before the kill.

## Why

review-security 2 of job 422: it fails safe today, but a spurious poll_timeout shows a wrong error and skips a poll; the kill and timeout path is high risk, so it needs its own reviewed job.

## Notes

Proposed by job 422-watcher-loop.e53dfd362cf04acca992b1b60dd36c2e (`factory/jobs/422-watcher-loop.e53dfd362cf04acca992b1b60dd36c2e/`).
