#!/usr/bin/env bash
#
# Consistent backup of Margo's private state. Runs as root (margo-backup.timer
# or by hand), stops the harness so no run is mid-flight, copies every account
# database through SQLite's online backup API, checks the copy's integrity,
# adds the config, deployment anchor, audit log and health file, restarts the
# harness if it was running, and prunes old backups.
#
#   sudo /opt/margo/deploy/azure/scripts/backup-state.sh [--keep N] [--dest DIR]
#
# The database copy itself runs as the service user: opening a WAL database
# as root could leave root-owned -shm/-wal files behind that the service user
# can no longer open. Root then moves the copy into a directory only root can
# read, rename or delete, so a model-driven shell cannot touch backups.
#
# Not a disaster-recovery plan on its own: the default destination is on the
# same VM. Pair it with Azure Backup for the VM or copy the directory elsewhere
# (RUNBOOKS.md, "Restore").
#
set -euo pipefail

SELF_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "$SELF_DIR/lib.sh"

KEEP="${MARGO_BACKUP_KEEP:-14}"
DEST="$MARGO_BACKUP_DIR"
while [ $# -gt 0 ]; do
  case "$1" in
    --keep) [ $# -ge 2 ] || die "--keep needs a value"; KEEP="$2"; shift ;;
    --dest) [ $# -ge 2 ] || die "--dest needs a value"; DEST="$2"; shift ;;
    -h|--help) sed -n '2,19p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
  shift
done
[[ "$KEEP" =~ ^[0-9]+$ ]] && [ "$KEEP" -ge 1 ] || die "--keep must be a positive integer"

require_root backup-state.sh

STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
TARGET="$DEST/$STAMP"
STAGING="$MARGO_HOME/.backup-staging"
WAS_ACTIVE="$(unit_state margo-harness.service)"

cleanup() {
  rm -rf "$STAGING"
  if [ "$WAS_ACTIVE" = "active" ]; then
    systemctl start margo-harness.service || warn "margo-harness did not restart; start it by hand (systemctl start margo-harness)"
  fi
}
trap cleanup EXIT

install -d -m 0700 -o root -g root "$DEST"
[ ! -e "$TARGET" ] || die "$TARGET already exists"
mkdir -m 0700 "$TARGET"

if [ "$WAS_ACTIVE" = "active" ]; then
  systemctl stop margo-harness.service
  log "margo-harness stopped for the backup (will be restarted)"
fi

rm -rf "$STAGING"
install -d -m 0700 -o "$MARGO_USER" -g "$MARGO_USER" "$STAGING"

# Every account database under COPILOT_HOME/margo/state/<account-hash>/, copied
# page by page under a read transaction and verified with integrity_check. The
# backup API restarts if the source changes mid-copy, so a manager approval
# arriving through the gate during the copy cannot produce a torn file.
DATABASES="$(as_margo python3 -I - "$MARGO_COPILOT_HOME/margo/state" "$STAGING/state" <<'EOF'
import json, os, sqlite3, sys
source_root, target_root = sys.argv[1], sys.argv[2]
copied = []
if os.path.isdir(source_root):
    for account_hash in sorted(os.listdir(source_root)):
        source_path = os.path.join(source_root, account_hash, "margo.sqlite3")
        if not os.path.isfile(source_path):
            continue
        os.makedirs(os.path.join(target_root, account_hash), mode=0o700, exist_ok=True)
        target_path = os.path.join(target_root, account_hash, "margo.sqlite3")
        descriptor = os.open(target_path, os.O_CREAT | os.O_EXCL | os.O_RDWR, 0o600)
        os.close(descriptor)
        source = sqlite3.connect("file:%s?mode=ro" % source_path, uri=True)
        try:
            copy = sqlite3.connect(target_path)
            try:
                source.backup(copy)
                integrity = copy.execute("PRAGMA integrity_check").fetchone()[0]
            finally:
                copy.close()
        finally:
            source.close()
        if integrity != "ok":
            sys.stderr.write("integrity_check failed for %s: %s\n" % (account_hash, integrity))
            sys.exit(1)
        copied.append({"account_hash": account_hash, "bytes": os.path.getsize(target_path), "integrity": integrity})
print(json.dumps(copied, sort_keys=True))
EOF
)" || die "database backup failed (see message above); nothing was pruned"

mkdir -p "$TARGET/state"
if [ -d "$STAGING/state" ]; then
  cp -a "$STAGING/state/." "$TARGET/state/"
fi
chown -R root:root "$TARGET"

copy_if_present() {
  # $1 = source, $2 = name inside the backup.
  if [ -f "$1" ]; then cp -p "$1" "$TARGET/$2"; chown root:root "$TARGET/$2"; chmod 0600 "$TARGET/$2"; fi
}
copy_if_present "$MARGO_COPILOT_HOME/margo/config.json" config.json
copy_if_present "$MARGO_DEPLOYMENT" deployment.json
copy_if_present "$MARGO_HOME/gate-audit.jsonl" gate-audit.jsonl
copy_if_present "$MARGO_HOME/health.json" health.json
copy_if_present "$MARGO_COPILOT_HOME/mcp-config.json" mcp-config.json

( cd "$TARGET" && find . -type f ! -name manifest.json -print0 | sort -z | xargs -0 sha256sum ) > "$TARGET/sha256sums.txt"
python3 -I - "$TARGET/manifest.json" "$STAMP" "$DATABASES" "$WAS_ACTIVE" "$TARGET/sha256sums.txt" <<'EOF'
import json, sys
hashes = {}
for line in open(sys.argv[5], encoding="utf-8"):
    digest, _, name = line.rstrip("\n").partition("  ")
    hashes[name[2:] if name.startswith("./") else name] = digest
document = {"schema_version": 1, "kind": "margo-state-backup", "recorded_at": sys.argv[2],
            "databases": json.loads(sys.argv[3]), "harness_was_active": sys.argv[4] == "active",
            "files": hashes}
with open(sys.argv[1], "w", encoding="utf-8") as stream:
    json.dump(document, stream, indent=2, sort_keys=True)
    stream.write("\n")
EOF
chmod 0600 "$TARGET/manifest.json" "$TARGET/sha256sums.txt"

# Prune: keep the newest $KEEP timestamped directories, never anything else
# that happens to live in the destination.
PRUNED=0
while IFS= read -r old; do
  rm -rf "${DEST:?}/${old:?}"
  PRUNED=$((PRUNED + 1))
done < <(find "$DEST" -mindepth 1 -maxdepth 1 -type d -name '[0-9]*T[0-9]*Z' -printf '%f\n' | sort -r | tail -n +"$((KEEP + 1))")

python3 -I -c 'import json, sys
print(json.dumps({"backup": sys.argv[1], "databases": json.loads(sys.argv[2]), "pruned": int(sys.argv[3]),
                  "harness_restarted": sys.argv[4] == "active"}, sort_keys=True))' \
  "$TARGET" "$DATABASES" "$PRUNED" "$WAS_ACTIVE"
