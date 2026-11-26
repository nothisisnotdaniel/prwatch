# 014 Decide whether the projects setting filters factory PRs

Category: security

## What

The watcher keeps `factory` PRs whatever the `projects` setting says (Watcher.cs allowed, as AC-3 of job 422 sets). Decide in a spec whether the setting should also be able to leave factory PRs out, and change the filter and its test if so. Settle it before the real claude runner (#4).

## Why

review-security 1 of job 422: a user who narrows projects may expect factory PRs to stop being reviewed too, and #4 makes each pick start a real paid claude run.

## Notes

Proposed by job 422-watcher-loop.e53dfd362cf04acca992b1b60dd36c2e (`factory/jobs/422-watcher-loop.e53dfd362cf04acca992b1b60dd36c2e/`).
