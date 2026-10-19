#!/usr/bin/env bash
# Exercises the shipped container image the way a person running Deluno in Docker uses it: the tools baked into the image, the
# environment settings, the container's own health report, a clean stop, and backup and restore across containers with and
# without the master key. Everything runs against the real image with real volumes; nothing is faked.
#
# Usage: bash scripts/test-docker-platform.sh <image>
# Needs docker, curl, jq and unzip. Each check prints "PASS: ..." and a failed check ends the script with the container's log.
set -euo pipefail

image="${1:?Usage: test-docker-platform.sh <image>}"
suffix="${GITHUB_RUN_ID:-local}-$$"
port_base="${DELUNO_DOCKER_TEST_PORT_BASE:-18400}"
owner_user="docker-platform-owner"
owner_password="docker-platform-password"
containers=()
volumes=()
workdir="$(mktemp -d)"

cleanup() {
  result=$?
  if [[ "$result" -ne 0 ]]; then
    for container in "${containers[@]}"; do
      echo "--- log of ${container}" >&2
      docker logs --tail 40 "$container" >&2 2>&1 || true
    done
  fi
  for container in "${containers[@]}"; do docker rm --force "$container" >/dev/null 2>&1 || true; done
  for volume in "${volumes[@]}"; do docker volume rm "$volume" >/dev/null 2>&1 || true; done
  rm -rf "$workdir"
  trap - EXIT
  exit "$result"
}
trap cleanup EXIT

pass() { echo "PASS: $*"; }
fail() { echo "FAIL: $*" >&2; exit 1; }

new_volume() {
  local name="deluno-platform-$1-${suffix}"
  docker volume create "$name" >/dev/null
  volumes+=("$name")
  made_volume="$name"
}

# start_deluno <name> <host-port> <container-port> <volume-mount> [docker run args...]
start_deluno() {
  local name="deluno-platform-$1-${suffix}" host_port="$2" container_port="$3" mount="$4"
  shift 4
  docker rm --force "$name" >/dev/null 2>&1 || true
  containers+=("$name")
  docker run --detach --name "$name" --publish "127.0.0.1:${host_port}:${container_port}" --mount "$mount" "$@" "$image" >/dev/null
  started_container="$name"
}

wait_ready() {
  local url="$1" attempts="${2:-60}"
  for _ in $(seq 1 "$attempts"); do
    if curl --fail --silent "${url}/api/health/ready" >/dev/null 2>&1; then return 0; fi
    sleep 1.5
  done
  return 1
}

bootstrap_token() {
  curl --fail --silent --header 'Content-Type: application/json' \
    --data "{\"username\":\"${owner_user}\",\"displayName\":\"Owner\",\"password\":\"${owner_password}\"}" "$1/api/auth/bootstrap" | jq --exit-status --raw-output '.accessToken'
}

login_token() {
  curl --fail --silent --header 'Content-Type: application/json' \
    --data "{\"username\":\"${owner_user}\",\"password\":\"${owner_password}\"}" "$1/api/auth/login" | jq --exit-status --raw-output '.accessToken'
}

add_indexer() {
  curl --fail --silent --header "Authorization: Bearer $2" --header 'Content-Type: application/json' \
    --data '{"name":"Docker Indexer","protocol":"torznab","privacy":"private","baseUrl":"http://indexer.example.invalid/api","apiKey":"saved-api-key","priority":25,"categories":"2000","mediaScope":"both","isEnabled":true,"keepName":true,"routeToLibraries":true,"rssEnabled":false,"automaticSearchEnabled":false}' \
    "$1/api/indexers" >/dev/null
}

indexer_state() {
  curl --fail --silent --header "Authorization: Bearer $2" "$1/api/indexers" | jq --compact-output '.[0] | {name, secretUnreadable, secretProblem}'
}

new_key() { head -c 32 /dev/urandom | base64; }

# ---- DOK-15: ffmpeg and ffprobe in the image run, and read a real video
tools="$(docker run --rm --entrypoint sh "$image" -c '
  set -e
  ffmpeg -v error -f lavfi -i testsrc=duration=2:size=64x64:rate=5 -c:v mpeg4 /tmp/clip.mp4
  echo "dir=${DELUNO_FFMPEG_DIR}"
  ffprobe -v error -select_streams v:0 -show_entries stream=codec_name,width,height -show_entries format=duration -of default=nw=1 /tmp/clip.mp4')"
grep -q '^dir=/usr/bin$' <<<"$tools" || fail "DELUNO_FFMPEG_DIR is not /usr/bin in the image: $tools"
grep -q '^codec_name=mpeg4$' <<<"$tools" && grep -q '^width=64$' <<<"$tools" && grep -q '^duration=2' <<<"$tools" || fail "ffprobe did not read the clip ffmpeg made: $tools"
pass "ffmpeg makes a clip and ffprobe reads its codec, size and duration inside the image, and DELUNO_FFMPEG_DIR points at them"

