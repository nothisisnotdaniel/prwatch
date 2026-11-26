# 042 Keep the login state after an error run that had an auth result

As the user, I want PRWatch not to report the login as ok after a run that failed with an I/O error but had reported an auth error, so that I am not shown a stale ok signal.

Category: security

## What

In src/PRWatch/ClaudeReviewRunner.cs (around line 401), a run that ends as error because of an I/O exception (failed, reason Error) after an auth result still sets login.state to ok, because the killed check leaves out RunStatus.Error. Treat any set reason the same way (run.Reason is not null && figures.AuthInResult → keep the state).

## Why

review-security round 1 of job 439: the behaviour was there before that job and the spec didn't cover it, but ok after an auth error is a stale signal on the login path, so it needs its own story.

## Notes

Proposed by job 439-needs-login-auth-only.7a04b79dac2a45bbbd35f48f71acc60d.

Proposed by job 439-needs-login-auth-only.7a04b79dac2a45bbbd35f48f71acc60d (`factory/jobs/439-needs-login-auth-only.7a04b79dac2a45bbbd35f48f71acc60d/`).
