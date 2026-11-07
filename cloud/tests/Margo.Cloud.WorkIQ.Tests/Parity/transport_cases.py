"""Record what the Python Work IQ transport does with each response shape, for the C# port to match.

    python3 cloud/tests/Margo.Cloud.WorkIQ.Tests/Parity/transport_cases.py --write   # regenerate
    python3 cloud/tests/Margo.Cloud.WorkIQ.Tests/Parity/transport_cases.py --check   # fail if stale

The inputs are below; the expected outcomes come from running ``margo_autopilot.transport``'s own
``_parse_body`` and ``_normalise``. ``TransportParityTests`` replays every case against the C# port.
Fictional data only.
"""

import argparse
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
sys.path.insert(0, str(ROOT / "skills" / "chief-of-staff" / "scripts"))
sys.path.insert(0, str(ROOT / "autopilot" / "src"))

from margo_autopilot import transport  # noqa: E402
from margo_autopilot.guard import TransportError  # noqa: E402

OUTPUT = HERE / "transport_cases.json"
SSE = "text/event-stream"
JSON = "application/json"


def message(rid, result=None, **extra):
    body = {"jsonrpc": "2.0", "id": rid, "result": result if result is not None else {"statusCode": 200}}
    body.update(extra)
    return json.dumps(body)


def sse(*events, newline="\n"):
    return "".join("event: message%sdata: %s%s%s" % (newline, event, newline, newline) for event in events)


PARSE = [
    ("json single message", message(7), JSON, 7),
    ("json batch picks the matching message", json.dumps([json.loads(message(6)), json.loads(message(7, {"n": 2}))]), JSON, 7),
    ("json first of two matches wins", json.dumps([json.loads(message(7, {"n": 1})), json.loads(message(7, {"n": 2}))]), JSON, 7),
    ("json float id equals int id", '{"jsonrpc":"2.0","id":7.0,"result":{}}', JSON, 7),
    ("json exponent id equals int id", '{"jsonrpc":"2.0","id":7e0,"result":{}}', JSON, 7),
    ("json true id equals 1", '{"jsonrpc":"2.0","id":true,"result":{"n":1}}', JSON, 1),
    ("json string id does not match", '{"jsonrpc":"2.0","id":"7","result":{}}', JSON, 7),
    ("json missing id does not match", '{"jsonrpc":"2.0","result":{}}', JSON, 7),
    ("json null literal answers nothing", "null", JSON, 7),
    ("json number answers nothing", "7", JSON, 7),
    ("empty body answers nothing", "", JSON, 7),
    ("python whitespace only answers nothing", " \t\r\n\x0b\x0c\x1c\x1f\x85\xa0\u2028\u3000", JSON, 7),
    ("non-json whitespace before json is not json", "\xa0" + message(7), JSON, 7),
    ("html is not json", "<html><body>Service unavailable</body></html>", JSON, 7),
    ("trailing data is not json", message(7) + " x", JSON, 7),
    ("byte order mark is not json", "\ufeff" + message(7), JSON, 7),
    ("missing content type is json", message(7), None, 7),
    ("duplicate keys keep the last value", '{"jsonrpc":"2.0","id":1,"result":{"a":1,"b":2,"a":3},"id":7}', JSON, 7),
    ("duplicate id overridden away", '{"jsonrpc":"2.0","id":7,"id":8,"result":{}}', JSON, 7),
    ("error message is still the answer", message(7, None, error={"code": -32000, "message": "fictional"}), JSON, 7),
    ("sse single event", sse(message(7)), SSE, 7),
    ("sse with charset parameter", sse(message(7)), SSE + "; charset=utf-8", 7),
    ("sse content type is case sensitive", sse(message(7)), "Text/Event-Stream", 7),
    ("sse crlf line endings", sse(message(7), newline="\r\n"), SSE, 7),
    ("sse skips other ids and non-json events", sse(message(6), "not json", message(7, {"n": 3})), SSE, 7),
    ("sse data split over two lines", "event: message\ndata: {\"jsonrpc\":\"2.0\",\ndata: \"id\":7,\"result\":{}}\n\n", SSE, 7),
    ("sse data without a space", 'data:{"jsonrpc":"2.0","id":7,"result":{}}\n\n', SSE, 7),
    ("sse data with python whitespace", 'data:\t\xa0\u3000{"jsonrpc":"2.0","id":7,"result":{}}\n\n', SSE, 7),
    ("sse lone carriage returns stay inside one block", 'data: {"jsonrpc":"2.0","id":7,"result":{}}\r\r', SSE, 7),
    ("sse batch event is not a message", "data: " + json.dumps([json.loads(message(7))]) + "\n\n", SSE, 7),
    ("sse empty data is skipped", "data: \n\n" + sse(message(7)), SSE, 7),
    ("sse comment and id lines are ignored", ": keep-alive\nid: 1\nevent: message\ndata: " + message(7) + "\n\n", SSE, 7),
    ("sse without a matching event", sse(message(6)), SSE, 7),
    ("sse leading space before data is not data", " data: " + message(7) + "\n\n", SSE, 7),
    ("sse byte order mark hides the first line", "\ufeffdata: " + message(7) + "\n\n", SSE, 7),
]

