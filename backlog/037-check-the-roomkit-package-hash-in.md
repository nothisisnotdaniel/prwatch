# 037 Check the RoomKit package hash in restore.sh

As the user, I want a local restore to check the RoomKit package the same way CI does, so that a local build never imports an unchecked package.

Category: security

## What

scripts/restore.sh runs `dotnet restore` but not CI's content check, so a local `dotnet build` imports RoomKit's `build/*.props` unchecked. After the restore, have restore.sh compute the sha256 of `roomkit.<v>.nupkg` in the NuGet packages folder (version from the csproj pin) and compare it with `roomkit.sha256`; on a mismatch, delete that package folder and exit non-zero with a fixed message. Add ScriptTests for match and mismatch.

## Why

Job 432's round-2 security review (nit 2): CI checks the pin and the attestation, but local restores don't. This was already true before job 432.

## Notes

From job 432 round 2, review-security nit 2.
