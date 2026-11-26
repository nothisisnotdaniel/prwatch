# 004 Headless claude runner

As the user, I want PRWatch to run the factory's PR review as a headless `claude -p` call with exactly the measured flags, so that reviews happen unattended but cannot do more than review.

Category: feature

## What

The real runner: starts the configured `claude` binary with the argv of `factory/tools/prwatch_smoke.py` (`/factory pr --headless --project <P> <n>`, `--model`, stream-json, `--no-session-persistence`, `--permission-mode dontAsk`, `--permission-prompts none`, `--max-budget-usd`, `--append-system-prompt`, `--add-dir`, the allowed and disallowed tool lists), cwd the factory root, environment only HOME, USER, TMPDIR, FACTORY_PRS_DIR, FACTORY_PR_HEADLESS=1 and the fixed PATH. Reads stream-json line by line (capped line length and total size, timeout and kill), records the last `result` (cost, `modelUsage`, `is_error`) and every `rate_limit_event`. An auth error sets login state "needs login" and pauses all runs; warn 7 days before `credential_expires_at`. A test pins the argv and the tool lists byte-equal to `prwatch_smoke.py`.

## Why

This is the one place PRWatch spends tokens and the only automated `claude` start in the factory; the flags were measured in spikes 393 and 408 so the run can only call `job.py pr` and write its own round files.

## Notes

High risk. Never `--dangerously-skip-permissions` or `--bare`. Needs factory #61 so the run can write only the reviewed PR's folder.

From job 421 (review-security 1): settings accept `claudePath` as a symlink. Resolve it again right before each start, check the resolved target is an executable regular file, and run that resolved path, so a swapped link is caught.
