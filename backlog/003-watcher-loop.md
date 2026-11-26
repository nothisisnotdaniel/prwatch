# 003 Watcher loop

As the user, I want PRWatch to look at the open PRs every few minutes and pick the next one that needs a review, so that PRs are reviewed without me starting anything.

Category: feature

## What

A hosted background loop that, while switched on, runs `python3 factory/tools/job.py pr poll` (fixed argv, cwd the factory root, timeout) every poll interval, parses its JSON, stores the PR list in the status, and picks at most one PR to review: round missing or not `current`, head unchanged for the quiet time, trusted author with auto-review on or a consent request present, its project has no factory job in `spec`, `build`, `review` or `approve`, and no run already going. The pick goes to a runner interface (a fake in this item; the real `claude` runner is the next item). Skipped projects and poll errors show in the status with fixed texts.

## Why

Polling costs no tokens; deciding in code when a review is due keeps runs rare and never collides with a factory job in the same checkout.

## Notes

From job 421 (review-security 2): when loading the status file, accept only `runs` keys that match `<repo>-<n>` (repo a known project or `factory`, n digits); drop or ignore any other key.
