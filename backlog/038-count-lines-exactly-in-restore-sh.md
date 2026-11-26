# 038 Count lines exactly in restore.sh's token file check

As the user, I want restore.sh's one-line check on the token file to be exact, so that a malformed file is rejected instead of half read.

Category: tidy-up

## What

scripts/restore.sh line 42 checks that the token file has exactly one line with `wc -l`, which counts newlines, so a two-line file whose last line has no trailing newline passes and only line 1 is used. Count with `sed -n '$='` (or equivalent) and add a ScriptTests case for that file.

## Why

Job 432's round-2 code review (nit 1). Harmless today because the value is still validated.

## Notes

From job 432 round 2, review-code nit 1.
