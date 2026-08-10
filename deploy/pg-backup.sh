#!/usr/bin/env bash
# Daily Postgres dump for Teamscop. Keep RETAIN_DAYS copies.
set -euo pipefail

RETENTION_DAYS="${TEAMSCOPE_BACKUP_RETAIN_DAYS:-14}"
BACKUP_DIR="${TEAMSCOPE_BACKUP_DIR:-/var/backups/teamscop}"
ENV_FILE="${TEAMSCOPE_API_ENV:-/etc/teamscop/api.env}"

mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"

# Parse ConnectionStrings__Default from api.env
CONN="$(grep -E '^ConnectionStrings__Default=' "$ENV_FILE" | head -1 | cut -d= -f2-)"
if [[ -z "$CONN" ]]; then
  echo "No ConnectionStrings__Default in $ENV_FILE" >&2
  exit 1
fi

# Extract Host/Port/Database/Username/Password (simple key=value; pairs)
get_field() {
  echo "$CONN" | tr ';' '\n' | grep -i "^$1=" | head -1 | cut -d= -f2-
}

export PGHOST="$(get_field Host)"
export PGPORT="$(get_field Port)"
export PGDATABASE="$(get_field Database)"
export PGUSER="$(get_field Username)"
export PGPASSWORD="$(get_field Password)"

STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
OUT="$BACKUP_DIR/teamscop-$STAMP.sql.gz"
pg_dump --no-owner --format=plain | gzip -c >"$OUT"
chmod 600 "$OUT"
echo "Wrote $OUT"

find "$BACKUP_DIR" -type f -name 'teamscop-*.sql.gz' -mtime +"$RETENTION_DAYS" -delete
ls -lh "$BACKUP_DIR" | tail -20
