# PRWatch backlog

`/factory next --project PRWatch` runs the first open item, checks it off and records the job id.
Each item is a user story in its own file under `backlog/`, numbered `NNN-slug.md`; the line links it as `#N`.
The order of the lines is the priority. Add items with `python3 factory/tools/job.py add --project PRWatch '<json>'`
(or by hand: write the story file first, then the line), and keep a number once it is given.
Each story has one `Category: <name>` line after its story sentence, one of: feature, bug, security, ux, ui, tidy-up, test, ci, docs.
`- [?]` items are follow-ups the factory proposed: change `[?]` to `[ ]` to approve one, or delete the line and its file.
`- [h]` items are tasks for you. `(after: #3)` waits until item 3 is done; `(after: Times#5)` names another project's item.
- [x] [#1](backlog/001-app-skeleton-on-the-roomkit-core.md) App skeleton on the RoomKit core → 419-app-skeleton-roomkit.bf29a276d8174f69ad61ab80f72c8298
- [ ] [#2](backlog/002-settings-and-status-file.md) Settings and status file (after: #1)
- [ ] [#3](backlog/003-watcher-loop.md) Watcher loop (after: #2)
- [ ] [#4](backlog/004-headless-claude-runner.md) Headless claude runner (after: #3; factory#61)
- [ ] [#5](backlog/005-token-brake.md) Token brake (after: #4)
- [ ] [#6](backlog/006-consent-merge-and-cancel-requests.md) Consent, Merge and Cancel requests (after: #3)
- [ ] [#7](backlog/007-web-app-ui.md) Web app UI (after: #2; #5; #6)
- [ ] [#8](backlog/008-release-workflow-and-v0-1-0.md) Release workflow and v0.1.0 (after: #1)
- [ ] [#9](backlog/009-install-at-login-and-auto-update.md) Install at login and auto-update (after: #7; #8)
- [x] [#10](backlog/010-create-the-github-repo-nothisisnotdaniel-prwatch.md) Create the GitHub repo nothisisnotdaniel/prwatch and push develop
- [h] [#11](backlog/011-test-prwatch-end-to-end-with.md) Test PRWatch end to end with a real PR (after: #9)
- [ ] [#12](backlog/012-verify-the-roomkit-package-signature-in.md) Verify the RoomKit package signature in CI (after: #1)
