# PRWatch

A small app on the Mac that watches the open pull requests of the factory and its projects, has them reviewed by
the factory (`/factory pr --headless`) and shows them in a web app on the Mac and the iPhone. The conventions and
safety rules are in `CLAUDE.md`.

Build and test from this folder with `dotnet test` (after the RoomKit setup below).

## RoomKit

PRWatch is built on the shared library RoomKit from the repo `nothisisnotdaniel/roomkit`. It ships as the NuGet
package `RoomKit` on GitHub Packages (`https://nuget.pkg.github.com/nothisisnotdaniel/index.json`). See
[RoomKit's docs](https://github.com/nothisisnotdaniel/roomkit/blob/main/docs/roomkit.md) for the API and the scripts.

`nuget.config` takes `RoomKit` only from the source `github` (that feed) and every other package only from
nuget.org. GitHub Packages needs a sign-in even to read, so set it up once on each Mac that builds PRWatch:

1. Create a classic personal access token on GitHub with only the `read:packages` scope.
2. Set it as an environment variable in your shell profile (for example `~/.zprofile`). It is never committed:

   ```sh
   export 'NuGetPackageSourceCredentials_github=Username=<user>;Password=<token>'
   ```

   `<user>` is your GitHub user name, `<token>` the token. Don't put it in `nuget.config` or any other file of
   this repo.
3. Open a new terminal and run `dotnet restore`.

CI does not need a token of yours: it uses `GITHUB_TOKEN` only on the `Restore` steps, in jobs that have just
`contents: read` and `packages: read`. The step after it, "Check the RoomKit package", checks the restored package
against `roomkit.sha256` and its build-provenance attestation from roomkit's `release.yml` on `main` before anything
is built.

The version is pinned exactly in `src/PRWatch/PRWatch.csproj` (`Version="[x.y.z]"`). A version bump changes the
csproj and `roomkit.sha256` in the same commit, and refreshes `scripts/launch-agent.sh` and `scripts/updater.sh`
from the new package (only the block between `# roomkit:app-begin` and `# roomkit:app-end` is PRWatch's).
