# 041 Cover both empty values for each name in the runner environment tests

As a maintainer, I want the empty-reader runner tests to check both an empty string and a missing value for every environment name, so that each name is tested for both cases.

Category: test

## What

In tests/PRWatch.Tests/RunnerTests.cs, the empty-reader tests return "" for most names and null for USER. Between them they cover both cases, but each name is tested for only one. Add test rows so that every name gets both "" and null.

## Why

review-code round 1 of job 437 raised this as a nit and called the current coverage fine; the builder left it because it adds test rows and changes no behaviour.

## Notes

Proposed by job 437-test-env-hook.76d2e56cfedd4e1a8085d51d667af006.

Proposed by job 437-test-env-hook.76d2e56cfedd4e1a8085d51d667af006 (`factory/jobs/437-test-env-hook.76d2e56cfedd4e1a8085d51d667af006/`).
