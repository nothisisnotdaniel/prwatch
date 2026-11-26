# 036 Keep the stored GitHub Packages token behind a keychain prompt

As the user, I want the read:packages token that restore.sh uses to be read only after I confirm, so that untrusted PR test code running as me can't take it without asking.

Category: security

## What

README.md and scripts/restore.sh store the token with `security add-generic-password -s prwatch-github-packages -a <user> -w` without `-T`, so `/usr/bin/security` is on the item's ACL and any process of the same user (including untrusted PR test code from `/factory pr`) can run `security find-generic-password -s prwatch-github-packages -w` without a prompt. The 0600 file fallback has the same exposure. Suggest `-T ''` in the README so the keychain asks on every read, and say in one line that a token in the 0600 file can still be read by any code running as the user.

## Why

Job 432's round-2 security review (nit 1): the env scoping works, but the README's reason ('untrusted PR code') reads as if the token were now safe from such code, and it is not.

## Notes

From job 432 round 2, review-security nit 1. Small risk: the token is read:packages only.
