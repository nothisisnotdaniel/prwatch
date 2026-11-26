# 030 Tidy the publish output flag in release.yml

Category: tidy-up

## What

Review-code 2 of job 427 questioned `-o "$RUNNER_TEMP/publish"` on the publish step; the smoke and pack steps rely on it and the tests assert it. Either document why it is there in a comment or move the path into one shared env variable.

## Why

Review-code 2 of job 427: the flag reads as unexplained to the next reader.

## Notes

Proposed by job 427-release-workflow.0cba18d4252349ddbd7fa28e2394eb26 (`factory/jobs/427-release-workflow.0cba18d4252349ddbd7fa28e2394eb26/`).
