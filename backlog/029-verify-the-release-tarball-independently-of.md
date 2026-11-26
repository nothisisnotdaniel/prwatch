# 029 Verify the release tarball independently of the build job's hash

Category: security

## What

The attestation in release.yml covers the SHA-256 the macos build job reports, so a compromised build job could report its own hash. Recompute and check the hash of the downloaded artifact in the release job (or attest from the artifact itself). ControlRoom has the same limit.

## Why

Review-security 3 of job 427: the provenance should not rest on a value from the job it is meant to vouch for.

## Notes

Proposed by job 427-release-workflow.0cba18d4252349ddbd7fa28e2394eb26 (`factory/jobs/427-release-workflow.0cba18d4252349ddbd7fa28e2394eb26/`).
