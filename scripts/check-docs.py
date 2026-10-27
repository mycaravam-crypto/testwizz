#!/usr/bin/env python3
"""Validate required docs and relative Markdown links without external networking."""
from pathlib import Path
import re
import sys

root = Path(__file__).resolve().parent.parent
required = [root / "README.md", root / "docs" / "architecture.md", root / "docs" / "getting-started.md", root / "docs" / "security.md", root / "docs" / "testing.md"]
errors = []
for doc in required:
    if not doc.is_file():
        errors.append(f"Missing required document: {doc.relative_to(root)}")
for doc in [p for p in (root / "docs").rglob("*.md")] + [root / "README.md"]:
    content = doc.read_text(encoding="utf-8")
    for dest in re.findall(r"(?<!!)\[[^\]]*\]\(([^)]+)\)", content):
        if dest.startswith(("https://", "http://", "mailto:", "#")):
            continue
        relative = dest.split("#", 1)[0].split("?", 1)[0]
        if relative and not (doc.parent / relative).exists():
            errors.append(f"{doc.relative_to(root)}: broken link {dest}")
if errors:
    print("\n".join(errors), file=sys.stderr)
    sys.exit(1)
print("Documentation links OK")
