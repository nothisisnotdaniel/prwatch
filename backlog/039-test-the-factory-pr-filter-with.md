# 039 Test the factory PR filter with an empty projects list

As the maintainer, I want the watcher tests to cover factory PRs with an empty projects list too, so that a regression in the default case is caught.

Category: test

## What

In tests/PRWatch.Tests/WatcherTests.cs the factory skip and the 'factory job in build' check are only exercised with `projects: ["Alpha"]`. Add cases with an empty `projects` list: a factory PR is watched, and it waits while a factory job is in `build`.

## Why

review-code round 1 of job 433 noted the empty-list path is only covered indirectly; it is the default setting, so a break there would hit every user.

## Notes

Proposed by job 433 (review-code nit 2, left as no change needed).

Proposed by job 433-projects-filter-factory-prs.89a797ae58af4ecbbdfe5ffee4d07514 (`factory/jobs/433-projects-filter-factory-prs.89a797ae58af4ecbbdfe5ffee4d07514/`).
