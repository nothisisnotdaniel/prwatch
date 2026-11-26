# 032 Note why the RoomKit check tests need bash

Category: tidy-up

## What

tests/PRWatch.Tests/RoomKitCheckStepTests.cs (which replaced RoomKitFeedStepTests.cs in job 432, commit db8c093) needs /bin/bash and Unix file modes, like the existing ReleaseWorkflowTests, so neither runs on Windows. CI and releases run on macOS only. Add a short comment saying so (or skip both on Windows with a clear reason).

## Why

Review-code 1 of job 430: the platform limit is unstated for the next reader.

## Notes

Proposed by job 430-roomkit-signature-ci.0c91bd8ba359453e94f1e4c8273d544b (`factory/jobs/430-roomkit-signature-ci.0c91bd8ba359453e94f1e4c8273d544b/`).

Reworded in the backlog audit, 2026-11-26.
