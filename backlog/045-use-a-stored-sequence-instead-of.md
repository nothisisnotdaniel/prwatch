# 045 Use a stored sequence instead of the clock to tell whether a request was used

As the user, I want PRWatch to decide whether a consent or merge tap was already used without relying on the wall clock, so that a clock set back can't make one tap count twice.

Category: security

## What

The rule from job 449 that decides whether request.json was used compares timestamps, so it assumes the clock never goes back (clock skew bounds the window). Replace it with a stored sequence counter for taps and runs, decide how the stored format changes and how old files are read, and add tests for a clock that goes back.

## Why

Review-security 2 of job 449: the used rule depends on the wall clock not going back; a sequence counter would remove that assumption but changes the stored format, so it needs its own job.

## Notes

Proposed by job 449-request-always-consumed.97a3ea214ef14befa45d79a528b16db8.

Proposed by job 449-request-always-consumed.97a3ea214ef14befa45d79a528b16db8 (`factory/jobs/449-request-always-consumed.97a3ea214ef14befa45d79a528b16db8/`).
