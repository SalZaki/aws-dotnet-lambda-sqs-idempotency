#!/usr/bin/env python3
"""Check every relative markdown link in this repository resolves to a file and an anchor.

    python3 scripts/check-doc-links.py [root]

The documentation is a set of cross-referenced documents rather than one file, and a link that no
longer resolves is invisible to a reader who never follows it and to markdownlint, which checks how
a link is written and not where it goes. A heading renamed in one document silently breaks every
citation of it in the other thirteen.

Anchors are the half worth automating. A link to a file that does not exist is at least a 404 a
reader reports; a link to a heading that has been reworded lands at the top of the right document,
looking like a link that works. Both are checked here, against the slugs GitHub derives from the
headings themselves.

External links are deliberately out of scope. Resolving them needs the network, which would make a
required check fail for a site that is down rather than for a change under review.
"""

import re
import sys
from pathlib import Path

# Fenced blocks are skipped when collecting headings: a `# comment` in a shell sample is not a
# heading, and treating it as one invents anchors that no document can link to.
FENCE = re.compile(r"^\s*(```|~~~)")
HEADING = re.compile(r"^(#{1,6})\s+(.*?)\s*#*\s*$")

# Inline links and images. The label is allowed one level of nesting so that a linked badge —
# `[![build](a.svg)](b.md)` — is read as the link it is rather than as the image inside it, and an
# optional title after the target is consumed rather than turning the whole link into no match. A
# link this pattern cannot read is a link that goes unchecked, which is the failure this script
# exists to prevent, so it errs towards matching.
LINK = re.compile(r"""!?\[((?:[^\[\]]|\[[^\[\]]*\])*)\]\(([^()\s]+)(?:\s+["'][^"']*["'])?\)""")

SKIP_DIRECTORIES = {".git", "node_modules", "bin", "obj", "cdk.out"}


def slug(heading: str) -> str:
    """Derive GitHub's anchor for a heading, which is what a link in these documents has to match."""
    text = re.sub(r"\[([^\]]*)\]\([^()]*\)", r"\1", heading)
    text = text.replace("`", "").replace("*", "")
    text = re.sub(r"[^\w\- ]", "", text, flags=re.UNICODE)
    return text.strip().lower().replace(" ", "-")


def anchors(path: Path) -> set[str]:
    """Every anchor a document offers, with GitHub's -1, -2 suffixes for repeated headings."""
    found: dict[str, int] = {}
    fenced = False

    for line in path.read_text(encoding="utf-8").splitlines():
        if FENCE.match(line):
            fenced = not fenced
            continue
        if fenced:
            continue

        match = HEADING.match(line)
        if not match:
            continue

        base = slug(match.group(2))
        seen = found.get(base, 0)
        found[base] = seen + 1

    return {name if index == 0 else f"{name}-{index}" for name, count in found.items() for index in range(count)}


def without_fences(text: str) -> str:
    """The document with its fenced blocks blanked out, every other character left where it was.

    Blanked rather than removed because the offsets that survive are what a failure is reported by.
    A sample inside a fence shows how a link is written, and the file it names need not exist.
    """
    kept = []
    fenced = False

    for line in text.splitlines():
        if FENCE.match(line):
            fenced = not fenced
            kept.append("")
            continue

        kept.append("" if fenced else line)

    return "\n".join(kept)


def targets(text: str, offset: int = 0) -> list[tuple[int, str]]:
    """Every link target in a fragment, with where it starts, including those inside a label."""
    found = []

    for match in LINK.finditer(text):
        found.append((offset + match.start(2), match.group(2)))
        found.extend(targets(match.group(1), offset + match.start(1)))

    return found


def links(path: Path) -> list[tuple[int, str]]:
    """Every inline link in a document, with the line its target is written on.

    Matched against the whole document rather than line by line. Prose here is hard-wrapped at 100
    columns, so a citation's label routinely breaks across two lines — `[Cost\nModel](cost-model.md)`
    — which is one link on the page and two fragments to a line scan. Twenty-five links in this
    repository were written that way when this was fixed, and every one of them was a cross-document
    heading citation: precisely what the checker exists to protect, passing silently.
    """
    text = without_fences(path.read_text(encoding="utf-8"))

    return [(text.count("\n", 0, position) + 1, target) for position, target in targets(text)]


def documents(root: Path) -> list[Path]:
    return sorted(
        path
        for path in root.rglob("*.md")
        if not SKIP_DIRECTORIES.intersection(path.relative_to(root).parts)
    )


def check(root: Path) -> list[str]:
    cache: dict[Path, set[str]] = {}
    broken = []

    for document in documents(root):
        for number, target in links(document):
            if target.startswith(("http://", "https://", "mailto:")):
                continue

            path, _, anchor = target.partition("#")
            where = f"{document.relative_to(root)}:{number}"

            if not path:
                destination = document
            else:
                destination = (document.parent / path).resolve()
                if not destination.exists():
                    broken.append(f"{where}: no such file: {target}")
                    continue
                if destination.is_dir():
                    if anchor:
                        broken.append(f"{where}: anchor on a directory: {target}")
                    continue

            if not anchor:
                continue

            if destination.suffix != ".md":
                broken.append(f"{where}: anchor on a file that has no headings: {target}")
                continue

            if destination not in cache:
                cache[destination] = anchors(destination)

            # Compared as written. GitHub's anchors are lower case and a browser matches a
            # fragment exactly, so `#Tracing-Specification` scrolls nowhere while looking correct.
            if anchor not in cache[destination]:
                broken.append(f"{where}: no such heading: {target}")

    return broken


def main(root: Path) -> int:
    broken = check(root)

    if broken:
        print(f"{len(broken)} link(s) resolve to nothing:\n", file=sys.stderr)
        for failure in broken:
            print(f"  {failure}", file=sys.stderr)
        return 1

    print(f"Every relative link in {len(documents(root))} document(s) resolves.")
    return 0


if __name__ == "__main__":
    sys.exit(main(Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()))
