# 034 Switch PRWatch to RoomKit 0.1.0 from the roomkit repo

As the user, I want PRWatch to build on the RoomKit package from its own repo, like ControlRoom now does, so that PRWatch gets RoomKit's fixes and no longer depends on an old ControlRoom release.

Category: feature

## What

Job 429 moved RoomKit to `nothisisnotdaniel/roomkit` (first release v0.1.0, published to GitHub Packages) and ControlRoom now restores it from there. PRWatch still pins `RoomKit [0.3.0]` from ControlRoom's release through a local folder feed. Switch PRWatch to `RoomKit 0.1.0` from GitHub Packages (`nuget.pkg.github.com/nothisisnotdaniel`), the same way ControlRoom does: `nuget.config` with `<clear />` and package source mapping (RoomKit only from GitHub Packages, everything else only from nuget.org), no credentials in the repo, a `read:packages` PAT locally through `NuGetPackageSourceCredentials_github`, and `packages: read` with `GITHUB_TOKEN` only on the CI and release steps that restore. Keep a content pin (job 430's `roomkit.sha256`, or `packages.lock.json` with locked-mode restore) and replace job 430's CI attestation check with one against roomkit's `release.yml` (signer workflow, `--source-ref refs/heads/main`, no self-hosted runners), tightened as in #33: pin the trigger or the OIDC issuer so attestations from same-repo PR branches can't pass. Refresh `scripts/launch-agent.sh` and `scripts/updater.sh` from the new package (only the app block stays PRWatch's), fix any API changes between 0.3.0 and 0.1.0, and update `PRWatch/CLAUDE.md` and the README (they still point at the deleted `ControlRoom/docs/roomkit.md`).

## Why

ControlRoom no longer releases RoomKit, so PRWatch is stuck on 0.3.0 without fixes, and job 430's check pins the old ControlRoom signer. The user chose GitHub Packages on 26 Nov 2026 so both apps restore RoomKit the same way, and asked to fold #33 into this item.

## Notes

Decided by the user on 26 Nov 2026: package source GitHub Packages (not the release download); #33 is folded in here and removed. High risk: it changes a dependency source and CI token permissions.
