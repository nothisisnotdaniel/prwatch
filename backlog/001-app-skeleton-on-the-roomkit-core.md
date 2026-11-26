# 001 App skeleton on the RoomKit core

As the user, I want a PRWatch app that starts on 127.0.0.1:8101 with RoomKit's token gate and PWA shell, so that every later feature has a tested home.

Category: feature

## What

Create `PRWatch.slnx`, `Directory.Build.props`, `.editorconfig`, `nuget.config` (the local RoomKit feed), `src/PRWatch` with `Program.cs` = `return RoomKitApp.Run(args, PrWatchApp.Options, PrWatchApp.Map);` (AppName/ShortName/DataFolderName PRWatch, SolutionFileName PRWatch.slnx, DefaultPort 8101, ReleaseRepository nothisisnotdaniel/prwatch), a minimal `wwwroot` page that loads behind the token gate in light and dark mode, `tests/PRWatch.Tests` (in-memory host: health, version, token gate, HostCheck), and a CI workflow that restores RoomKit (from the ControlRoom release with `gh release download`) and runs `dotnet test` on every push and PR.

## Why

PRWatch is unbuilt; the watcher, runner and UI need a project, a test harness and CI first. RoomKit gives access control and the PWA for free, so nothing of that is written again.

## Notes

See `ControlRoom/docs/roomkit.md` for the package, the feed and the options. 0 warnings. No real port or factory root in tests.
