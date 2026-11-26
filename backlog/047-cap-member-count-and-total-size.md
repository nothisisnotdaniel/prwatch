# 047 Cap member count and total size in the release tarball check

As the user, I want the release check to stop on an oversized archive, so that a hostile tarball can't tie up the CI runner until the job times out.

Category: security

## What

`.github/scripts/compare-release.py` reads every member of the downloaded archive with no limit on count or total uncompressed size. Add a cap on both (set from the real tarball with room to spare), fail with a fixed message when it is passed, and test both limits.

## Why

Review-security 2 of job 458: not a way past the check, but a DoS on the CI runner.

## Notes

Proposed by job 458-verify-release-hash.2627d768c70142ccbf0f691f91a4bd9f.

Proposed by job 458-verify-release-hash.2627d768c70142ccbf0f691f91a4bd9f (`factory/jobs/458-verify-release-hash.2627d768c70142ccbf0f691f91a4bd9f/`).
