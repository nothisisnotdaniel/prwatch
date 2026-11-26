# 046 Announce the armed Consent or Merge state to screen readers

As a user with a screen reader, I want to hear when a Consent or Merge button is armed and when it disarms, so that I know a second tap will send it.

Category: ux

## What

Job 454 arms a Consent or Merge button for 5 s after the first tap, but the change is only visual. Add an announcement (for example an aria-live region or an updated accessible name) with a short text for armed and for disarmed, and a test for it.

## Why

Review-ux 1 of job 454: a screen reader gets no cue that the button is armed or that the 5 s window ran out; the spec ruled out new text, so it needs its own job.

## Notes

Proposed by job 454-armed-button-timeout.c7183f7d8b3747aea4d2821279eec761.

Proposed by job 454-armed-button-timeout.c7183f7d8b3747aea4d2821279eec761 (`factory/jobs/454-armed-button-timeout.c7183f7d8b3747aea4d2821279eec761/`).
