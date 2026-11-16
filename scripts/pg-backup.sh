#!/usr/bin/env bash
# A daily dump of the accounts database (Postgres), kept for two weeks on the machine and, when the service has a bucket (S3_* in .env), also in the bucket in a folder of its own
# (db-dumps/, thirty days), run by cron as the user that runs docker compose:
#
#   17 3 * * * /bin/bash /home/deploy/Yaparr.Cloud/scripts/pg-backup.sh >> /home/deploy/db-backups/cron.log 2>&1
#
# When a dump has been made and checked it tells the service (last-dump.json in the data folder, with whether it reached the bucket), which the admin portal shows,
# so a dump that stops being made, or stops reaching the bucket, does not go unnoticed. See docs/setup-guide.md, "Moving to Postgres". Dumps are kept in $DUMP_DIR
# (default: ~/db-backups). To get one back from the bucket: dumps list, then dumps get <name> > file.sql.gz (setup guide).
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

# To the bucket: 0 put there, 3 no bucket is set (nothing to do), anything else did not work (the dump is still here).
uploaded=""
rc=0
docker compose exec -T cloud dotnet Yaparr.Cloud.dll dumps upload "$(basename "$out")" < "$out" || rc=$?
case "$rc" in
  0) uploaded=',"uploaded":true'
     docker compose exec -T cloud dotnet Yaparr.Cloud.dll dumps prune --days 30 || echo "warning: old dumps could not be removed from the bucket" >&2 ;;
  3) ;;
  *) uploaded=',"uploaded":false'; echo "warning: the dump could not be copied to the bucket" >&2 ;;
esac

printf '{"at":"%s","bytes":%s,"file":"%s"%s}' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$size" "$(basename "$out")" "$uploaded" | docker compose exec -T cloud sh -c 'cat > /data/last-dump.json'
echo "$(date -Is) wrote $out ($size bytes)"
