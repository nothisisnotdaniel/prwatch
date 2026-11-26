# PRWatch backlog

`/factory next --project PRWatch` runs the first open item, checks it off and records the job id.
Each item is a user story in its own file under `backlog/`, numbered `NNN-slug.md`; the line links it as `#N`.
The order of the lines is the priority. Add items with `python3 factory/tools/job.py add --project PRWatch '<json>'`
(or by hand: write the story file first, then the line), and keep a number once it is given.
Each story has one `Category: <name>` line after its story sentence, one of: feature, bug, security, ux, ui, tidy-up, test, ci, docs.
`- [?]` items are follow-ups the factory proposed: change `[?]` to `[ ]` to approve one, or delete the line and its file.
`- [h]` items are tasks for you. `(after: #3)` waits until item 3 is done; `(after: Times#5)` names another project's item.
- [x] [#1](backlog/001-app-skeleton-on-the-roomkit-core.md) App skeleton on the RoomKit core → 419-app-skeleton-roomkit.bf29a276d8174f69ad61ab80f72c8298
- [x] [#2](backlog/002-settings-and-status-file.md) Settings and status file (after: #1) → 421-settings-status-file.48ccee759b2f4905b1ee027461afa8e9
- [x] [#3](backlog/003-watcher-loop.md) Watcher loop (after: #2) → 422-watcher-loop.e53dfd362cf04acca992b1b60dd36c2e
- [x] [#4](backlog/004-headless-claude-runner.md) Headless claude runner (after: #3; factory#61) → 423-headless-claude-runner.4541c8db4c7748da877aaebbf92c84dd
- [x] [#5](backlog/005-token-brake.md) Token brake (after: #4) → 424-token-brake.486949e398f74eacafdcc4e6dceba0e1
- [x] [#6](backlog/006-consent-merge-and-cancel-requests.md) Consent, Merge and Cancel requests (after: #3) → 425-consent-merge-requests.d848652104824497849170322e175f66
- [x] [#7](backlog/007-web-app-ui.md) Web app UI (after: #2; #5; #6) → 426-web-app-ui.c82fa5db37484371aec59d93c2dd41e6
- [ ] [#8](backlog/008-release-workflow-and-v0-1-0.md) Release workflow and v0.1.0 (after: #1)
- [ ] [#9](backlog/009-install-at-login-and-auto-update.md) Install at login and auto-update (after: #7; #8)
- [x] [#10](backlog/010-create-the-github-repo-nothisisnotdaniel-prwatch.md) Create the GitHub repo nothisisnotdaniel/prwatch and push develop
- [h] [#11](backlog/011-test-prwatch-end-to-end-with.md) Test PRWatch end to end with a real PR (after: #9)
- [ ] [#12](backlog/012-verify-the-roomkit-package-signature-in.md) Verify the RoomKit package signature in CI (after: #1)
- [x] [#13](backlog/013-build-the-headless-allow-list-per.md) Build the headless allow-list per PR
- [?] [#14](backlog/014-decide-whether-the-projects-setting-filters.md) Decide whether the projects setting filters factory PRs (after: #3)
- [?] [#15](backlog/015-report-a-clean-exit-that-races.md) Report a clean exit that races the poll timeout as success
- [?] [#16](backlog/016-stop-a-test-from-changing-process.md) Stop a test from changing process-wide environment variables
- [?] [#17](backlog/017-set-needs-login-only-for-auth.md) Set needs_login only for auth failures, not for timeouts
- [?] [#18](backlog/018-kill-the-claude-process-when-the.md) Kill the claude process when the status update fails after start
- [?] [#19](backlog/019-stop-the-output-reads-when-a.md) Stop the output reads when a run is killed
- [?] [#20](backlog/020-close-the-time-of-check-time.md) Close the time-of-check/time-of-use gap when starting claude
- [?] [#21](backlog/021-catch-every-write-error-when-saving.md) Catch every write error when saving the ledger and the status
- [?] [#22](backlog/022-show-a-new-request-in-the.md) Show a new request in the status even when a poll lands mid-write
- [?] [#23](backlog/023-write-request-json-without-a-check.md) Write request.json without a check-then-write symlink window
- [?] [#24](backlog/024-make-sure-job-py-pr-request.md) Make sure job.py pr request always consumes request.json
- [?] [#25](backlog/025-decide-how-long-an-armed-consent.md) Decide how long an armed Consent or Merge button stays armed
- [?] [#26](backlog/026-show-a-short-sent-text-after.md) Show a short sent text after a Consent, Merge or Cancel tap
- [?] [#27](backlog/027-check-the-tap-area-of-the.md) Check the tap area of the on/off switch on the iPhone
