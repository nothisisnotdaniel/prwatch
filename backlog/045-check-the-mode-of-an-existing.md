# 045 Check the mode of an existing prs/ or PR folder before writing a request

As the user, I want PRWatch to refuse or tighten a prs/ or PR folder that others can write to, so that no other local user can swap request files under it.

Category: security

## What

FolderHandleWriter accepts an existing `factory/prs/` or `factory/prs/<repo>-<n>/` folder with whatever mode it has. Decide the rule (refuse a group- or world-writable folder, or one owned by another user, with a fixed status text; or tighten it) and add tests for both folders.

## Why

Review-security 2 of job 448: the folder walk is now symlink-safe, but a writable folder owned by someone else still lets them replace files in it. This was the behaviour before the change too.

## Notes

Proposed by job 448-request-write-symlink.f200b8eab1294bad99e5e46bb94227ed.

Proposed by job 448-request-write-symlink.f200b8eab1294bad99e5e46bb94227ed (`factory/jobs/448-request-write-symlink.f200b8eab1294bad99e5e46bb94227ed/`).
