#!/usr/bin/env bash
# A daily dump of the accounts database (Postgres), kept for two weeks, run by cron as the user that runs docker compose:
#
#   17 3 * * * /home/deploy/Yaparr.Cloud/scripts/pg-backup.sh >> /home/deploy/db-backups/cron.log 2>&1
#
# When a dump has been made and checked, it tells the service (last-dump.json in the data folder), which the admin portal shows, so a dump that stops
# being made does not go unnoticed. See docs/setup-guide.md, "Moving to Postgres". Dumps are kept in $DUMP_DIR (default: ~/db-backups).
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")/.."
dir="${DUMP_DIR:-$HOME/db-backups}"
mkdir -p "$dir"
umask 077
out="$dir/yaparr-$(date +%F).sql.gz"
docker compose exec -T postgres pg_dump -U yaparr yaparr | gzip > "$out.tmp"
gzip -t "$out.tmp"
mv "$out.tmp" "$out"
find "$dir" -name 'yaparr-*.sql.gz' -mtime +14 -delete
size=$(stat -c %s "$out")
printf '{"at":"%s","bytes":%s,"file":"%s"}' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$size" "$(basename "$out")" | docker compose exec -T cloud sh -c 'cat > /data/last-dump.json'
echo "$(date -Is) wrote $out ($size bytes)"
