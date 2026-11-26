# 017 Set needs_login only for auth failures, not for timeouts

As the user, I want PRWatch to show "needs login" only when the login really failed, so that I am not sent to log in again after a slow run.

Category: bug

## What

needs_login is also set when a run ends as a timeout. Limit it to runs whose result is an auth error.

## Why

Raised by review-code 3 of job 423. A timeout is not a login problem and pauses every run for no reason.

## Notes

Proposed by job 423-headless-claude-runner.4541c8db4c7748da877aaebbf92c84dd (`factory/jobs/423-headless-claude-runner.4541c8db4c7748da877aaebbf92c84dd/`).
