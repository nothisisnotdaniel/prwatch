# 008 Release workflow and v0.1.0

As the user, I want PRWatch released like ControlRoom, so that the installed app can update itself from a tagged release.

Category: ci

## What

A release workflow on a merged `release vX.Y.Z` PR into `main`: tests, publish, a tarball named for the updater, a GitHub release with the tag. Version from the PR title into `InformationalVersion`. Cut v0.1.0.

## Why

The install and the auto-updater need releases to fetch.

## Notes

Mirror `ControlRoom/.github/workflows/release.yml`.
