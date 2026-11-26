# 025 Decide how long an armed Consent or Merge button stays armed

Category: security

## What

In wwwroot/app.js an armed Consent or Merge button (second-tap state) stays armed until the 30 s refresh re-renders the list, which then silently resets it. Pick one rule: disarm after a short timeout (review-security 1) or keep the armed state across a refresh (review-ux 1). Then build it, with a test.

## Why

review-security 1 and review-ux 1 of job 426 pull in opposite directions: a stray tap on a button left armed sends a consent or merge, while a refresh that resets it in the middle of a tap is confusing. One decision covers both.

## Notes

Proposed by job 426-web-app-ui.c82fa5db37484371aec59d93c2dd41e6 (`factory/jobs/426-web-app-ui.c82fa5db37484371aec59d93c2dd41e6/`).
