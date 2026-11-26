# 013 Build the headless allow-list per PR

As the owner of the factory, I want PRWatch to start each headless /factory pr session with an allow-list scoped to that PR, so that a session reviewing one PR cannot write review files for another.

Category: security

## What

Factory job 418 replaced prwatch_smoke.ALLOWED_TOOLS with allowed_tools(repo, n), which limits Write and Edit to factory/prs/<repo>-<n>/round-*/. PRWatch still builds a fixed list that allows writes into every PR's round folders. Build the --allowedTools argv per PR from the same rules, and update PRWatch's test that compares its lists with prwatch_smoke.py's.

## Why

Until PRWatch uses the per-PR list, factory job 418's protection does not apply to real headless runs: a hostile PR's session can still write a passing review into another PR's round folder.

## Notes

Proposed by factory job 418-scope-headless-pr-writes.6407bab1a4a548ed9c4cd8bac5ab6a58 (spec out of scope; review-security nit 2).
