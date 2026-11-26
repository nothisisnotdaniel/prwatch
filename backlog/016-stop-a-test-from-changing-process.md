# 016 Stop a test from changing process-wide environment variables

As a maintainer, I want the runner tests to leave the process environment alone, so that tests stay independent and can run in parallel.

Category: test

## What

A runner test sets process-wide environment variables. Add an environment hook on the runner or the fake so the test can supply its own environment instead.

## Why

Raised by review-code 1 of job 423. Shared process state can make tests depend on each other.

## Notes

Proposed by job 423-headless-claude-runner.4541c8db4c7748da877aaebbf92c84dd (`factory/jobs/423-headless-claude-runner.4541c8db4c7748da877aaebbf92c84dd/`).
