#!/usr/bin/env bash
# Proves the install examples in README.md work, against an image you have already built.
#
# The `docker run` line and the docker-compose.yml are taken from the README itself (the first fenced block under
# the "### docker run" and "### docker-compose.yml" headings), so the README and this test cannot drift apart: if
# either heading or block moves, this fails rather than testing something else. Only what a stranger would change
# is changed here: the published image becomes the image under test, and the /srv folders become fresh temporary
# folders anyone can write to. Everything else (ports, user, environment, mounts, restart policy) runs as written.
#
# For each example it starts Deluno, waits for /api/health/ready to answer, and takes it down again.
#
# Usage:  bash scripts/test-readme-docker-examples.sh <image>
#         bash scripts/test-readme-docker-examples.sh <image> --extract-only   (print what would run; needs no Docker)
set -euo pipefail

image="${1:?usage: test-readme-docker-examples.sh <image> [--extract-only]}"
mode="${2:-run}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readme="$root/README.md"
ready_url="http://127.0.0.1:5099/api/health/ready"

# The first fenced block after a heading line, without the fences. Fails if there is none before the next heading.
extract_block() {
  local heading="$1" block
  block="$(tr -d '\r' < "$readme" | awk -v heading="$heading" '
    $0 == heading { found = 1; next }
    found && /^```/ { if (inblock) exit; inblock = 1; next }
    found && !inblock && /^#+ / { exit }
    inblock { print }
  ')"
  if [[ -z "$block" ]]; then
    echo "README.md has no fenced block under \"$heading\"; this test reads its install examples from there." >&2
    exit 1
  fi
  printf '%s\n' "$block"
}

work="$(mktemp -d)"
mkdir -p "$work/config" "$work/data" "$work/qbittorrent-config"
# The examples run as 1000:1000, which is not the CI user: let any user write.
chmod -R 0777 "$work"
compose_file="$work/docker-compose.yml"

# The published image is named from the one place the repository is written (repository.json), lower-cased, as the
# release workflow does; no owner is spelled in this script.
published_image="ghcr.io/$(sed -n 's/.*"deluno"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$root/repository.json" | tr '[:upper:]' '[:lower:]')"
[[ "$published_image" != "ghcr.io/" ]] || { echo "repository.json has no deluno repository." >&2; exit 1; }
published_pattern="$(printf '%s' "$published_image" | sed 's/\./\\./g')"

localize() {
  sed -e "s#${published_pattern}:[A-Za-z0-9._-]*#${image}#g" \
      -e "s#/srv/deluno-config#${work}/config#g" \
      -e "s#/srv/qbittorrent-config#${work}/qbittorrent-config#g" \
      -e "s#/srv/data#${work}/data#g"
}

docker_run_script="$(extract_block '### docker run' | localize)"
extract_block '### docker-compose.yml' | localize > "$compose_file"

grep -qF -- "$image" <<<"$docker_run_script" || { echo "The docker run example in README.md does not name the published image; nothing to test it against." >&2; exit 1; }
grep -qF -- "image: $image" "$compose_file" || { echo "The docker-compose.yml example in README.md does not name the published image; nothing to test it against." >&2; exit 1; }
if grep -q '/srv/' "$compose_file" || grep -q '/srv/' <<<"$docker_run_script"; then
  echo "An example in README.md mounts a /srv folder this test does not know how to replace; teach localize() about it." >&2
  exit 1
fi

if [[ "$mode" == "--extract-only" ]]; then
  echo "== docker run =="; echo "$docker_run_script"
  echo "== docker-compose.yml =="; cat "$compose_file"
  rm -rf "$work"
  exit 0
fi

cleanup() {
  result=$?
  if [[ "$result" -ne 0 ]]; then
    docker logs deluno 2>&1 | tail -n 80 || true
    docker compose -f "$compose_file" -p readme-proof logs --no-color 2>&1 | tail -n 120 || true
  fi
  docker compose -f "$compose_file" -p readme-proof down -v --remove-orphans >/dev/null 2>&1 || true
  docker rm --force deluno >/dev/null 2>&1 || true
  # The containers wrote their config folders as their own users, which the runner cannot delete, so the folders
  # are emptied from inside a throwaway container (as root) first. A tidy-up that fails must never fail a proof
  # that passed.
  docker run --rm --user 0 -v "$work:/work" --entrypoint sh "$image" -c 'rm -rf /work/* /work/.[!.]*; true' >/dev/null 2>&1 || true
  rm -rf "$work" 2>/dev/null || true
  trap - EXIT
  exit "$result"
}
trap cleanup EXIT

wait_for_ready() {
  local label="$1"
  for _ in $(seq 1 60); do
    if curl --fail --silent --show-error "$ready_url" >/dev/null 2>&1; then
      echo "$label: /api/health/ready answered."
      return 0
    fi
    sleep 2
  done
  echo "$label: /api/health/ready did not answer within 120 seconds." >&2
  return 1
}

echo "== docker-compose.yml from the README =="
# The second service is somebody else's published image; a flaky pull is not Deluno's fault, so it is retried.
for attempt in 1 2 3; do
  docker compose -f "$compose_file" -p readme-proof pull qbittorrent && break
  [[ "$attempt" -eq 3 ]] && { echo "Could not pull the qbittorrent image the README's compose file uses." >&2; exit 1; }
  sleep 10
done
docker compose -f "$compose_file" -p readme-proof up -d
wait_for_ready "compose"
docker compose -f "$compose_file" -p readme-proof down -v --remove-orphans

echo "== docker run from the README =="
bash -euo pipefail -c "$docker_run_script"
wait_for_ready "docker run"
docker rm --force deluno >/dev/null

echo "Both README install examples start Deluno and reach ready."
