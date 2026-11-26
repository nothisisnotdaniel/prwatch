# 012 Verify the RoomKit package signature in CI

As the owner of PRWatch, I want CI to check that the RoomKit package really comes from ControlRoom's release workflow, so that a tampered release can't slip into the build.

Category: security

## What

CI now downloads RoomKit.0.3.0.nupkg from the ControlRoom v0.3.0 release and compares it with SHA256SUMS from that same release. Add a check that does not trust the release alone, for example `gh attestation verify` against a build attestation ControlRoom's release workflow publishes, or a checksum pinned in the PRWatch repo. If ControlRoom publishes no attestation yet, pin the SHA-256 of the package in PRWatch and fail CI when it differs.

## Why

A checksum taken from the same release only catches a corrupted download. Anyone who can change the release can change both the package and its checksum. Proposed by job 419-app-skeleton-roomkit (security review nit).
