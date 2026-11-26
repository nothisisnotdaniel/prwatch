# 027 Check the tap area of the on/off switch on the iPhone

Category: ui

## What

The `.switch` checkbox is 28px inside a 44px row, so the label and box make up the tap area. Check it on the iPhone and enlarge it only if taps miss.

## Why

review-ui 1 of job 426: probably fine (the reviewer saw no need to change it), but the switch stops every review, so it should be easy to hit.

## Notes

A task for the user (26 Nov 2026): only a tap on the real iPhone can tell. If taps miss, add a new item to
enlarge the switch's tap area to at least 44×44 pt.

Proposed by job 426-web-app-ui.c82fa5db37484371aec59d93c2dd41e6 (`factory/jobs/426-web-app-ui.c82fa5db37484371aec59d93c2dd41e6/`).
