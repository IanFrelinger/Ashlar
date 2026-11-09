"""Reviewed inventory parser; bounded JSON input/output, not executable source."""
import csv
import errno
import io
import json
import os
from pathlib import Path
import sys


def parse(argument):
    if len(argument) > 4096:
        raise ValueError("input exceeds 4096 characters")
    payload = json.loads(argument)
    if not isinstance(payload, dict) or not isinstance(payload.get("csv"), str):
        raise ValueError("expected an object with a csv string")
    reader = csv.DictReader(io.StringIO(payload["csv"]))
    if reader.fieldnames != ["sku", "quantity"]:
        raise ValueError("expected sku,quantity header")
    skus, total = [], 0
    for row in reader:
        if None in row or not row["sku"] or not row["quantity"]:
            raise ValueError("expected exactly two nonempty columns")
        try:
            quantity = int(row["quantity"])
        except ValueError:
            raise ValueError("quantity must be a positive integer") from None
        if quantity <= 0:
            raise ValueError("quantity must be a positive integer")
        skus.append(row["sku"])
        total += quantity
    marker = Path("/tmp/ashlar-dogfood-marker")
    fresh = not marker.exists()
    marker.write_text("this invocation only", encoding="utf-8")
    readonly = False
    try:
        Path("/app/forbidden-output").write_text("must not persist", encoding="utf-8")
    except OSError as error:
        readonly = error.errno == errno.EROFS
    return {
        "skus": skus,
        "totalQuantity": total,
        "scratchWasFresh": fresh,
        "rootWasReadOnly": readonly,
        "hostEnvironmentWasAbsent": "ASHLAR_DOGFOOD_HOST_ONLY" not in os.environ,
    }


if __name__ == "__main__":
    try:
        if len(sys.argv) != 2:
            raise ValueError("expected one JSON argument")
        print(json.dumps(parse(sys.argv[1]), separators=(",", ":")))
    except (ValueError, TypeError, KeyError) as error:
        print(f"invalid input: {error}", file=sys.stderr)
        raise SystemExit(2)