# Raw bytes: invalid UTF-8 is replaced, never fatal (Python errors="replace").
PARSE_BYTES = [
    ("invalid utf-8 in a string is replaced",
     b'{"jsonrpc":"2.0","id":7,"result":{"s":"a\xc0\xafb\xed\xa0\x80c\xf0\x9f\x98d\xe2\x82e\xff"}}', JSON, 7),
    ("valid utf-8 survives", '{"jsonrpc":"2.0","id":7,"result":{"s":"caf\u00e9 \U0001f600"}}'.encode("utf-8"), JSON, 7),
]

NORMALISE = [
    ("structured content is the payload", '{"structuredContent":{"statusCode":200,"data":{"ok":true}},"content":[{"type":"text","text":"{\\"ignored\\":1}"}]}'),
    ("text content is parsed", '{"content":[{"type":"text","text":"{\\"statusCode\\":200,\\"data\\":{\\"ok\\":true}}"}]}'),
    ("non-object structured content falls back to text", '{"structuredContent":[1],"content":[{"type":"text","text":"{\\"a\\":1}"}]}'),
    ("null structured content falls back to text", '{"structuredContent":null,"content":[{"type":"text","text":"{\\"a\\":1}"}]}'),
    ("text items join with newlines", '{"content":[{"type":"text","text":"{\\"a\\":"},{"type":"text","text":"1}"}]}'),
    ("non-text items are ignored", '{"content":[{"type":"image","data":"AA=="},{"type":"Text","text":"x"},{"type":"text","text":5},"text",{"type":"text","text":"{\\"b\\":2}"}]}'),
    ("text that is not json", '{"content":[{"type":"text","text":"Fictional plain answer"}]}'),
    ("whitespace text is not json", '{"content":[{"type":"text","text":"  "}]}'),
    ("json list text becomes value", '{"content":[{"type":"text","text":"[1,2]"}]}'),
    ("json null text becomes value", '{"content":[{"type":"text","text":"null"}]}'),
    ("json number text becomes value", '{"content":[{"type":"text","text":"5"}]}'),
    ("json string text becomes value", '{"content":[{"type":"text","text":"\\"done\\""}]}'),
    ("byte order mark text is not json", '{"content":[{"type":"text","text":"\\ufeff{}"}]}'),
    ("duplicate keys in text keep the last value", '{"content":[{"type":"text","text":"{\\"a\\":1,\\"a\\":2}"}]}'),
    ("no content is an empty payload", "{}"),
    ("empty content is an empty payload", '{"content":[]}'),
    ("string content has no items", '{"content":"text"}'),
    ("object content has no items", '{"content":{"type":"text","text":"{}"}}'),
    ("null content cannot be iterated", '{"content":null}'),
    ("number content cannot be iterated", '{"content":5}'),
    ("boolean content cannot be iterated", '{"content":true}'),
    ("tool error carries its status", '{"isError":true,"structuredContent":{"statusCode":403}}'),
    ("tool error status from text", '{"isError":true,"content":[{"type":"text","text":"{\\"statusCode\\":429}"}]}'),
    ("tool error without status", '{"isError":true,"content":[{"type":"text","text":"denied"}]}'),
    ("tool error with string status", '{"isError":true,"structuredContent":{"statusCode":"403"}}'),
    ("tool error with float status", '{"isError":true,"structuredContent":{"statusCode":403.0}}'),
    ("tool error with boolean status", '{"isError":true,"structuredContent":{"statusCode":true}}'),
    ("truthy string is an error", '{"isError":"yes","structuredContent":{"statusCode":500}}'),
    ("truthy number is an error", '{"isError":1,"structuredContent":{}}'),
    ("truthy list is an error", '{"isError":[0],"structuredContent":{}}'),
    ("false is not an error", '{"isError":false,"structuredContent":{"statusCode":200}}'),
    ("empty string is not an error", '{"isError":"","structuredContent":{"statusCode":200}}'),
    ("zero is not an error", '{"isError":0,"structuredContent":{"statusCode":200}}'),
    ("float zero is not an error", '{"isError":0.0,"structuredContent":{"statusCode":200}}'),
    ("underflowing float is not an error", '{"isError":1e-400,"structuredContent":{"statusCode":200}}'),
    ("empty list is not an error", '{"isError":[],"structuredContent":{"statusCode":200}}'),
    ("empty object is not an error", '{"isError":{},"structuredContent":{"statusCode":200}}'),
    ("null is not an error", '{"isError":null,"structuredContent":{"statusCode":200}}'),
    ("non-iterable content fails before the error flag", '{"isError":true,"content":null}'),
    ("list result is malformed", "[]"),
    ("null result is malformed", "null"),
    ("string result is malformed", '"ok"'),
]


