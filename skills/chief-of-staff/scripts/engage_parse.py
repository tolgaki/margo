#!/usr/bin/env python3
"""Parse workiq-retrieve markdown output into structured Viva Engage rows.

The retrieval tool returns markdown with an embedded, doubly-escaped ``sourceJson``
blob per hit. This pulls those out and emits clean JSON so a community read-out can
be built from real fields rather than from prose.

Usage:
    python3 engage_parse.py <retrieval-output.txt> [--group "Work IQ"] [--format json|table]
"""

import argparse
import json
import re
import sys
from urllib.parse import unquote

# Titles come from the footnote block: [^h1]: "Title" — https://engage.cloud.microsoft/...
FOOTNOTE = re.compile(r'^\[\^(h\d+)\]:\s*"(.*?)"\s*[\u2014-]\s*(\S+)', re.M)
SOURCE_JSON = re.compile(r'\*\*sourceJson:\*\*[ \t]*(\{.*)$', re.M)


def unescape(raw: str) -> str:
    """Undo the two layers of escaping the retrieval renderer applies.

    Hits arrive as JSON that has been escaped a second time (``PostBody`` often
    contains embedded JSON, so ``\\n`` and ``\\"`` appear doubled). Collapsing the
    doubled backslashes first, then undoing the markdown escapes, parses ~all hits;
    doing it in the other order leaves the embedded quotes terminating strings early.
    """
    collapsed = raw.replace('\\\\', '\\')
    return collapsed.replace('\\[', '[').replace('\\]', ']').replace('\\_', '_')


# Fallback for the rare blob that still will not parse: pull the flat scalar fields
# directly. Never worth losing a whole post over one malformed body.
FIELD = {
    'GroupName': re.compile(r'"GroupName":"(.*?)"'),
    'Type': re.compile(r'"Type":"(.*?)"'),
    'SenderName': re.compile(r'"SenderName":"(.*?)"'),
    'CreatedAt': re.compile(r'"CreatedAt":"(.*?)"'),
    'UpdatedAt': re.compile(r'"UpdatedAt":"(.*?)"'),
    'EncodedId': re.compile(r'"EncodedId":"(.*?)"'),
    'PostUpvoteCount': re.compile(r'"PostUpvoteCount":(\d+)'),
    'BestAnswerAuthorName': re.compile(r'"BestAnswerAuthorName":"(.*?)"'),
}

# The body is what the read-out actually quotes, so recover as much of it as
# possible rather than dropping it: match up to the next top-level key.
BODY = re.compile(r'"PostBody":"(.*?)"\s*,\s*"[A-Za-z]\w*":', re.S)


def salvage(raw: str) -> dict:
    obj = {}
    for key, pattern in FIELD.items():
        m = pattern.search(raw)
        if m:
            obj[key] = int(m.group(1)) if key == 'PostUpvoteCount' else m.group(1)
    body = BODY.search(raw)
    obj['PostBody'] = body.group(1) if body else ''
    # Marks the row as degraded so a partial post is never presented as a complete
    # one. The read-out leads with unanswered questions, and a row with no body is
    # not a question anyone can answer.
    obj['_salvaged'] = True
    obj.setdefault('ScopeType', 'YAMMER_GROUP' if 'YAMMER_GROUP' in raw else '')
    return obj


def _encoded_key(value: str) -> str:
    """Padding-insensitive join key for a base64 thread id.

    The permalink may carry base64 padding literally (``==``), percent-encoded
    (``%3D``), or stripped, while ``EncodedId`` keeps it. Normalizing both sides
    is what keeps a hit attached to its title and link.
    """
    return unquote(value or '').rstrip('=')


