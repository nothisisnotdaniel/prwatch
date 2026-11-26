# 043 Wait for the run timeout timer in the AC-2 runner test

As a maintainer, I want the AC-2 runner timeout test to wait until the run's timeout timer is registered before it advances the clock, so that it cannot depend on timing.

Category: test

## What

In tests/PRWatch.Tests/RunnerTests.cs, the AC-2 test advances the clock once the calls file has one entry and does not wait for the runner's timeout timer to be registered. The builder of job 439 argues that this is deterministic today (TryStart creates the timer first, and Register on a token that is already cancelled runs the callback). Make the wait explicit, or document why it is not needed.

## Why

review-code round 1 of job 439 raised this as a nit; it matches the existing timeout test pattern, so it was left.

## Notes

Proposed by job 439-needs-login-auth-only.7a04b79dac2a45bbbd35f48f71acc60d.

Proposed by job 439-needs-login-auth-only.7a04b79dac2a45bbbd35f48f71acc60d (`factory/jobs/439-needs-login-auth-only.7a04b79dac2a45bbbd35f48f71acc60d/`).
