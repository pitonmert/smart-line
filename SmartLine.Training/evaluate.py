"""Read metrics that a real Ultralytics run already wrote.

If results.csv is absent, this command exits with an error. It does not fill in
accuracy, mAP, precision, or recall.
"""

from __future__ import annotations

import argparse
import csv
import json
import sys
from pathlib import Path


def read_results(path: Path) -> dict:
    if not path.is_file():
        raise FileNotFoundError(
            f"No results.csv at {path}. A training run has not produced metrics, so none are reported."
        )
    with path.open(encoding="utf-8", newline="") as handle:
        rows = list(csv.DictReader(handle))
    if not rows:
        raise ValueError(f"{path} has a header but no metric rows.")
    last = {key.strip(): value.strip() for key, value in rows[-1].items() if key}
    return {"rows": len(rows), "last": last, "source": str(path)}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Print metrics from an existing Ultralytics results.csv.")
    parser.add_argument("--results", required=True, type=Path)
    args = parser.parse_args(argv)
    try:
        report = read_results(args.results)
    except (FileNotFoundError, ValueError) as exc:
        print(str(exc))
        return 2
    print(json.dumps(report, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