def load_hits(path: str) -> list:
    text = open(path, encoding='utf-8').read()

    titles = {}
    links = {}
    for ref, title, url in FOOTNOTE.findall(text):
        titles[ref] = title
        links[ref] = url.rstrip('\\').rstrip('.')

    hits = []
    for blob in SOURCE_JSON.findall(text):
        try:
            obj = json.loads(unescape(blob))
        except json.JSONDecodeError:
            obj = salvage(blob)
        if obj.get('ScopeType') != 'YAMMER_GROUP':
            continue
        hits.append(obj)

    # Match hits to titles/links by encoded thread id where possible.
    by_encoded = {}
    for ref, url in links.items():
        m = re.search(r'/(?:threads|articles)/([A-Za-z0-9_%+/=-]+)', url)
        if m:
            by_encoded[_encoded_key(m.group(1))] = ref

    rows = []
    for obj in hits:
        ref = by_encoded.get(_encoded_key(obj.get('EncodedId', '')))
        body = obj.get('PostBody') or ''
        created = (obj.get('CreatedAt') or '')[:10]
        updated = (obj.get('UpdatedAt') or '')[:10]
        rows.append({
            'title': titles.get(ref) or body[:90].replace('\n', ' '),
            'url': links.get(ref, ''),
            'group': obj.get('GroupName'),
            'type': obj.get('Type'),
            'author': obj.get('SenderName'),
            'created': created,
            'updated': updated,
            # Replies bump UpdatedAt, so the created->updated gap is the only usable
            # engagement proxy: PostUpvoteCount comes back 0 for every hit.
            'active_days': _daygap(created, updated),
            'upvotes': obj.get('PostUpvoteCount') or 0,
            'answered': bool(obj.get('BestAnswerBody')) or bool(obj.get('BestAnswerAuthorName')),
            'answered_by': obj.get('BestAnswerAuthorName'),
            'topics': obj.get('Topics'),
            'body': body,
            'salvaged': bool(obj.get('_salvaged')),
            'linked': ref is not None,
        })
    return rows


def _daygap(created: str, updated: str) -> int:
    from datetime import date
    try:
        c = date(*[int(x) for x in created.split('-')])
        u = date(*[int(x) for x in updated.split('-')])
        return (u - c).days
    except Exception:
        return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('path')
    ap.add_argument('--group', help='filter to one community, e.g. "Work IQ"')
    ap.add_argument('--format', choices=['json', 'table'], default='table')
    args = ap.parse_args()

    rows = load_hits(args.path)
    if args.group:
        rows = [r for r in rows if r['group'] == args.group]

    # Unanswered questions first, then by thread activity.
    rows.sort(key=lambda r: (r['answered'], -r['active_days'], r['created']))

    if args.format == 'json':
        json.dump(rows, sys.stdout, indent=2)
        return 0

    # Report the two metrics that actually go wrong, not just the row count: a
    # count-only check passes while every row has lost its link or its body.
    salvaged = [r for r in rows if r['salvaged']]
    unlinked = [r for r in rows if not r['linked']]
    print('%s posts%s — %d linked, %d unlinked, %d salvaged (body may be partial)' % (
        len(rows), (' in ' + args.group if args.group else ''),
        len(rows) - len(unlinked), len(unlinked), len(salvaged)))
    if unlinked or salvaged:
        print('! degraded rows are marked below: NOLINK = no permalink, SALV = recovered from a malformed blob')
    for r in rows:
        mark = 'ANS' if r['answered'] else ('ASK' if r['type'] == 'QUESTION' else '  .')
        flags = ''.join(['*' if r['salvaged'] else '', '^' if not r['linked'] else ''])
        print('%s%-2s [%-4s] act:%-4d %s %-20s %s' % (
            mark, flags, (r['type'] or '?')[:4], r['active_days'], r['created'],
            (r['author'] or '?')[:20], r['title'][:72] or '(no title, no body)'))
    if unlinked or salvaged:
        print('-' * 100)
        print('* = salvaged (body partial or empty)   ^ = no permalink resolved')
        print('Do not present a ^ row as citable, and do not quote a * row without re-reading the source.')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
