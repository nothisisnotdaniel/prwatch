# 022 Show a new request in the status even when a poll lands mid-write

Category: bug

## What

In PrWatchApp.cs (around line 91) the status update sets `Request` only on the PR whose head still matches. A poll that lands between the check and the update means the request is not shown until the next poll from job.py. Find a way to show it at once without showing a request against a newer head.

## Why

review-code 1 of job 425: the window is tiny and the file is already written, but the UI briefly hides a tap the user just made.

## Notes

Proposed by job 425-consent-merge-requests.d848652104824497849170322e175f66 (`factory/jobs/425-consent-merge-requests.d848652104824497849170322e175f66/`).
