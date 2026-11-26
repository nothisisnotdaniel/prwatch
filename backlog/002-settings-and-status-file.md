# 002 Settings and status file

As the user, I want PRWatch's switches and limits in one settings file and its current state in one status file, so that the watcher, the brake and the UI all read the same values.

Category: feature

## What

A settings file in `~/Library/Application Support/PRWatch/settings.json` with defaults: on/off (default off), factory root, projects to watch (default: all discovered), poll interval 5 min, quiet time after a push 10 min, auto-review for trusted authors (on), auto-merge for trusted authors (off), reviewer mode (default the factory's `default` mode; model from `factory/modes.json`), per-run `--max-budget-usd` cap, brake thresholds (5 h and 7 d utilization, 5 h and 7 d cost, runs per hour = 3), and the absolute `claude` path. A status file (last poll, last run per PR, brake state, login state, last error) written atomically. GET/PUT `/api/settings` with validation (ranges, absolute paths, known projects and modes) and GET `/api/status`.

## Why

Every later piece needs these values; putting them in one validated file keeps the on/off switch and the limits under the user's control.
