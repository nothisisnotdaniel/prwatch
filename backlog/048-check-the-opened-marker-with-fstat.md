# 048 Check the opened marker with fstat instead of a separate fstatat

As the user, I want PRWatch to check the marker file it actually opened, so that no swap between the check and the open can slip through.

Category: security

## What

`FolderHandleWriter.ReadNoFollow` checks that the name is a regular file with `fstatat`, then opens it with a separate `openat`. Check the opened descriptor with `fstat` instead (a per-platform P/Invoke: `fstat$INODE64` on macOS x64, the plain symbol on arm64, glibc versions on Linux), and add a test.

## Why

Review-security 1 of job 461: `O_NOFOLLOW | O_NONBLOCK` already make the gap harmless (no symlink, no FIFO hang, a directory fails the read), but an fstat on the descriptor would close it fully.

## Notes

Proposed by job 461-keep-live-update-lock.792106e26c9f4022947e469c2f0a3397.

Proposed by job 461-keep-live-update-lock.792106e26c9f4022947e469c2f0a3397 (`factory/jobs/461-keep-live-update-lock.792106e26c9f4022947e469c2f0a3397/`).
