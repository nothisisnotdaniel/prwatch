# 031 Keep a live review's update lock when a second instance starts

Category: security

## What

A second PRWatch instance on the same data folder (for example `dotnet run` while the LaunchAgent server runs) removes a live run's `.update.lock` at start, so the updater could swap the server mid-review. Record the owning PID in the `prwatch-run` marker and let `UpdateLockCleaner` remove the lock only when that PID is no longer alive (or refuse a second instance), with a test for a live PID.

## Why

Review-security 1 of job 428: swapping the app in the middle of a review leaves a half-written round, which is exactly what the lock is there to prevent.

## Notes

Proposed by job 428-install-login-autoupdate.a596cf0b74a0402cb519bfe12ad1b796 (`factory/jobs/428-install-login-autoupdate.a596cf0b74a0402cb519bfe12ad1b796/`).
