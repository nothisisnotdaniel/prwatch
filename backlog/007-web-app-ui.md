# 007 Web app UI

As the user, I want one page on my phone and Mac that lists the open PRs with their review state and the buttons I need, so that I can follow and steer the reviews from anywhere.

Category: ui

## What

The `wwwroot` app: PR list per project (title, author, trusted or not, stage, round verdict, tests, mergeable, last run time and cost), the Consent, Merge and Cancel buttons where they apply (with the head they act on), the on/off switch, a settings form, and a status bar (brake and its release time, login state and expiry warning, last poll, last error). Mobile first, light and dark, untrusted text via `textContent` only, fixed error texts, refreshes on a timer while visible.

## Why

The taps are how the user gives consent and merges for untrusted authors; the status makes the brake and a needed login visible before reviews silently stop.

## Notes

From job 421:
- Show the `_unknown` status value and the `settings_unreadable` error with fixed, plain texts (review-ux 1 and 2).
- A `POST /api/settings` that changes other fields still fails while the stored `factoryRoot` is invalid; the form should say so and point at the `factoryRoot` field (review-code 1).
- Before rendering, ignore `runs` keys that don't match `<repo>-<n>` (review-security 2).