# ---- DOK-07: the container's own health report moves from starting to healthy
new_volume health; vol_health="$made_volume"
port=$((port_base + 1))
start_deluno health "$port" 8080 "type=volume,src=${vol_health},dst=/data" --health-interval 2s --health-start-period 1s --health-timeout 5s
name="$started_container"
state=""
for _ in $(seq 1 60); do
  state="$(docker inspect --format '{{.State.Health.Status}}' "$name")"
  [[ "$state" == "healthy" ]] && break
  sleep 2
done
[[ "$state" == "healthy" ]] || fail "the container never reported healthy (last: ${state})"
pass "docker inspect reports the container healthy once Deluno answers"

# ---- DOK-10 / DAT-10: docker stop is clean and fast; the same volume opens again with the data and sign-in intact
base="http://127.0.0.1:${port}"
wait_ready "$base" || fail "no readiness on the health container"
token="$(bootstrap_token "$base")"
add_indexer "$base" "$token"
started=$SECONDS
docker stop --time 30 "$name" >/dev/null
elapsed=$((SECONDS - started))
exit_code="$(docker inspect --format '{{.State.ExitCode}}' "$name")"
[[ "$exit_code" == "0" ]] || fail "docker stop ended Deluno with exit code ${exit_code}, not 0"
[[ "$elapsed" -lt 20 ]] || fail "docker stop took ${elapsed}s; Deluno did not stop within its grace period"
pass "docker stop ends Deluno with exit code 0 in ${elapsed}s"
docker start "$name" >/dev/null
wait_ready "$base" || fail "the stopped container did not come back"
login_token "$base" >/dev/null || fail "the account written before the stop could not sign in after the restart"
pass "after docker stop and start the same volume still holds the account"
docker rm --force "$name" >/dev/null

# ---- DOK-12: the environment settings take effect (port, who may connect, where the data lives, which key)
new_volume env; vol_env="$made_volume"
port=$((port_base + 2))
env_key="$(new_key)"
start_deluno env "$port" 9191 "type=volume,src=${vol_env},dst=/data" \
  --env ASPNETCORE_URLS=http://+:9191 --env Server__Port=9191 --env Server__AllowLan=true --env Storage__DataRoot=/data/alt --env "DELUNO_MASTER_KEY=${env_key}" \
  --env DELUNO_METADATA_PROVIDER_MODE=direct --env DELUNO_METADATA_BROKER_URL=http://metadata.example.invalid/ --env TMDB_API_KEY=docker-test-key
name="$started_container"
wait_ready "http://127.0.0.1:${port}" || fail "Deluno did not answer on the port Server__Port named"
docker exec "$name" test -f /data/alt/platform.db || fail "Storage__DataRoot did not move the databases to /data/alt"
docker exec "$name" test ! -e /data/platform.db || fail "Deluno still wrote databases to the default folder"
pass "Server__Port and Storage__DataRoot move where Deluno listens and where its databases live"
env_token="$(bootstrap_token "http://127.0.0.1:${port}")"
metadata="$(curl --fail --silent --header "Authorization: Bearer ${env_token}" "http://127.0.0.1:${port}/api/metadata/status")"
jq --exit-status '.mode == "direct" and ([.sources[] | select(.source == "tmdb") | .isConfigured] == [true]) and ([.sources[] | select(.source == "broker") | .isConfigured] == [true])' <<<"$metadata" >/dev/null || fail "the metadata variables did not take effect: ${metadata}"
pass "DELUNO_METADATA_PROVIDER_MODE, DELUNO_METADATA_BROKER_URL and TMDB_API_KEY set the metadata mode, the broker and the direct TMDb key"
docker exec "$name" test ! -e /data/alt/secrets/master.key || fail "a master key file was written although DELUNO_MASTER_KEY was supplied"
env_log="$(docker logs "$name" 2>&1)"
grep -q 'source=auto:Linux:DELUNO_MASTER_KEY:env:DELUNO_MASTER_KEY' <<<"$env_log" || fail "Deluno did not take its master key from DELUNO_MASTER_KEY"
pass "DELUNO_MASTER_KEY is the key Deluno uses, and no key file is written beside the data"
docker rm --force "$name" >/dev/null

new_volume lan; vol_lan="$made_volume"
port=$((port_base + 3))
start_deluno lan "$port" 8080 "type=volume,src=${vol_lan},dst=/data" --env Server__AllowLan=false
name="$started_container"
for _ in $(seq 1 60); do
  docker exec "$name" curl --fail --silent http://127.0.0.1:8080/api/health/ready >/dev/null 2>&1 && break
  sleep 1.5
done
docker exec "$name" curl --fail --silent http://127.0.0.1:8080/api/health/ready >/dev/null || fail "Deluno with Server__AllowLan=false did not answer on its own loopback"
if curl --silent --max-time 5 "http://127.0.0.1:${port}/api/health/ready" >/dev/null 2>&1; then
  fail "Server__AllowLan=false still let a connection in from outside the container"
