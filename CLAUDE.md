# PRWatch — conventions

Every factory agent reads this file before working on PRWatch. Keep it short and accurate.

- **What it is:** a small app on the user's Mac that watches the open pull requests of the factory and its
  projects and has them reviewed by the factory without anyone at the keyboard. It polls with `job.py pr poll`
  (no tokens while idle), starts one headless `claude -p "/factory pr --headless --project <P> <n>"` run per new or
  changed PR, merges clean rounds of trusted authors (write access) through that run, and shows everything in a
  web app (Mac browser and iPhone home-screen PWA) with the Consent and Merge taps for untrusted authors. It
  starts at login. It is the **only** program in the factory allowed to start `claude` on its own, and only for
  `/factory pr` reviews (the user's rule of 22 Nov 2026): never add any other kind of run, schedule or job.
- **Stack:** .NET 10 ASP.NET Core minimal API on the **RoomKit core only** (the `RoomKit` NuGet package from the
  `nothisisnotdaniel/roomkit` repo, restored from GitHub Packages, source `github` =
  `https://nuget.pkg.github.com/nothisisnotdaniel/index.json` in `nuget.config`, which maps only `RoomKit` there and
  everything else to nuget.org; docs: `https://github.com/nothisisnotdaniel/roomkit/blob/main/docs/roomkit.md`).
  No `FactoryModule`, no `IJobFormat`: `Program.cs` is
  `return RoomKitApp.Run(args, PrWatchApp.Options, PrWatchApp.Map, addServices: PrWatchApp.AddServices);`.
  Plain HTML/CSS/JS in `wwwroot` (no CDN, no build step, strict CSP), xUnit tests with `Microsoft.AspNetCore.Mvc.Testing`. No other package without a reason
  in the spec.
- **Names:** `AppName` "PRWatch", `ShortName` "PRWatch", `DataFolderName` "PRWatch", `SolutionFileName`
  `PRWatch.slnx`, `DefaultPort` **8101** (ControlRoom is 8100), `ReleaseRepository` `nothisisnotdaniel/prwatch`
  (the GitHub repo is still to be created, see the backlog).
- **Test command:** `dotnet test` (from `PRWatch/`; `dotnet build` must stay at 0 warnings). Tests never read the
  real factory root, never start a real `claude`, `gh` or `git` against the network and never open a real port:
  they build a temporary factory root with a fake `factory/tools/job.py` and a fake `claude` script on a test
  `PATH`, and host the app in memory. The in-memory harness is `tests/PRWatch.Tests/PrWatchFactory.cs` (own temp
  folder, `--TokenFile=<temp>/PRWatch/access-token`, `--environment=Production`, `Host: 127.0.0.1:8101`).
  Before the first `dotnet build` or `dotnet test` on a Mac (and after a RoomKit version bump), run
  `scripts/restore.sh` once (README "RoomKit"). It reads a classic PAT with only `read:packages` from the keychain
  (service `prwatch-github-packages`) or from `~/Library/Application Support/PRWatch/github-packages` (0600) and sets
  `NuGetPackageSourceCredentials_github=Username=<user>;Password=<token>` for that one `dotnet restore` only. The PAT
  is never committed (no `packageSourceCredentials` in `nuget.config`) and never exported globally: a shell
  profile export would hand it to every process, including `/factory pr` test runs of untrusted PR code. CI uses
  `GITHUB_TOKEN` only on the `Restore` steps (jobs with just `contents: read` and `packages: read`); the next step,
  "Check the RoomKit package", reads the version pinned in `src/PRWatch/PRWatch.csproj` (`Version="[x.y.z]"`), checks the restored
  `roomkit.<v>.nupkg` against `roomkit.sha256` and its build-provenance attestation from roomkit's `release.yml` on
  `refs/heads/main` (a merged pull request, GitHub-hosted runner) before any build. A RoomKit version bump changes the
  csproj and `roomkit.sha256` in the same commit.
- **Access:** as RoomKit gives it: loopback only (`127.0.0.1:8101`), `HostCheck`, the bearer token on every
  `/api/` path (token in `~/Library/Application Support/PRWatch/access-token`), reached from the iPhone through
  `tailscale serve` (`--TailnetHost`). Never listen on `0.0.0.0` or a LAN address.
- **Safety rules** (reviewers enforce these; a change to any of them is high risk):
  - **What PRWatch writes:** only its own files in `~/Library/Application Support/PRWatch/` (settings, status,
    usage ledger, logs, and the update lock `.update.lock/prwatch-run` for the whole of every `claude` run: a
    folder made with libc `mkdir`, touched every 10 min, deleted only while it holds PRWatch's marker and never
    through a symlink; a lock without the marker is the updater's and is never changed) and the request files
    `factory/prs/<repo>-<n>/request.json` (atomic write: temp file in the same folder, then rename; never through a
    symlink; written relative to no-follow folder handles). It never writes job records, backlogs, reviews, comments
    or any file in a project repo or the factory root.
  - **Processes:** PRWatch runs exactly two programs, each with a fixed `ProcessStartInfo.ArgumentList`, never
    through a shell, never with an argument taken from a request except a PR number (digits only, checked) and a
    project name (matched against the discovered project list):
    1. `python3 factory/tools/job.py pr poll` (cwd the factory root) to read the PRs. Every git and gh call
       stays inside `job.py pr`; PRWatch itself never runs `git` or `gh`.
    2. `claude -p "/factory pr --headless --project <P> <n>"` with the flags measured by factory job 408 and
       narrowed by job 412 (`factory/tools/prwatch_smoke.py`: `claude_argv`, `ALLOWED_TOOLS`,
       `DISALLOWED_TOOLS`, `HEADLESS`): `--model <reviewer mode>`, `--output-format stream-json --verbose`,
       `--no-session-persistence`, `--permission-mode dontAsk`, `--permission-prompts none`,
       `--max-budget-usd <cap>`, `--append-system-prompt <HEADLESS>`, `--add-dir <prs dir>`, `--allowedTools …`,
       `--disallowedTools …`. Keep PRWatch's lists byte-equal to `prwatch_smoke.py`'s; a test pins them. Never
       `--dangerously-skip-permissions`, `--allow-dangerously-skip-permissions` or `--bare`.
  - **Environment of the `claude` run:** only `HOME`, `USER`, `TMPDIR`, `FACTORY_PRS_DIR`,
    `FACTORY_PR_HEADLESS=1` and `PATH` = ControlRoom's `FIXED_PATH`
    (`/usr/local/share/dotnet:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin`); cwd the factory
    root. Nothing else from PRWatch's own environment is passed on. The `claude` binary is an absolute path from
    the settings, checked at start (a missing or non-executable file shows "claude not found", no run).
  - **Consent and merges:** PRWatch never decides them in code. An untrusted author's tests run only after the
    user taps Consent (a `review` request with `consent: true` for the current `headSha`); an untrusted clean
    round merges only after a Merge tap (a `merge` request for that head). Every request names the `headSha` the
    user saw, so a push after the tap makes it stale (`job.py pr request` exits 8). Auto-merge for trusted authors
    is a setting, off by default.
  - **Untrusted text:** PR titles, authors, bodies and `claude` output are shown as text only (`textContent`,
    never `innerHTML`), capped in length, and never put in a URL, a log line, a file name or a command.
  - **Secrets:** the token, the Claude login and any `CLAUDE_CODE_OAUTH_TOKEN` are never logged, shown or cached
    by the service worker. PRWatch does not store a Claude credential; it uses the keychain login.
- **Watcher rules:** poll every 5 min (setting); start a review only for a PR whose `headSha` has had no new push
  for 10 min (setting) and whose round is missing or not `current` (a `review`, `merge` or `cancel` request from
  `POST /api/prs/<action>`, written by `RequestWriter`, skips the round, trust and same-head rules, but only once per tap: a request counts as no request and shows as `requestUnused` when the PR's last tap (`taps` in the status) named another head, or when its last run was handed a request and there is no tap or that run was for the same head and started no earlier than the tap); one run at a time in total, never while a
  factory job of that project is in `spec`, `build`, `review` or `approve` (read-only check of
  `factory/jobs/*/state.json`), and never while the off switch is set or the token brake holds. A run's exit and
  its stream-json `result` events are recorded in the status file.
- **Token brake:** read from the run's stream-json: `total_cost_usd` and `modelUsage` are cumulative (take the
  last), `rate_limit_event.unifiedWindows.five_hour.utilization` / `seven_day.utilization` with `resetsAt` are the
  plan usage. Stop starting runs when the 5-hour or weekly utilization passes its setting, when PRWatch's own
  rolling 5 h / 7 d cost sum passes its setting, after 3 runs in an hour, or at once on a rate limit until
  `resetsAt`. An auth error (`is_error` with an auth message) pauses every run as "needs login" with no retry
  loop; only a run whose result is `auth` sets "needs login" (a run killed as `timeout`, `output_too_large` or
  `stopped` never does, whatever its stream or stderr said); warn 7 days before `credential_expires_at`.
- **Layout:**
  - `PRWatch.slnx`, `Directory.Build.props` (nullable, implicit usings, code style in build), `.editorconfig`,
    `nuget.config` (RoomKit from GitHub Packages), `roomkit.sha256` (the pinned package's SHA-256), `README.md`.
  - `src/PRWatch/`: `Program.cs`, `PrWatchApp` (options and routes), the watcher, the `claude` runner, the brake
    (`TokenBrake`, `UsageLedger`), the request writer, the taps of a PR (`PrActions`, served by `GET /api/prs`),
    settings and status, the update lock (`UpdateLock`, shown as `updateLock` in `/api/status`); `wwwroot/`
    (`index.html`, `room.js`, `app.js`, `prwatch.css`, icons, manifest).
  - `tests/PRWatch.Tests/`: xUnit tests with a temporary factory root and fake `job.py` / `claude` scripts.
    `FakeJobPy` writes a `job.py` that prints `poll-output.json` (exit `poll-exit`, pause `poll-sleep`; while `poll-wait` exists it writes
    `poll-started` and blocks until `poll-release`) and logs its
    argv, cwd and environment to a calls file outside the root; `WatcherRig` builds a watcher on its stores and a
    `UsageLedger` (data folder) without a host; `FakeClock` timers fire on `Advance`; `FakeReviewRunner` stands in for the runner
    (`PrWatchFactory.TestServices`; like the real runner it writes the run start to the status). `FakeClaude`
    writes an executable `python3` fake `claude` in the test's folder that logs argv, cwd and environment, prints
    `stream.jsonl` (`#wait` blocks until a `release` file, `#flood` floods, `#orphan` double-forks a grandchild in
    a new session that keeps stdout and stderr open, writes `orphan-pid` and sleeps until a `release-orphan`
    file), writes `stderr.txt` and exits with `exit-code`; tests supply the environment through the runners'
    test-only `ReadVariable` hook and never write the process environment. The poll timeout is the config value `PollTimeoutSeconds` (default 120), the run timeout
    `RunTimeoutSeconds` (default 3600).
  - The runner is `ClaudeReviewRunner` (argv in `ClaudeArgs`, pinned by `ClaudeArgsTests` to `prwatch_smoke.py`);
    a failure after its process started (before the monitor runs) kills the run with its tree, releases the lock and
    records `run_failed` (test-only hooks `AfterProcessStart` and `StatusStore.BeforeSave`).
  - `scripts/restore.sh` (0755, PRWatch's own): the local restore with the PAT scoped to it (`RestoreScriptTests`).
  - `scripts/launch-agent.sh`, `scripts/updater.sh` (0755): RoomKit's copies of the pinned package; change only
    the block between `# roomkit:app-begin` and `# roomkit:app-end` (`ScriptTests` pins both). Install at login
    with auto-update, from `PRWatch/`: `scripts/launch-agent.sh install --release [--tailnet-host <host>]`. The
    updater ends `busy` while a review run holds `.update.lock`.
  - `.github/workflows/`: CI on every push and PR, release on a merged `release vX.Y.Z` PR into `main`.
- **Style:** C# like ControlRoom: file-scoped namespaces, `internal sealed` unless a type must be public, no
  primary constructors, no `var`, braces on every body, camelCase private fields without `_`; 0 warnings. The web
  app is mobile first (iPhone width), works in light and dark mode and shows fixed error texts, never exception
  messages.
- **Git:** `develop` is the working branch: factory work branches off it as `factory/<job-id>` and is merged back with `--no-ff`. `main` holds releases only and is pushed to only for a new release.
