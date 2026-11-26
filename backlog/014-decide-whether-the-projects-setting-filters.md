# 014 Decide whether the projects setting filters factory PRs

Category: security

## What

The watcher keeps `factory` PRs whatever the `projects` setting says (Watcher.cs allowed, as AC-3 of job 422 sets). Decide in a spec whether the setting should also be able to leave factory PRs out, and change the filter and its test if so. Settle it before the real claude runner (#4).

## Why

review-security 1 of job 422: a user who narrows projects may expect factory PRs to stop being reviewed too, and #4 makes each pick start a real paid claude run.

## Decision

The user chose on 26 Nov 2026 to let the `projects` setting filter `factory` PRs like any other project:
- An empty `projects` list still means every discovered project, `factory` included.
- A list that is filled in and leaves out `factory` skips factory PRs: no review runs for them.
- No new setting. Change the filter in `Watcher.cs` and its test to match. If the settings form describes the
  projects list, it should say that `factory` is one of the choices.

## Notes

Proposed by job 422-watcher-loop.e53dfd362cf04acca992b1b60dd36c2e (`factory/jobs/422-watcher-loop.e53dfd362cf04acca992b1b60dd36c2e/`).