def outcome(run):
    try:
        return {"ok": run()}
    except TransportError as exc:
        return {"error": type(exc).__name__, "message": str(exc), "status_code": exc.status_code}
    except Exception as exc:  # what the guard records as an unknown transport failure
        return {"error": "other", "type": type(exc).__name__}


def build():
    cases = []
    for name, body, content_type, rid in PARSE:
        raw = body.encode("utf-8")
        cases.append({"name": name, "op": "parse_body", "body_hex": raw.hex(), "content_type": content_type,
                      "request_id": rid, "expect": outcome(lambda: transport._parse_body(raw, content_type, rid))})
    for name, raw, content_type, rid in PARSE_BYTES:
        cases.append({"name": name, "op": "parse_body", "body_hex": raw.hex(), "content_type": content_type,
                      "request_id": rid, "expect": outcome(lambda: transport._parse_body(raw, content_type, rid))})
    for name, result in NORMALISE:
        cases.append({"name": name, "op": "normalise", "result_json": result,
                      "expect": outcome(lambda: transport._normalise(json.loads(result)))})
    return json.dumps({"generated_by": "cloud/tests/Margo.Cloud.WorkIQ.Tests/Parity/transport_cases.py",
                       "cases": cases}, indent=1) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--write", action="store_true")
    group.add_argument("--check", action="store_true")
    args = parser.parse_args()
    text = build()
    if args.write:
        OUTPUT.write_text(text, encoding="utf-8")
        print("wrote %s" % OUTPUT.relative_to(ROOT))
        return 0
    if not OUTPUT.is_file() or OUTPUT.read_text(encoding="utf-8") != text:
        print("%s is stale; run with --write" % OUTPUT.relative_to(ROOT))
        return 1
    print("%s matches transport.py" % OUTPUT.relative_to(ROOT))
    return 0


if __name__ == "__main__":
    sys.exit(main())
