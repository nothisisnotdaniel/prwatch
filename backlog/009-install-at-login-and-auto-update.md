# 009 Install at login and auto-update

As the user, I want PRWatch to start at login and update itself, so that it watches PRs without me starting it.

Category: feature

## What

Copy RoomKit's `scripts/launch-agent.sh` and `scripts/updater.sh` and fill only the `roomkit:app-begin`/`app-end` block (APP_NAME PRWatch, labels, support and log folders, REPO_SLUG, port 8101, tarball prefix, server name, solution file). A `gui/<uid>` LaunchAgent with KeepAlive (so the keychain login works). The updater never swaps while a review run is going or a factory job is active; PRWatch exposes that as a status the updater checks.

## Why

A watcher that has to be started by hand isn't a watcher; swapping mid-review would leave a half-written round.
