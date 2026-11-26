# 033 Tighten the RoomKit attestation check against same-repo PR attestations

Category: security

## What

PRWatch's CI pins the signer workflow, `--source-ref refs/heads/main` and GitHub-hosted runners when it verifies the RoomKit attestation. Its strength still depends on ControlRoom's release.yml never minting attestations for same-repo PR branches. Pin the trigger or the OIDC issuer (`--cert-oidc-issuer`), or document the limit in ControlRoom/docs/roomkit.md. Recheck this once job 429 has moved RoomKit to its own repo.

## Why

Review-security 1 of job 430: provenance that a PR branch could also produce is weaker than it looks.

## Notes

Proposed by job 430-roomkit-signature-ci.0c91bd8ba359453e94f1e4c8273d544b (`factory/jobs/430-roomkit-signature-ci.0c91bd8ba359453e94f1e4c8273d544b/`).
