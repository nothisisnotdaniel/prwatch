# 023 Write request.json without a check-then-write symlink window

Category: security

## What

RequestWriter checks for links (IsPlainFolder, EnsureFolder, LinkTarget) before SafeFileWriter writes, so a same-user process could swap in a symlink in between (TOCTOU). Use an openat/O_NOFOLLOW-style writer once RoomKit offers one, or add one there.

## Why

review-security 1 of job 425: low risk (the rename replaces the link and the attacker needs the user's rights), but the safety rules say never write through a symlink.

## Notes

Proposed by job 425-consent-merge-requests.d848652104824497849170322e175f66 (`factory/jobs/425-consent-merge-requests.d848652104824497849170322e175f66/`).
