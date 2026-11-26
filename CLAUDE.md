# PRWatch — conventions

Every factory agent reads this file before working on PRWatch. Keep it short and accurate.

- **What it is:** a small app on the user's Mac that watches the open pull requests of the factory and its
  projects and has them reviewed by the factory without anyone at the keyboard. It polls with `job.py pr poll`
  (no tokens while idle), starts one headless `claude -p "/factory pr --headless --project <P> <n>"` run per new or
  changed PR, merges clean rounds of trusted authors (write access) through that run, and shows everything in a
  web app (Mac browser and iPhone home-screen PWA) with the Consent and Merge taps for untrusted authors. It
  starts at login. It is the **only** program in the factory allowed to start `claude` on its own, and only for
  `/factory pr` reviews (the user's rule of 22 Nov 2026): never add any other kind of run, schedule or job.
- **Stack:** .NET 10 ASP.NET Core minimal API on the **RoomKit core only** (the `RoomKit` NuGet package from
  ControlRoom's releases, restored from the local feed `~/Library/Application Support/RoomKit/feed` named in
  `nuget.config`; see `ControlRoom/docs/roomkit.md`). No `FactoryModule`, no `IJobFormat`: `Program.cs` is
  `return RoomKitApp.Run(args, PrWatchApp.Options, PrWatchApp.Map);`. Plain HTML/CSS/JS in `wwwroot` (no CDN, no
  build step, strict CSP), xUnit tests with `Microsoft.AspNetCore.Mvc.Testing`. No other package without a reason
  in the spec.
- **Names:** `AppName` "PRWatch", `ShortName` "PRWatch", `DataFolderName` "PRWatch", `SolutionFileName`
  `PRWatch.slnx`, `DefaultPort` **8101** (ControlRoom is 8100), `ReleaseRepository` `nothisisnotdaniel/prwatch`
  (the GitHub repo is still to be created, see the backlog).
- **Test command:** `dotnet test` (from `PRWatch/`; `dotnet build` must stay at 0 warnings). Tests never read the
  real factory root, never start a real `claude`, `gh` or `git` against the network and never open a real port:
  they build a temporary factory root with a fake `factory/tools/job.py` and a fake `claude` script on a test
  `PATH`, and host the app in memory. The in-memory harness is `tests/PRWatch.Tests/PrWatchFactory.cs` (own temp
  folder, `--TokenFile=<temp>/PRWatch/access-token`, `--environment=Production`, `Host: 127.0.0.1:8101`).
  Before the first build on a Mac, fill the feed once with the `gh release download` command from
  `ControlRoom/docs/roomkit.md` for the version pinned in `src/PRWatch/PRWatch.csproj` (`Version="[x.y.z]"`; CI
  reads it there and checks the package against the release's `SHA256SUMS`).
- **Access:** as RoomKit gives it: loopback only (`127.0.0.1:8101`), `HostCheck`, the bearer token on every
  `/api/` path (token in `~/Library/Application Support/PRWatch/access-token`), reached from the iPhone through
  `tailscale serve` (`--TailnetHost`). Never listen on `0.0.0.0` or a LAN address.
- **Safety rules** (reviewers enforce these; a change to any of them is high risk):
  - **What PRWatch writes:** only its own files in `~/Library/Application Support/PRWatch/` (settings, status,
    usage ledger, logs) and the request files `factory/prs/<repo>-<n>/request.json` (atomic write: temp file in the
    same folder, then rename; never through a symlink). It never writes job records, backlogs, reviews, comments
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
  for 10 min (setting) and whose round is missing or not `current`; one run at a time in total, never while a
  factory job of that project is in `spec`, `build`, `review` or `approve` (read-only check of
  `factory/jobs/*/state.json`), and never while the off switch is set or the token brake holds. A run's exit and
  its stream-json `result` events are recorded in the status file.
- **Token brake:** read from the run's stream-json: `total_cost_usd` and `modelUsage` are cumulative (take the
  last), `rate_limit_event.unifiedWindows.five_hour.utilization` / `seven_day.utilization` with `resetsAt` are the
  plan usage. Stop starting runs when the 5-hour or weekly utilization passes its setting, when PRWatch's own
  rolling 5 h / 7 d cost sum passes its setting, after 3 runs in an hour, or at once on a rate limit until
  `resetsAt`. An auth error (`is_error` with an auth message) pauses every run as "needs login" with no retry
  loop; warn 7 days before `credential_expires_at`.
- **Layout:**
  - `PRWatch.slnx`, `Directory.Build.props` (nullable, implicit usings, code style in build), `.editorconfig`,
    `nuget.config` (the RoomKit feed).
  - `src/PRWatch/`: `Program.cs`, `PrWatchApp` (options and routes), the watcher, the `claude` runner, the brake,
    the request writer, settings and status; `wwwroot/` (`index.html`, `room.js`, `app.js`, icons, manifest).
  - `tests/PRWatch.Tests/`: xUnit tests with a temporary factory root and fake `job.py` / `claude` scripts.
  - `scripts/launch-agent.sh`, `scripts/updater.sh`: RoomKit's copies; change only the block between
    `# roomkit:app-begin` and `# roomkit:app-end`.
  - `.github/workflows/`: CI on every push and PR, release on a merged `release vX.Y.Z` PR into `main`.
- **Style:** C# like ControlRoom: file-scoped namespaces, `internal sealed` unless a type must be public, no
  primary constructors, no `var`, braces on every body, camelCase private fields without `_`; 0 warnings. The web
  app is mobile first (iPhone width), works in light and dark mode and shows fixed error texts, never exception
  messages.
- **Git:** `develop` is the working branch: factory work branches off it as `factory/<job-id>` and is merged back with `--no-ff`. `main` holds releases only and is pushed to only for a new release.
