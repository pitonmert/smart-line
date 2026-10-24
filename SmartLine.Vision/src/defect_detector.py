"""Defect labels supplied to the recipe engine.

Pixel-level defect detection is not invented here. A trained model, or an operator
label during dataset capture, is the source of defect codes. This module only
normalizes those codes and refuses to mark an image defective without evidence.
"""

from __future__ import annotations


def normalize_defects(raw_labels: list[str] | None, known_codes: set[str] | None = None) -> list[str]:
    if not raw_labels:
        return []
    normalized = []
    seen = set()
    for label in raw_labels:
        code = str(label).strip().upper()
        if not code or code in seen:
            continue
        if known_codes is not None and code not in known_codes:
            continue
        seen.add(code)
        normalized.append(code)
    return normalized
