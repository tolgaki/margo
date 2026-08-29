#!/usr/bin/env bash
#
# Self-test for tools/check-clean.sh.
#
#   ./tools/check-clean-selftest.sh
#
# Every case below is a leak class that was demonstrated to slip past an earlier
# version of the checker. A checker that cannot prove it still detects things is
# how the first version passed a repo that contained a real OneDrive drive id.
#
# CI runs this. If you loosen a pattern in check-clean.sh, a case here will fail.
#
set -uo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.." || exit 1
CHECKER="$PWD/tools/check-clean.sh"

if [ -t 1 ] && [ -z "${NO_COLOR:-}" ]; then
  R=$'\033[31m'; G=$'\033[32m'; B=$'\033[1m'; N=$'\033[0m'
else
  R=""; G=""; B=""; N=""
fi

TMP=$(mktemp -d 2>/dev/null || mktemp -d -t margoselftest)
trap 'rm -rf "$TMP"' EXIT

FAILED=0
CASES=0

# must_flag <name> <rule-substring> <filename> <content>
#
# Asserting only "exit != 0" is not a test: any unrelated failure satisfies it,
# so every case would pass for the wrong reason whenever the repo is dirty. The
# rule substring pins WHICH check fired.
must_flag() {
  name="$1"; rule="$2"; fname="$3"; content="$4"
  CASES=$((CASES + 1))
  d="$TMP/case$CASES"; mkdir -p "$d"
  printf '%s\n' "$content" > "$d/$fname"
  out=$(MARGO_SCAN_ROOT="$d" NO_COLOR=1 "$CHECKER" 2>&1); rc=$?
  if [ "$rc" -eq 0 ]; then
    printf '%s✗%s MISSED: %s\n' "$R$B" "$N" "$name"
    printf '      %s\n' "$content"
    FAILED=1
  elif ! printf '%s' "$out" | grep -q "✗ $rule"; then
    printf '%s✗%s WRONG RULE: %s\n' "$R$B" "$N" "$name"
    printf '      expected the "%s" check to fail; got:\n' "$rule"
    printf '%s\n' "$out" | grep '✗' | sed 's/^/        /'
    FAILED=1
  else
    printf '%s✓%s caught: %-34s %s(%s)%s\n' "$G" "$N" "$name" "$R$B" "$rule" "$N"
  fi
}

# must_flag_dir <name> <rule-substring> <relative/dir/path> <filename> <content>
# Same as must_flag, but the leak is in a DIRECTORY component rather than in the
# file name or its contents.
must_flag_dir() {
  name="$1"; rule="$2"; reldir="$3"; fname="$4"; content="$5"
  CASES=$((CASES + 1))
  d="$TMP/case$CASES"; mkdir -p "$d/$reldir"
  printf '%s\n' "$content" > "$d/$reldir/$fname"
  out=$(MARGO_SCAN_ROOT="$d" NO_COLOR=1 "$CHECKER" 2>&1); rc=$?
  if [ "$rc" -eq 0 ]; then
    printf '%s✗%s MISSED: %s\n' "$R$B" "$N" "$name"
    FAILED=1
  elif ! printf '%s' "$out" | grep -q "✗ $rule"; then
    printf '%s✗%s WRONG RULE: %s\n' "$R$B" "$N" "$name"
    printf '%s\n' "$out" | grep '✗' | sed 's/^/        /'
    FAILED=1
  else
    printf '%s✓%s caught: %-34s %s(%s)%s\n' "$G" "$N" "$name" "$R$B" "$rule" "$N"
  fi
}

# must_pass <name> <filename> <content>
must_pass() {
  name="$1"; fname="$2"; content="$3"
  CASES=$((CASES + 1))
  d="$TMP/case$CASES"; mkdir -p "$d"
  printf '%s\n' "$content" > "$d/$fname"
  out=$(MARGO_SCAN_ROOT="$d" NO_COLOR=1 "$CHECKER" 2>&1)
  if [ $? -eq 0 ]; then
    printf '%s✓%s allowed: %s\n' "$G" "$N" "$name"
  else
    printf '%s✗%s FALSE POSITIVE: %s\n' "$R$B" "$N" "$name"
    printf '      %s\n' "$content"
    printf '%s\n' "$out" | grep '✗' | sed 's/^/        /'
    FAILED=1
  fi
}

printf '%sSelf-testing tools/check-clean.sh%s\n\n' "$B" "$N"

