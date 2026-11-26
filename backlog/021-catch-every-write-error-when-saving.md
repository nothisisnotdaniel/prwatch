# 021 Catch every write error when saving the ledger and the status

As the user, I want PRWatch to survive any error while saving its usage ledger or status file, so that one odd file error never stops the watcher or a run monitor.

Category: security

## What

UsageLedger.Save and StatusStore.Update catch only IOException and UnauthorizedAccessException. Widen both together (for example to any non-fatal exception from PrivateFileWriter), log a fixed text, and add a test with a writer that throws another exception type.

## Why

Review-security 1 of job 424: another exception from PrivateFileWriter would escape into the runner monitor. Both stores should change together so they stay consistent.

## Notes

Proposed by job 424-token-brake.486949e398f74eacafdcc4e6dceba0e1 (`factory/jobs/424-token-brake.486949e398f74eacafdcc4e6dceba0e1/`).
