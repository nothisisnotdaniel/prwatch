# 049 Show factory job pull requests with their job and reused reviews

As the factory owner, I want PRWatch to show which pull requests come from factory jobs and whether their reviews were reused, so that I can see at a glance why one merged without a full review.

Category: ui

## What

For a pull request whose head branch is `factory/<id>`, the list and the detail view show the job id and the outcome of the last round: `reviews reused`, `full review` (with the reason `job.py pr reuse` printed), or `needs rework` (with the `/factory rework <id> --from-pr` command to copy). Release pull requests (`release vX.Y.Z` from develop into main) are shown as `release, merge by hand` and are never reviewed or merged. Update the headless allow-list to match `factory/tools/prwatch_smoke.py` after factory's reuse item, keeping the lists byte-equal (the test pins them). All of this is text from `job.py pr poll`, shown with `textContent`.

## Why

Once factory jobs land through pull requests, most of them will merge with reused reviews. Without a label, a merge without a review run looks like a bug or a skipped check.

## Notes

Planned on 2026-11-26 as part of the PR-based redesign. Only the allow-list change touches PRWatch's safety rules; it is high risk.
