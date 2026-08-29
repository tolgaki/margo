#!/usr/bin/env python3
"""Consolidate Teams channel messages saved from workiq-fetch into a triage view.

``workiq-fetch`` writes oversized responses to temp files. Point this at one or more
of those files; it merges them, drops the join/leave ``systemEventMessage`` noise,
strips HTML, and reports each root post with its reply and reaction counts.

Usage:
    python3 teams_feedback.py <fetch-output.json> [more.json ...] \
        [--replies replies1.json ...] [--format table|json] [--since YYYY-MM-DD]
"""

import argparse
import html
import json
import re
import sys

TAG = re.compile(r'<[^>]+>')
WS = re.compile(r'\s+')


def clean(body: str) -> str:
    """Turn Teams HTML into a single readable line."""
    if not body:
        return ''
    text = re.sub(r'<img[^>]*>', ' [image] ', body)
    text = TAG.sub(' ', text)
    text = html.unescape(text)
    return WS.sub(' ', text).strip()


def iter_messages(paths, problems):
    """Yield raw message dicts from any nesting workiq-fetch produced.

    Every page that does not yield usable data is recorded in ``problems``. A
    failed page is indistinguishable from an empty one once its messages are
    dropped, and this collection is documented as returning 500 when the wrong
    fields are selected — so an unreported failure becomes a confident undercount.
    """
    for path in paths:
        try:
            doc = json.load(open(path, encoding='utf-8'))
        except (OSError, json.JSONDecodeError) as exc:
            problems.append('%s: unreadable (%s)' % (path, exc.__class__.__name__))
            continue
        results = doc.get('results')
        if not results:
            problems.append('%s: no results array' % path)
            continue
        for page, result in enumerate(results, 1):
            status = result.get('statusCode')
            if status is not None and int(status) >= 400:
                err = result.get('error') or (result.get('data') or {}).get('error') or ''
                problems.append('%s page %d: HTTP %s %s' % (path, page, status, err))
                continue
            data = result.get('data') or {}
            if 'value' not in data:
                problems.append('%s page %d: no value array' % (path, page))
                continue
            for msg in data['value']:
                yield msg


def collect(paths, problems):
    """Dedupe by id, keeping the most recently modified copy of each message."""
    seen = {}
    for msg in iter_messages(paths, problems):
        if msg.get('messageType') != 'message':
            continue
        if msg.get('deletedDateTime'):
            continue
        mid = msg.get('id')
        prev = seen.get(mid)
        if prev is None or (msg.get('lastModifiedDateTime') or '') > (prev.get('lastModifiedDateTime') or ''):
            seen[mid] = msg
    return seen


def build(paths, reply_paths, problems):
    roots = collect(paths, problems)
    replies = collect(reply_paths, problems) if reply_paths else {}

    # Replies can also arrive inline in the root files; route anything with a
    # replyToId into the reply bucket rather than treating it as a new thread.
    for mid, msg in list(roots.items()):
        if msg.get('replyToId'):
            replies[mid] = msg
            del roots[mid]

    threads = {}
    for mid, msg in replies.items():
        parent = msg.get('replyToId')
        if not parent:
            # A root-level message that arrived in a reply payload. Keep it as a
            # thread of its own instead of failing the whole run on a missing key.
            roots.setdefault(mid, msg)
            continue
        threads.setdefault(parent, []).append(msg)

    rows = []
    for mid, msg in roots.items():
        kids = sorted(threads.get(mid, []), key=lambda m: m.get('createdDateTime') or '')
        author = ((msg.get('from') or {}).get('user') or {}).get('displayName') or 'unknown'
        responders = []
        for k in kids:
            name = ((k.get('from') or {}).get('user') or {}).get('displayName')
            if name and name != author and name not in responders:
                responders.append(name)

        # Who spoke last decides whether a thread still needs someone, and
        # `teams-feedback.md` mandates the asker-last / team-last split. A bare
        # `bool(responders)` marks "Asker -> Helper -> Asker: still blocked" as
        # answered, which is exactly the thread that most needs surfacing.
        ordered = sorted(kids, key=lambda k: k.get('createdDateTime') or '')
        last_speaker = author
        last_reply_at = ''
        if ordered:
            last = ordered[-1]
            last_speaker = (((last.get('from') or {}).get('user') or {})
                            .get('displayName')) or author
            last_reply_at = last.get('createdDateTime') or ''

        if not kids:
            status = 'no-reply'
        elif last_speaker == author:
            status = 'asker-last'
        else:
            status = 'team-last'

        rows.append({
            'id': mid,
            'created': (msg.get('createdDateTime') or '')[:10],
            'author': author,
            'subject': msg.get('subject') or '',
            'text': clean((msg.get('body') or {}).get('content', '')),
            'reactions': len(msg.get('reactions') or []),
            'mentions': len(msg.get('mentions') or []),
            'replies': len(kids),
            'responders': responders,
            'last_speaker': last_speaker,
            'last_reply_at': last_reply_at,
            'status': status,
            # Only a thread whose last word came from someone else is answered.
            'answered': status == 'team-last',
            'url': msg.get('webUrl', ''),
        })
    rows.sort(key=lambda r: r['created'])
    return rows


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('paths', nargs='+')
    ap.add_argument('--replies', nargs='*', default=[])
    ap.add_argument('--since', help='only posts created on/after YYYY-MM-DD')
    ap.add_argument('--format', choices=['table', 'json'], default='table')
    args = ap.parse_args()

    problems = []
    rows = build(args.paths, args.replies, problems)
    if args.since:
        rows = [r for r in rows if r['created'] >= args.since]

    if args.format == 'json':
        json.dump({'partial': bool(problems), 'problems': problems, 'posts': rows},
                  sys.stdout, indent=2)
        return 1 if problems else 0

    unanswered = [r for r in rows if not r['answered']]
    if problems:
        # Loud, on stdout, above the counts — a silently truncated sweep reads as a
        # healthy channel, and every number below is derived from what survived.
        print('WARNING: %d page(s) yielded no data. COUNTS BELOW ARE PARTIAL.' % len(problems))
        for p in problems:
            print('  ! ' + p)
    no_reply = [r for r in rows if r['status'] == 'no-reply']
    asker_last = [r for r in rows if r['status'] == 'asker-last']
    print('%d posts, %d with no reply, %d awaiting a follow-up (asker spoke last)%s' % (
        len(rows), len(no_reply), len(asker_last), ' (PARTIAL)' if problems else ''))
    print('-' * 100)
    for r in rows:
        mark = {'no-reply': 'NONE', 'asker-last': 'BACK', 'team-last': ' ans'}[r['status']]
        print('%s %s %-18s r:%-2d x:%-2d %s' % (
            mark, r['created'], r['author'][:18], r['replies'], r['reactions'],
            (r['subject'] + ' ' if r['subject'] else '') + r['text'][:72]))
    return 1 if problems else 0


if __name__ == '__main__':
    raise SystemExit(main())
