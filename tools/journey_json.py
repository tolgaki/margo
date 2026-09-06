"""Strict, bounded JSON input shared by the development-only journey tools."""

import json
from pathlib import Path


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False)


def load(path, error_type=ValueError):
    def pairs(values):
        result = {}
        for key, value in values:
            if key in result:
                raise error_type("duplicate JSON field: " + key)
            result[key] = value
        return result

    def nonfinite(value):
        raise error_type("non-finite JSON number: " + value)

    try:
        with Path(path).open(encoding="utf-8") as stream:
            content = stream.read(4 * 1024 * 1024 + 1)
        if len(content) > 4 * 1024 * 1024:
            raise error_type("journey JSON input exceeds 4 MiB of text")
        return json.loads(content, object_pairs_hook=pairs, parse_constant=nonfinite)
    except (OSError, UnicodeError, json.JSONDecodeError, RecursionError) as exc:
        raise error_type("cannot read valid bounded JSON from %s: %s" % (path, exc)) from exc
