# 046 Compare the full file mode in the release tarball check

As the user, I want the release check to compare every file's full mode, so that a compromised build job can't ship the same bytes with setuid, setgid or group/other-write bits.

Category: security

## What

`.github/scripts/compare-release.py` compares only the owner-execute bit (`mode & 0o100`) between the rebuilt and the downloaded tarball. Compare the permission bits that matter (setuid, setgid, sticky, group/other write, and the execute bits) or the full `mode & 0o7777`, after checking what `dotnet publish` and `tar` produce on both runners, and add tests for a member that differs only in those bits.

## Why

Review-security 1 of job 458: identical bytes with a world-writable `PRWatch` or `updater.sh` would still pass the verify job.

## Notes

Proposed by job 458-verify-release-hash.2627d768c70142ccbf0f691f91a4bd9f.

Proposed by job 458-verify-release-hash.2627d768c70142ccbf0f691f91a4bd9f (`factory/jobs/458-verify-release-hash.2627d768c70142ccbf0f691f91a4bd9f/`).
