# 048 Say when restore.sh can't reach the keychain, not only that the prompt was denied

As the user, I want restore.sh to tell a denied keychain prompt apart from a locked or unreachable keychain, so that I know whether to unlock it or run the restore from a GUI session.

Category: tidy-up

## What

scripts/restore.sh gives the same "prompt denied?" message (exit 1, no fallback to the 0600 file) for a denied or cancelled prompt and for a locked keychain or one with no GUI session, as under SSH. Add a hint such as "or the keychain is locked / no GUI session", or tell the cases apart by the `security` exit status, and update the test that pins the text (AC-4 of job 463). Keep the fail-closed behaviour.

## Why

Review-security 1 of job 463: the shared message is safe but leaves the user guessing why the restore failed.

## Notes

Proposed by job 463-keychain-packages-token.4e560a74f8f841ecb20dc079832e8f2d.

Proposed by job 463-keychain-packages-token.4e560a74f8f841ecb20dc079832e8f2d (`factory/jobs/463-keychain-packages-token.4e560a74f8f841ecb20dc079832e8f2d/`).
