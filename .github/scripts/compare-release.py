"""Compares two PRWatch release tarballs member by member, without extracting anything.

Usage: python3 -I .github/scripts/compare-release.py <built.tar.gz> <downloaded.tar.gz>

Exits 0 only when both archives hold the same member names under one top folder, each a regular file or a
directory, with the same type, the same owner-execute bit and the same bytes. The one exception is
PRWatch.staticwebassets.endpoints.json in the top folder: its two JSON documents must be equal once every
ResponseHeaders entry named Last-Modified has its value replaced, and each replaced value must be an HTTP date.

Exits 1 with one fixed line on stderr (naming the member, escaped, never its contents) on any difference, on a
symlink, hard link, device or other member type, on an absolute name, a ".." or "." component, a name outside the
one top folder or a duplicate name, and on an unreadable archive; exits 2 on a wrong argument count. The archives
come from a build job that is not trusted, so the script only reads them: no extraction, no link is followed.
"""

import json
import re
import sys
import tarfile
import zlib

ENDPOINTS = "PRWatch.staticwebassets.endpoints.json"
# fullmatch with ASCII digits: "$" alone would also accept a trailing newline, "\d" alone any Unicode digit.
HTTP_DATE = re.compile(r"^[A-Z][a-z]{2}, \d{2} [A-Z][a-z]{2} \d{4} \d{2}:\d{2}:\d{2} GMT$", re.ASCII)
PLACEHOLDER = "Last-Modified"
CHUNK = 1 << 16
MAX_JSON = 64 << 20
MAX_SHOWN = 200


class Mismatch(Exception):
    """One failure: its message is the whole stderr line."""


def shown(name):
    """A member name as one safe line: JSON-escaped (no control characters), capped in length."""
    text = json.dumps(name, ensure_ascii=True)
    if len(text) > MAX_SHOWN:
        text = text[:MAX_SHOWN] + "..."
    return text


def members(archive, label):
    """The checked members of one open archive, by name, and the top folder."""
    found = {}
    top = None
    for member in archive:
        name = member.name
        if member.isdir():
            name = name.rstrip("/")
        if name.startswith("/"):
            raise Mismatch(f"{label} has a member with an absolute name: {shown(name)}")
        parts = name.split("/")
        if ".." in parts:
            raise Mismatch(f"{label} has a member with a .. component: {shown(name)}")
        if "" in parts or "." in parts:
            raise Mismatch(f"{label} has a member with an empty or . component: {shown(name)}")
        if top is None:
            top = parts[0]
        elif parts[0] != top:
            raise Mismatch(f"{label} has a member outside the top folder: {shown(name)}")
        if member.issym():
            raise Mismatch(f"{label} has a symlink: {shown(name)}")
        if member.islnk() or member.ischr() or member.isblk() or member.isfifo():
            raise Mismatch(f"{label} has a hard link or device: {shown(name)}")
        if member.issparse() or not (member.isreg() or member.isdir()):
            raise Mismatch(f"{label} has a member that is neither a file nor a directory: {shown(name)}")
        if len(parts) == 1 and not member.isdir():
            raise Mismatch(f"{label} has a top member that is not a folder: {shown(name)}")
        if name in found:
            raise Mismatch(f"{label} has a duplicate member: {shown(name)}")
        found[name] = member
    if top is None:
        raise Mismatch(f"{label} has no members")
    return found, top


def same_bytes(left, right):
    """True when both streams hold the same bytes, read in chunks."""
    while True:
        a = left.read(CHUNK)
        b = right.read(CHUNK)
        if a != b:
            return False
        if not a:
            return True


def no_duplicate_keys(pairs):
    """A JSON object hook that refuses a key given twice."""
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate key")
        result[key] = value
    return result


def no_constant(_):
    """A JSON constant hook that refuses NaN, Infinity and -Infinity."""
    raise ValueError("non-standard constant")


def normalised(stream, name, label):
    """The endpoints document as canonical text, every Last-Modified header value checked and replaced."""
    data = stream.read(MAX_JSON + 1)
    if len(data) > MAX_JSON:
        raise Mismatch(f"{label} has an endpoints file that is too large: {shown(name)}")
    try:
        document = json.loads(data.decode("utf-8"), object_pairs_hook=no_duplicate_keys, parse_constant=no_constant)
    except (ValueError, RecursionError):
        raise Mismatch(f"{label} has an endpoints file that is not valid JSON: {shown(name)}") from None

    def walk(node):
        if isinstance(node, dict):
            headers = node.get("ResponseHeaders")
            if isinstance(headers, list):
                for header in headers:
                    if isinstance(header, dict) and header.get("Name") == "Last-Modified":
                        value = header.get("Value")
                        if not isinstance(value, str) or not HTTP_DATE.fullmatch(value):
                            raise Mismatch(f"{label} has a malformed Last-Modified value in: {shown(name)}")
                        header["Value"] = PLACEHOLDER
            for value in node.values():
                walk(value)
        elif isinstance(node, list):
            for value in node:
                walk(value)

    try:
        walk(document)
        return json.dumps(document, ensure_ascii=True, separators=(",", ":"))
    except RecursionError:
        raise Mismatch(f"{label} has an endpoints file that is too deep: {shown(name)}") from None


def compare(built_path, downloaded_path):
    archives = []
    try:
        for path, label in ((built_path, "built"), (downloaded_path, "downloaded")):
            try:
                archive = tarfile.open(path, mode="r:gz")
            except (tarfile.TarError, OSError, EOFError, zlib.error):
                raise Mismatch(f"cannot read the {label} archive") from None
            archives.append(archive)
        built, downloaded = archives
        try:
            built_members, top = members(built, "built")
            downloaded_members, _ = members(downloaded, "downloaded")
        except (tarfile.TarError, OSError, EOFError, zlib.error):
            raise Mismatch("cannot read an archive") from None

        for name in sorted(built_members):
            if name not in downloaded_members:
                raise Mismatch(f"member missing from downloaded: {shown(name)}")
        for name in sorted(downloaded_members):
            if name not in built_members:
                raise Mismatch(f"extra member in downloaded: {shown(name)}")

        endpoints = top + "/" + ENDPOINTS
        for name in sorted(built_members):
            left = built_members[name]
            right = downloaded_members[name]
            if left.isdir() != right.isdir():
                raise Mismatch(f"member type differs: {shown(name)}")
            if (left.mode & 0o100) != (right.mode & 0o100):
                raise Mismatch(f"exec bit differs: {shown(name)}")
            if left.isdir():
                continue
            try:
                left_stream = built.extractfile(left)
                right_stream = downloaded.extractfile(right)
                if left_stream is None or right_stream is None:
                    raise Mismatch(f"cannot read member: {shown(name)}")
                if name == endpoints:
                    if normalised(left_stream, name, "built") != normalised(right_stream, name, "downloaded"):
                        raise Mismatch(f"endpoints file differs outside Last-Modified: {shown(name)}")
                elif not same_bytes(left_stream, right_stream):
                    raise Mismatch(f"file bytes differ: {shown(name)}")
            except (tarfile.TarError, OSError, EOFError, zlib.error):
                raise Mismatch(f"cannot read member: {shown(name)}") from None
    finally:
        for archive in archives:
            archive.close()


def main(argv):
    if len(argv) != 3:
        print("usage: compare-release.py <built.tar.gz> <downloaded.tar.gz>", file=sys.stderr)
        return 2
    try:
        compare(argv[1], argv[2])
    except Mismatch as error:
        print(str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
