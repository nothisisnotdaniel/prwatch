#!/bin/sh
# Restores PRWatch's packages with the GitHub Packages token set for this one `dotnet restore` only, so that no
# other process (a shell, the factory's /factory pr test runs of untrusted PR code) ever inherits it. macOS only.
#   scripts/restore.sh [<dotnet restore argument> ...]
# Run it once before the first `dotnet build` or `dotnet test`, and again after a RoomKit version bump: once the
# pinned RoomKit is in ~/.nuget/packages, build and test restore from there without any token.
# The token (a classic PAT with only read:packages) comes from, in this order:
#   1. the login keychain: a generic password with service SERVICE below, account <GitHub user>, password <token>
#      (security add-generic-password -s prwatch-github-packages -a <user> -T '' -w: with -T '' no program may read
#      it without asking, so every run shows a keychain prompt; choose "Allow", not "Always Allow"), or
#   2. the file ~/Library/Application Support/PRWatch/github-packages: one line <user>:<token>, a regular file (not
#      a symlink) of this user with mode 0600.
# A token in the 0600 file can still be read by any code that runs as you, including /factory pr test runs of
# untrusted PR code; the keychain item with -T '' is the safer choice.
# The user must be 1-39 of A-Z a-z 0-9 -, the token 1-255 of A-Z a-z 0-9 _, so neither can change the credential
# string. The script sets NuGetPackageSourceCredentials_github=Username=<user>;Password=<token> only in the
# environment of `dotnet restore` (run in the repo root) and exits with its code. It never prints, logs or exports
# the token. When `dotnet restore` succeeds, the script checks the restored RoomKit package as CI's "Check the
# RoomKit package" step does (without the attestation, which needs gh and a token): the version pinned in
# src/PRWatch/PRWatch.csproj (Version="[x.y.z]"), and the SHA-256 of
# ${NUGET_PACKAGES:-~/.nuget/packages}/roomkit/<v>/roomkit.<v>.nupkg against roomkit.sha256 (one line
# <hash>  RoomKit.<v>.nupkg). A package that is missing, a symlink or does not match is removed from the NuGet
# packages folder (rm -rf of roomkit/<v>, never followed through a symlink), so no build imports its build/*.props.
# Exit codes: 1 no token found or the keychain did not hand it out (prompt denied or cancelled; the token file is
# then not tried), 2 the token file or a value is refused, 4 the RoomKit check failed (no pin, package missing or
# not matching roomkit.sha256), else dotnet's (a failed restore is passed on without the check).

set -u
SERVICE="prwatch-github-packages"
TOKEN_FILE="$HOME/Library/Application Support/PRWatch/github-packages"

cd "$(dirname "$0")/.." || exit 1

if [ -n "${NuGetPackageSourceCredentials_github:-}" ]; then
    echo "NuGetPackageSourceCredentials_github is set in this shell; remove it from your shell profile. Using the keychain or file token instead." >&2
fi

user=""
token=""
if command -v security >/dev/null 2>&1 && security find-generic-password -s "$SERVICE" >/dev/null 2>&1; then
    user=$(security find-generic-password -s "$SERVICE" 2>/dev/null | sed -n 's/^ *"acct"<blob>="\(.*\)"$/\1/p')
    if ! token=$(security find-generic-password -s "$SERVICE" -w 2>/dev/null); then
        echo "The keychain did not hand out the token (prompt denied?)." >&2
        exit 1
    fi
elif [ -e "$TOKEN_FILE" ] || [ -L "$TOKEN_FILE" ]; then
    if [ -L "$TOKEN_FILE" ] || [ ! -f "$TOKEN_FILE" ]; then
        echo "The token file is not a regular file." >&2
        exit 2
    fi
    if [ "$(stat -f '%Lp %u' "$TOKEN_FILE")" != "600 $(id -u)" ]; then
        echo "The token file must be yours with mode 0600." >&2
        exit 2
    fi
    line=$(sed -n '1p' "$TOKEN_FILE")
    if [ "$(wc -l < "$TOKEN_FILE" | tr -d ' ')" -gt 1 ]; then
        echo "The token file must hold exactly one line <user>:<token>." >&2
        exit 2
    fi
    user=${line%%:*}
    token=${line#*:}
    if [ "$user" = "$line" ]; then
        echo "The token file must hold exactly one line <user>:<token>." >&2
        exit 2
    fi
else
    echo "No GitHub Packages token: add it to the keychain (security add-generic-password -s $SERVICE -a <user> -T '' -w) or to ~/Library/Application Support/PRWatch/github-packages (see README.md)." >&2
    exit 1
fi

case $user in
    '' | *[!A-Za-z0-9-]*)
        echo "The GitHub user name is refused." >&2
        exit 2
        ;;
esac
if [ ${#user} -gt 39 ]; then
    echo "The GitHub user name is refused." >&2
    exit 2
fi
case $token in
    '' | *[!A-Za-z0-9_]*)
        echo "The token is refused." >&2
        exit 2
        ;;
esac
if [ ${#token} -gt 255 ]; then
    echo "The token is refused." >&2
    exit 2
fi

NuGetPackageSourceCredentials_github="Username=$user;Password=$token" dotnet restore "$@"
code=$?
if [ "$code" -ne 0 ]; then
    exit "$code"
fi

# The pin regex allows only digits and dots, so the version folder below cannot leave the packages folder.
version=$(sed -n 's/.*<PackageReference Include="RoomKit" Version="\[\([0-9][0-9.]*\)\]".*/\1/p' src/PRWatch/PRWatch.csproj 2>/dev/null)
case $version in
    '' | *[!0-9.]*)
        echo "No pinned RoomKit version in src/PRWatch/PRWatch.csproj." >&2
        exit 4
        ;;
esac
folder="${NUGET_PACKAGES:-$HOME/.nuget/packages}/roomkit/$version"
package="$folder/roomkit.$version.nupkg"
if [ ! -f "$package" ] || [ -L "$package" ]; then
    rm -rf "$folder"
    echo "RoomKit.$version.nupkg is not in the restored packages." >&2
    exit 4
fi
actual=$(shasum -a 256 "$package" | awk '{ print $1 }')
pinned=""
if [ -f roomkit.sha256 ]; then
    pinned=$(awk -v name="RoomKit.$version.nupkg" 'NR == 1 && NF == 2 && $2 == name { hash = $1 } END { if (NR == 1) print hash }' roomkit.sha256)
fi
if [ -z "$pinned" ] || [ "$pinned" != "$actual" ]; then
    rm -rf "$folder"
    echo "RoomKit.$version.nupkg does not match roomkit.sha256; removed it from the NuGet packages folder." >&2
    exit 4
fi
