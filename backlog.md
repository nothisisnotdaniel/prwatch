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
- [x] [#8](backlog/008-release-workflow-and-v0-1-0.md) Release workflow and v0.1.0 (after: #1) → 427-release-workflow.0cba18d4252349ddbd7fa28e2394eb26
- [x] [#9](backlog/009-install-at-login-and-auto-update.md) Install at login and auto-update (after: #7; #8) → 428-install-login-autoupdate.a596cf0b74a0402cb519bfe12ad1b796
- [x] [#10](backlog/010-create-the-github-repo-nothisisnotdaniel-prwatch.md) Create the GitHub repo nothisisnotdaniel/prwatch and push develop
- [x] [#34](backlog/034-switch-prwatch-to-roomkit-0-1.md) Switch PRWatch to RoomKit 0.1.0 from the roomkit repo → 432-roomkit-github-packages.9be0ff12fcfa4691be4c66d09b589556
- [h] [#11](backlog/011-test-prwatch-end-to-end-with.md) Test PRWatch end to end with a real PR (after: #9)
- [x] [#12](backlog/012-verify-the-roomkit-package-signature-in.md) Verify the RoomKit package signature in CI (after: #1) → 430-roomkit-signature-ci.0c91bd8ba359453e94f1e4c8273d544b
- [x] [#13](backlog/013-build-the-headless-allow-list-per.md) Build the headless allow-list per PR
- [x] [#14](backlog/014-decide-whether-the-projects-setting-filters.md) Decide whether the projects setting filters factory PRs (after: #3) → 433-projects-filter-factory-prs.89a797ae58af4ecbbdfe5ffee4d07514
- [x] [#15](backlog/015-report-a-clean-exit-that-races.md) Report a clean exit that races the poll timeout as success → 436-poll-clean-exit-race.ee8d9b0d25a84c40a393b4ab960d21a7
- [x] [#16](backlog/016-stop-a-test-from-changing-process.md) Stop a test from changing process-wide environment variables → 437-test-env-hook.76d2e56cfedd4e1a8085d51d667af006
- [x] [#17](backlog/017-set-needs-login-only-for-auth.md) Set needs_login only for auth failures, not for timeouts → 439-needs-login-auth-only.7a04b79dac2a45bbbd35f48f71acc60d
- [x] [#18](backlog/018-kill-the-claude-process-when-the.md) Kill the claude process when the status update fails after start → 442-kill-on-status-fail.bfff1b19358e4e3a87b1f0780f6e3808
- [x] [#19](backlog/019-stop-the-output-reads-when-a.md) Stop the output reads when a run is killed → 443-kill-stops-output-reads.911bda1e867a4b8180bb97b4414ebba9
- [x] [#21](backlog/021-catch-every-write-error-when-saving.md) Catch every write error when saving the ledger and the status → 444-catch-save-write-errors.5aec37763d074f9a863e4e85ce4dfb99
- [x] [#22](backlog/022-show-a-new-request-in-the.md) Show a new request in the status even when a poll lands mid-write → 445-request-status-race.d41650a57c8844e382f5342710b46d83
- [x] [#23](backlog/023-write-request-json-without-a-check.md) Write request.json without a check-then-write symlink window → 448-request-write-symlink.f200b8eab1294bad99e5e46bb94227ed
- [x] [#24](backlog/024-make-sure-job-py-pr-request.md) Make sure job.py pr request always consumes request.json → 449-request-always-consumed.97a3ea214ef14befa45d79a528b16db8
- [ ] [#25](backlog/025-decide-how-long-an-armed-consent.md) Decide how long an armed Consent or Merge button stays armed
- [ ] [#26](backlog/026-show-a-short-sent-text-after.md) Show a short sent text after a Consent, Merge or Cancel tap
- [x] [#27](backlog/027-check-the-tap-area-of-the.md) Check the tap area of the on/off switch on the iPhone
- [ ] [#28](backlog/028-drop-the-unneeded-checkout-in-the.md) Drop the unneeded checkout in the release job
- [ ] [#29](backlog/029-verify-the-release-tarball-independently-of.md) Verify the release tarball independently of the build job's hash
- [ ] [#30](backlog/030-tidy-the-publish-output-flag-in.md) Tidy the publish output flag in release.yml
- [ ] [#31](backlog/031-keep-a-live-review-s-update.md) Keep a live review's update lock when a second instance starts
- [ ] [#32](backlog/032-note-why-the-roomkit-feed-tests.md) Note why the RoomKit check tests need bash
- [x] [#35](backlog/035-put-prwatch-on-tailscale-and-add.md) Put PRWatch on Tailscale and add it to the iPhone home screen (after: #34)
- [ ] [#36](backlog/036-keep-the-stored-github-packages-token.md) Keep the stored GitHub Packages token behind a keychain prompt
- [ ] [#37](backlog/037-check-the-roomkit-package-hash-in.md) Check the RoomKit package hash in restore.sh
- [ ] [#38](backlog/038-count-lines-exactly-in-restore-sh.md) Count lines exactly in restore.sh's token file check
- [ ] [#39](backlog/039-test-the-factory-pr-filter-with.md) Test the factory PR filter with an empty projects list
- [ ] [#40](backlog/040-skip-the-poll-tree-kill-after.md) Skip the poll tree kill after the process has exited
- [ ] [#41](backlog/041-cover-both-empty-values-for-each.md) Cover both empty values for each name in the runner environment tests
- [ ] [#42](backlog/042-keep-the-login-state-after-an.md) Keep the login state after an error run that had an auth result
- [ ] [#43](backlog/043-wait-for-the-run-timeout-timer.md) Wait for the run timeout timer in the AC-2 runner test
- [ ] [#44](backlog/044-guard-the-lock-release-in-the.md) Guard the lock release in the runner's abandon path
- [ ] [#45](backlog/045-use-a-stored-sequence-instead-of.md) Use a stored sequence instead of the clock to tell whether a request was used
