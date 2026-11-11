#!/usr/bin/env bash
# Deploys the service on this machine: gets the code of a branch, rebuilds and restarts the service, waits for it to be healthy, and if it does not
# become healthy goes back to what was running before. Run it in the folder of the clone, or by name (it finds its own folder).
#
#   ./deploy.sh                          deploy master
#   DEPLOY_BRANCH=some-branch ./deploy.sh
#   DEPLOY_HEALTH_TIMEOUT=60 ./deploy.sh   seconds to wait for "healthy" (default 120)
#
# GitHub Actions runs it over SSH with a key that can run nothing else (a forced command in authorized_keys, see docs/setup-guide.md). The client's
# command arrives in SSH_ORIGINAL_COMMAND: "check" only says how things stand and deploys nothing; anything else deploys master.
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"

branch="${DEPLOY_BRANCH:-master}"
timeout="${DEPLOY_HEALTH_TIMEOUT:-120}"

health() { docker inspect --format '{{.State.Health.Status}}' "$(docker compose ps -q cloud)" 2>/dev/null || echo none; }

wait_healthy() {
  local until=$((SECONDS + timeout))
  while [ "$SECONDS" -lt "$until" ]; do
    [ "$(health)" = healthy ] && return 0
    sleep 3
  done
  return 1
}

if [ "${SSH_ORIGINAL_COMMAND:-}" = "check" ]; then
  echo "branch: $(git branch --show-current)"
  echo "commit: $(git log --oneline -1)"
  echo "service: $(health)"
  exit 0
fi

# Where to go back to: the branch that is checked out, or the commit if none is.
previous="$(git symbolic-ref -q --short HEAD || git rev-parse HEAD)"

git fetch --quiet origin
git checkout --quiet "$branch"
git merge --ff-only --quiet "origin/$branch"
echo "deploying $branch: $(git log --oneline -1)"

docker compose up -d --build
if wait_healthy; then
  echo "healthy"
  docker image prune -f > /dev/null
  exit 0
fi

echo "NOT healthy after ${timeout}s, so going back to ${previous}" >&2
docker compose logs --tail 30 cloud >&2 || true
git checkout --quiet "$previous"
docker compose up -d --build
if wait_healthy; then echo "back on ${previous}, healthy" >&2; else echo "still not healthy after going back: look at it by hand" >&2; fi
exit 1
