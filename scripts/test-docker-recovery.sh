#!/usr/bin/env bash
# Exercises the container-native recovery path with real Deluno state. The
# runner owns these exact names and removes them in the EXIT trap; a deployment
# keeps its /data volume and rolls back to its previously selected image.
set -euo pipefail

baseline_image="${1:?Usage: test-docker-recovery.sh <baseline-image> <candidate-image>}"
candidate_image="${2:?Usage: test-docker-recovery.sh <baseline-image> <candidate-image>}"
suffix="${GITHUB_RUN_ID:-local}-$$"
primary_container="deluno-docker-recovery-${suffix}"
failed_container="deluno-docker-failed-${suffix}"
recovered_container="deluno-docker-recovered-${suffix}"
data_volume="deluno-docker-data-${suffix}"
port="18081"
base_url="http://127.0.0.1:${port}"
username="docker-recovery-owner"
password="docker-recovery-password"
fixture_title="Docker Recovery Fixture"

cleanup() {
  result=$?
  if [[ "$result" -ne 0 ]]; then
    for container in "$primary_container" "$failed_container" "$recovered_container"; do
      docker logs "$container" 2>/dev/null || true
    done
  fi

  for container in "$primary_container" "$failed_container" "$recovered_container"; do
    docker rm --force "$container" >/dev/null 2>&1 || true
  done
  docker volume rm "$data_volume" >/dev/null 2>&1 || true
  trap - EXIT
  exit "$result"
}
trap cleanup EXIT

wait_for_ready() {
  local attempts="${1:-30}"
  for _ in $(seq 1 "$attempts"); do
    if curl --fail --silent --show-error "${base_url}/api/health/ready" >/dev/null; then
      return 0
    fi
    sleep 2
  done
  return 1
}

run_deluno() {
  local container="$1"
  local image="$2"
  docker run --detach \
    --name "$container" \
    --publish "${port}:8080" \
    --mount "type=volume,src=${data_volume},dst=/data" \
    --env Server__Port=8080 \
    --env Server__AllowLan=true \
    --env Storage__DataRoot=/data \
    "$image" >/dev/null
}

docker volume create "$data_volume" >/dev/null
run_deluno "$primary_container" "$baseline_image"
wait_for_ready

bootstrap="$(curl --fail --silent --show-error \
  --header 'Content-Type: application/json' \
  --data "{\"username\":\"${username}\",\"displayName\":\"Docker recovery owner\",\"password\":\"${password}\"}" \
  "${base_url}/api/auth/bootstrap")"
token="$(jq --exit-status --raw-output '.accessToken' <<<"$bootstrap")"

created_movie="$(curl --fail --silent --show-error \
  --header "Authorization: Bearer ${token}" \
  --header 'Content-Type: application/json' \
  --data "{\"title\":\"${fixture_title}\",\"releaseYear\":2026,\"monitored\":false}" \
  "${base_url}/api/movies")"
movie_id="$(jq --exit-status --raw-output '.id' <<<"$created_movie")"

# A bad replacement must not be treated as an update. It shares the persisted
# volume only long enough to prove readiness fails; the known-good image is
# then recreated against that untouched volume.
docker rm --force "$primary_container" >/dev/null
docker run --detach \
  --name "$failed_container" \
  --publish "${port}:8080" \
  --mount "type=volume,src=${data_volume},dst=/data" \
  busybox:1.36 sh -c 'sleep 30' >/dev/null

if wait_for_ready 3; then
  echo 'A deliberately non-Deluno replacement unexpectedly passed readiness.' >&2
  exit 1
fi
docker rm --force "$failed_container" >/dev/null

# The candidate image must migrate the baseline volume itself; the test never
# copies databases between images or accepts a clean replacement volume.
run_deluno "$recovered_container" "$candidate_image"
wait_for_ready

recovered_login="$(curl --fail --silent --show-error \
  --header 'Content-Type: application/json' \
  --data "{\"username\":\"${username}\",\"password\":\"${password}\"}" \
  "${base_url}/api/auth/login")"
recovered_token="$(jq --exit-status --raw-output '.accessToken' <<<"$recovered_login")"
recovered_movie="$(curl --fail --silent --show-error \
  --header "Authorization: Bearer ${recovered_token}" \
  "${base_url}/api/movies/${movie_id}")"

jq --exit-status --arg id "$movie_id" --arg title "$fixture_title" \
  '.id == $id and .title == $title' <<<"$recovered_movie" >/dev/null

echo 'Docker recovery smoke passed: a populated baseline volume survived failed replacement readiness and migrated under the candidate image.'
