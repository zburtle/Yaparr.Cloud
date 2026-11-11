#!/usr/bin/env bash
# Deploys the service on this machine: gets the code of a branch, rebuilds and restarts the service, waits for it to be healthy, and if it does not
# become healthy goes back to what was running before. Run it in the folder of the clone, or by name (it finds its own folder).
#
#   ./deploy.sh deploy                    deploy master
#   ./deploy.sh check                     say what is running, and deploy nothing
#   DEPLOY_BRANCH=some-branch ./deploy.sh deploy
#   DEPLOY_HEALTH_TIMEOUT=60 ./deploy.sh deploy   seconds to wait for "healthy" (default 120)
#
# GitHub Actions runs it over SSH with a key that can run nothing else (a forced command in authorized_keys, see docs/setup-guide.md). What the client
# asked for arrives in SSH_ORIGINAL_COMMAND, and only "deploy" and "check" are accepted: with no command, or any other, nothing happens.
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

action="${SSH_ORIGINAL_COMMAND:-${1:-}}"
case "$action" in
  check)
    echo "branch: $(git branch --show-current)"
    echo "commit: $(git log --oneline -1)"
    echo "service: $(health)"
    exit 0
    ;;
  deploy) ;;
  *)
    echo "usage: deploy.sh deploy | check" >&2
    exit 2
    ;;
esac

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
