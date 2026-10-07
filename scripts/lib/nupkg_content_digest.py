"""One definition of "the same package", used by both sides of the post-push check.

WHY THIS EXISTS. The release workflow hashes each .nupkg before pushing it, then downloads the
package nuget.org serves and compares. Those two byte streams are NEVER equal: nuget.org
repository-signs on publish, which appends a `.signature.p7s` entry to the archive. Measured on the
real feed - ashlar.sdk 0.1.2 as served contains `.signature.p7s` (12987 bytes) and hashes to
eb2111dc..., which is not the hash of the file that was pushed. A raw byte comparison therefore
cannot pass, and it runs AFTER `dotnet nuget push`, so it fails once the packages are already public
and unrecallable.

WHAT IS COMPARED INSTEAD. Every archive entry except the signature, by name and by uncompressed
content. That is the part nuget.org must not alter, and it is what a consumer actually restores.
Verified against the served package: signing appends `.signature.p7s` and touches nothing else -
`[Content_Types].xml` does not even declare the p7s extension, so the rest of the archive is
untouched.

Deliberately NOT compared: the zip container itself (entry order, compression, timestamps). Those
are re-encoded by any tool that adds an entry and carry no promise to a consumer.

Both the manifest writer and the post-push verifier import this, so the two sides cannot drift into
computing different things and agreeing anyway.
"""

from __future__ import annotations

import hashlib
import zipfile

# The repository signature nuget.org appends on publish. Excluded because its PRESENCE is the
# expected difference; a package that arrives without it is caught by signature verification, not by
# a content digest.
SIGNATURE_ENTRY = ".signature.p7s"


def frame_entry(name: str, digest: bytes) -> bytes:
    """One entry's contribution to the digest stream, length-prefixed.

    The prefix makes the stream prefix-free. Without it, entry NAMES run straight into the
    fixed-width digest that follows, and two different entry sets can concatenate to the same bytes
    - ({"a": X}, {"bc": Y}) against ({"ab": X'}, {"c": Y'}) is such a pair whenever the digests line
    up. That is not constructible against real SHA-256 content, which is why this is framed here as
    a pure function: the property is asserted directly on chosen bytes rather than left as a claim
    no test can reach.
    """
    encoded = name.encode("utf-8")
    return len(encoded).to_bytes(8, "big") + encoded + digest


def content_digest(nupkg_path: str) -> str:
    """SHA-256 over every entry except the repository signature, by name and uncompressed content."""
    outer = hashlib.sha256()
    with zipfile.ZipFile(nupkg_path) as archive:
        names = sorted(n for n in archive.namelist() if n != SIGNATURE_ENTRY)
        for name in names:
            with archive.open(name) as entry:
                inner = hashlib.sha256()
                for chunk in iter(lambda: entry.read(1024 * 1024), b""):
                    inner.update(chunk)
            outer.update(frame_entry(name, inner.digest()))
    return outer.hexdigest()


def has_signature(nupkg_path: str) -> bool:
    with zipfile.ZipFile(nupkg_path) as archive:
        return SIGNATURE_ENTRY in archive.namelist()