# --- Email: the match must be filtered, never the line -----------------------
must_flag "plain real address"            "no real email addresses"          leak.md "contact jane.doe@northwind.co"
must_flag "real address beside allowed"   "no real email addresses"          leak.md "contact jane.doe@acme.co or bob@example.com"
must_flag "real address beside thread id" "no real email addresses"          leak.md "owner alice@acme.co in 19:abc123def456@thread.tacv2"

# --- Home paths: no trailing slash, uppercase, Windows -----------------------
must_flag "unix home, no trailing slash"  "no absolute home paths"           leak.md "clone lives at /Users/jsmith"
must_flag "unix home, uppercase name"     "no absolute home paths"           leak.md "state under /home/J_Smith/x/"
must_flag "windows home path"             "no absolute home paths"           leak.md 'profile C:\Users\jsmith\.copilot\skills\'

# --- Files with no extension are still scanned -------------------------------
must_flag "extension-less script"         "no real email addresses"          postinstall "# admin is jane.doe@acme.co"
must_flag "extension-less GUID"           "no tenant/org/channel GUIDs"      postinstall "TENANT=7f2a91b4-3c8d-4e15-9a62-1d5f8b3c7e04"

# --- Tenant identifiers that are not GUID-shaped -----------------------------
must_flag "onedrive drive id"             "no tenant resource identifiers"   leak.md 'drive b!WF9pF4hH0yvIlXpVYzPrOCzDhqQLFNHuHW3E3dcgxGykG6sEm5hTamJjYzYbtvb'
must_flag "sharepoint item id"            "no tenant resource identifiers"   leak.md 'folder 015WODUORRUVPNIU4HYRG2F6JGGQVPL3YO'
must_flag "sharepoint site url"           "no tenant resource identifiers"   leak.md 'see https://acmecorp.sharepoint.com/sites/Eng/Shared%20Documents'
must_flag "teams meeting link"            "no tenant resource identifiers"   leak.md 'join https://teams.microsoft.com/l/meetup-join/19%3ameeting_abc%40thread.v2/0'
must_flag "onmicrosoft tenant domain"     "no corporate mail domains"        leak.md 'tenant acmecorp.onmicrosoft.com'
must_flag "colleague name in filename"    "no workplace data in file"        'jane.doe@acme.co.md' 'nothing sensitive inside'
must_flag_dir "home path as directory"    "no workplace data in file"        'Users/jsmith/notes' 'x.md' 'benign'
must_flag "teams thread id"               "no tenant resource identifiers"   leak.md 'channel 19:aBcDeF1234567@thread.tacv2'

# --- Personal names outside the fictional cast -------------------------------
must_flag "real name in 1:1 example"      "no personal names outside"        leak.md 'add that to my 1:1 with Soumya'
must_flag "real name in reply example"    "no personal names outside"        leak.md 'draft a reply to Priyanka about the deck'

# --- GUIDs -------------------------------------------------------------------
must_flag "bare tenant GUID"              "no tenant/org/channel GUIDs"      leak.md "tenant 7f2a91b4-3c8d-4e15-9a62-1d5f8b3c7e04"

# --- Things that must NOT trip it -------------------------------------------
must_pass "example.com addresses"   ok.md "write to someone@example.com"
must_pass "placeholder home path"   ok.md 'clone at /Users/{you}/src/margo'
must_pass "documented public ADO id" ok.md "resource 499b84ac-1321-427f-aa17-267ca6975798"
must_pass "installer AppId"          ok.md "AppId={{B7E4B0A1-3C5D-4E2A-9F17-6D8C2A5E9B34}"
must_pass "graph api host"           ok.md "POST https://graph.microsoft.com/v1.0/me/sendMail"
must_pass "placeholder thread id"    ok.md 'channel {19:...@thread.tacv2}'
must_pass "cast name in 1:1 example" ok.md 'add that to my 1:1 with Dana'
must_pass "RFC2606 .example domain"  ok.md 'mailbox margo@yourtenant.example'
must_pass "container service path"   ok.md 'WORKDIR /home/margo'

printf '\n'
if [ "$FAILED" -ne 0 ]; then
  printf '%sSelf-test FAILED.%s check-clean.sh no longer detects a known leak class,\n' "$R$B" "$N"
  printf 'or has started flagging something legitimate.\n'
  exit 1
fi
printf '%sSelf-test passed%s — %s cases.\n' "$G$B" "$N" "$CASES"
