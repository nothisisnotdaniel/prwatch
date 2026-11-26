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
2. Store it in your login keychain (the command asks for the token, so it stays out of your shell history):

   ```sh
   security add-generic-password -s prwatch-github-packages -a <user> -T '' -w
   ```

   `<user>` is your GitHub user name. With `-T ''` no program, not even `/usr/bin/security`, may read the token
   without asking you, so the keychain asks every time it is read: `scripts/restore.sh` shows a prompt, and you
   allow it once per run with "Allow", not "Always Allow" (that would let `security` read it silently again). If
   you stored the item without `-T ''`, delete it with
   `security delete-generic-password -s prwatch-github-packages` and add it again with the command above.

   Or put the single line `<user>:<token>` into
   `~/Library/Application Support/PRWatch/github-packages` and `chmod 600` it. The token is never committed: don't
   put it in `nuget.config` or any other file of this repo. A token in the 0600 file can still be read by any code
   that runs as you, including `/factory pr` test runs of untrusted PR code, so the keychain is the safer choice.
3. Run `scripts/restore.sh` once, before `dotnet build` or `dotnet test`, and again after a RoomKit version bump.
   It sets `NuGetPackageSourceCredentials_github=Username=<user>;Password=<token>` for that one `dotnet restore`
   only. After that, build and test take RoomKit from `~/.nuget/packages` and need no token. After a successful
   restore it checks the SHA-256 of the restored `roomkit.<v>.nupkg` against `roomkit.sha256` (not the attestation,
   which stays in CI); a missing or different package is removed from the NuGet packages folder and the script
   exits 4, so no build uses it.

Never export the PAT globally (for example `export NuGetPackageSourceCredentials_github=...` in `~/.zprofile`):
every process started from that shell would inherit it, including the factory's `/factory pr` test runs of
untrusted PR code.

CI does not need a token of yours: it uses `GITHUB_TOKEN` only on the `Restore` steps, in jobs that have just
`contents: read` and `packages: read`. The step after it, "Check the RoomKit package", checks the restored package
against `roomkit.sha256` and its build-provenance attestation from roomkit's `release.yml` on `main` before anything
is built.

The version is pinned exactly in `src/PRWatch/PRWatch.csproj` (`Version="[x.y.z]"`). A version bump changes the
csproj and `roomkit.sha256` in the same commit, and refreshes `scripts/launch-agent.sh` and `scripts/updater.sh`
from the new package (only the block between `# roomkit:app-begin` and `# roomkit:app-end` is PRWatch's).