fi
pass "Server__AllowLan=false answers on the container's own loopback only; a published port gets nothing"
docker rm --force "$name" >/dev/null

# ---- BKP-16 / DOK-13: backup and restore across containers, and what the master key decides
new_volume backup-source; vol_src="$made_volume"
port=$((port_base + 4))
source_key="$(new_key)"
start_deluno backup-source "$port" 8080 "type=volume,src=${vol_src},dst=/data" --env "DELUNO_MASTER_KEY=${source_key}"
name="$started_container"
base="http://127.0.0.1:${port}"
wait_ready "$base" || fail "the backup source did not become ready"
token="$(bootstrap_token "$base")"
add_indexer "$base" "$token"
backup_id="$(curl --fail --silent --header "Authorization: Bearer ${token}" --header 'Content-Type: application/json' --data '{"reason":"manual"}' "$base/api/backups" | jq --exit-status --raw-output '.backup.id')"
curl --fail --silent --header "Authorization: Bearer ${token}" --output "${workdir}/backup.zip" "$base/api/backups/${backup_id}/download"
unzip -l "${workdir}/backup.zip" | grep -q 'platform.db' || fail "the downloaded backup has no platform.db"
docker rm --force "$name" >/dev/null
pass "a running container makes a backup and the download is a zip holding the databases"

restore_into() { # label key
  local volume
  new_volume "restore-$1"; volume="$made_volume"
  local restore_port=$((port_base + 5))
  local restored
  start_deluno "restore-$1" "$restore_port" 8080 "type=volume,src=${volume},dst=/data" --env "DELUNO_MASTER_KEY=$2"
  restored="$started_container"
  local restore_base="http://127.0.0.1:${restore_port}"
  wait_ready "$restore_base" || fail "the restore target ($1) did not become ready"
  local temp_token
  temp_token="$(bootstrap_token "$restore_base")"
  curl --fail --silent --header "Authorization: Bearer ${temp_token}" --form "file=@${workdir}/backup.zip" "$restore_base/api/backups/restore" | jq --exit-status '.restored == true' >/dev/null \
    || fail "the restore into the $1 container was refused"
  docker restart "$restored" >/dev/null
  sleep 3
  wait_ready "$restore_base" || fail "the restored container ($1) did not come back"
  restored_state="$(indexer_state "$restore_base" "$(login_token "$restore_base")")"
  docker rm --force "$restored" >/dev/null
}

restore_into same-key "$source_key"
[[ "$(jq --raw-output '.name' <<<"$restored_state")" == "Docker Indexer" ]] || fail "the restored volume lost the saved indexer: ${restored_state}"
[[ "$(jq --raw-output '.secretUnreadable' <<<"$restored_state")" == "false" ]] || fail "with the same DELUNO_MASTER_KEY the restored credentials were not readable: ${restored_state}"
pass "restore into a new container with the same DELUNO_MASTER_KEY brings back the indexer and its saved API key readable"

restore_into other-key "$(new_key)"
[[ "$(jq --raw-output '.secretUnreadable' <<<"$restored_state")" == "true" ]] || fail "with another DELUNO_MASTER_KEY the saved API key was not flagged: ${restored_state}"
jq --raw-output '.secretProblem' <<<"$restored_state" | grep -q "Enter it again" || fail "the unreadable key did not say to enter it again: ${restored_state}"
pass "restore with another DELUNO_MASTER_KEY keeps the indexer but flags its API key 'Enter it again', in plain words"

# A container recreated without DELUNO_MASTER_KEY and without a key file on the volume reads nothing it saved before.
new_volume recreate; vol_recreate="$made_volume"
port=$((port_base + 6))
start_deluno recreate "$port" 8080 "type=volume,src=${vol_recreate},dst=/data" --env "DELUNO_MASTER_KEY=$(new_key)"
name="$started_container"
base="http://127.0.0.1:${port}"
wait_ready "$base" || fail "the first container did not become ready"
token="$(bootstrap_token "$base")"
add_indexer "$base" "$token"
docker rm --force "$name" >/dev/null
start_deluno recreate-again "$port" 8080 "type=volume,src=${vol_recreate},dst=/data"
name="$started_container"
wait_ready "$base" || fail "the recreated container did not become ready"
recreated_state="$(indexer_state "$base" "$(login_token "$base")")"
[[ "$(jq --raw-output '.secretUnreadable' <<<"$recreated_state")" == "true" ]] || fail "a container recreated without its master key still read the saved API key: ${recreated_state}"
jq --raw-output '.secretProblem' <<<"$recreated_state" | grep -q "Enter it again" || fail "the recreated container did not say to enter the key again: ${recreated_state}"
pass "a container recreated without DELUNO_MASTER_KEY and without a key file flags every saved credential 'Enter it again'"

echo "Docker platform checks passed against ${image}."
