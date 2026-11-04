// node --test scripts/docker-container.test.mjs
// Two things only a running container shows, both on the image built by the "Container image" job of ci.yml (DELUNO_DOCKER_IMAGE):
//   1. the configuration a deployment passes in reaches the app: server port, LAN access, data root and master key are honoured,
//      and no secret comes back through the API or the log;
//   2. a Docker stack with separate /downloads and /media mounts: the library health row says they are different volumes, the
//      import preview does not promise a hard link between them, and an import across them lands the whole film (#1464).
// Each container is started with docker run (startContainer below) from the image CI just built.
// Every request is made from inside the container (docker exec curl), so no host port is taken and two jobs never collide.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { randomBytes } from "node:crypto";
import { test } from "node:test";

const image = process.env.DELUNO_DOCKER_IMAGE;
// Only where an image was built and named: the "Container image" job of ci.yml sets DELUNO_DOCKER_IMAGE. Anywhere else the tests are skipped, with this reason.
const skip = image ? false : "DELUNO_DOCKER_IMAGE names no image: these tests start the image the Container image job builds.";
const unique = `${process.pid}-${randomBytes(3).toString("hex")}`;

function docker(args, input) {
  const result = spawnSync("docker", args, { encoding: "utf8", input });
  return { status: result.status, out: `${result.stdout ?? ""}`.trim(), err: `${result.stderr ?? ""}`.trim() };
}

function sleep(seconds) {
  spawnSync(process.execPath, ["-e", `setTimeout(()=>{},${seconds * 1000})`]);
}

function waitFor(check, seconds, what) {
  const until = Date.now() + seconds * 1000;
  while (Date.now() < until) {
    if (check()) return;
    sleep(2);
  }
  assert.fail(`${what} did not happen within ${seconds} s`);
}

// A request made from inside the container; returns { status, body }.
function request(container, port, method, path, { body, token } = {}) {
  const args = ["exec", "-i", container, "curl", "-sS", "-m", "60", "-X", method, "-o", "-", "-w", "\n%{http_code}", `http://127.0.0.1:${port}${path}`, "-H", "Content-Type: application/json"];
  if (token) args.push("-H", `Authorization: Bearer ${token}`);
  if (body !== undefined) args.push("--data-binary", "@-");
  const result = docker(args, body === undefined ? undefined : JSON.stringify(body));
  const cut = result.out.lastIndexOf("\n");
  const text = cut < 0 ? "" : result.out.slice(0, cut);
  const status = Number(result.out.slice(cut + 1));
  let json = null;
  try { json = JSON.parse(text); } catch { /* not JSON */ }
  return { status, text, json };
}

function startContainer(name, extraArgs, env) {
  const args = ["run", "--detach", "--name", name, ...extraArgs];
  for (const [key, value] of Object.entries(env)) args.push("--env", `${key}=${value}`);
  args.push(image);
  const started = docker(args);
  assert.equal(started.status, 0, `could not start ${name}: ${started.err}`);
}

function ready(name, port) {
  return docker(["exec", name, "curl", "--fail", "--silent", `http://127.0.0.1:${port}/api/health/ready`]).status === 0;
}

function signIn(name, port) {
  const username = "container-owner";
  const password = "container-owner-password";
  const made = request(name, port, "POST", "/api/auth/bootstrap", { body: { username, displayName: "Container owner", password } });
  assert.equal(made.status, 200, `the first account: ${made.text}`);
  return made.json.accessToken;
}

function removeAll(names, volumes = []) {
  for (const name of names) docker(["rm", "--force", name]);
  for (const volume of volumes) docker(["volume", "rm", volume]);
}

function containerAddress(name) {
  return docker(["inspect", "--format", "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}", name]).out;
}

function reachableFromAnotherContainer(address, port) {
  // The same image has curl (its healthcheck uses it); a second container on the default network is "another device".
  const result = docker(["run", "--rm", "--entrypoint", "curl", image, "-sS", "-m", "8", "-o", "/dev/null", "-w", "%{http_code}", `http://${address}:${port}/api/health/ready`]);
  return result.out === "200";
}

test("container configuration reaches the app: the server port, the data root and the master key are honoured, nothing secret comes back, and AllowLan decides whether another device can reach it", { skip }, () => {
  const masterKey = randomBytes(32).toString("base64");
  const tmdbKey = `tmdb-${randomBytes(8).toString("hex")}`;
  const lan = `deluno-cfg-lan-${unique}`;
  const local = `deluno-cfg-local-${unique}`;
  const common = {
    ASPNETCORE_URLS: "http://+:9191",
    Server__Port: "9191",
    Storage__DataRoot: "/custom-data",
    DELUNO_MASTER_KEY: masterKey,
    TMDB_API_KEY: tmdbKey,
  };
  const mounts = ["--mount", "type=tmpfs,dst=/custom-data,tmpfs-mode=1777"];
  try {
    startContainer(lan, mounts, { ...common, Server__AllowLan: "true" });
    waitFor(() => ready(lan, 9191), 120, "the app answering on the configured port 9191");

    assert.notEqual(docker(["exec", lan, "curl", "--silent", "-m", "3", "--fail", "http://127.0.0.1:8080/api/health/ready"]).status, 0, "nothing answers on the default port 8080");
    const files = docker(["exec", lan, "sh", "-c", "ls /custom-data"]).out;
    assert.match(files, /platform\.db/, "the databases are in the configured data root");
    assert.equal(docker(["exec", lan, "sh", "-c", "ls /data 2>/dev/null | grep -c '\\.db$' || true"]).out, "0", "and none in the default /data");
    assert.equal(docker(["exec", lan, "sh", "-c", "ls /custom-data/secrets 2>/dev/null | grep -c master || true"]).out, "0", "the master key comes from the environment, so no key file is written");

    assert.ok(reachableFromAnotherContainer(containerAddress(lan), 9191), "AllowLan=true: another device on the network reaches it");

    const token = signIn(lan, 9191);
    for (const path of ["/api/settings", "/api/updates/status", "/api/system/health", "/api/health/ready"]) {
      const answer = request(lan, 9191, "GET", path, { token });
      assert.ok(answer.status < 500, `${path} answered ${answer.status}`);
      assert.ok(!answer.text.includes(masterKey), `${path} never returns the master key`);
      assert.ok(!answer.text.includes(tmdbKey), `${path} never returns the metadata key`);
    }
    const log = docker(["logs", lan]);
    assert.ok(!`${log.out}${log.err}`.includes(masterKey), "the log never holds the master key");

    startContainer(local, mounts, { ...common, Server__AllowLan: "false" });
    waitFor(() => ready(local, 9191), 120, "the second app answering");
    assert.ok(!reachableFromAnotherContainer(containerAddress(local), 9191), "AllowLan=false: another device does not reach it");
  } finally {
    removeAll([lan, local]);
  }
});

test("separate /downloads and /media mounts: the health row says they are different volumes, the preview promises no hard link across them, and the import lands the whole film", { skip }, () => {
  const name = `deluno-mounts-${unique}`;
  const volume = `${name}-data`;
  const mounts = [
    "--mount", `type=volume,src=${volume},dst=/data`,
    "--mount", "type=tmpfs,dst=/downloads,tmpfs-mode=1777",
    "--mount", "type=tmpfs,dst=/media,tmpfs-mode=1777",
  ];
  try {
    docker(["volume", "create", volume]);
    startContainer(name, mounts, { Server__Port: "8080", Server__AllowLan: "true", Storage__DataRoot: "/data" });
    waitFor(() => ready(name, 8080), 120, "the app answering");
    const token = signIn(name, 8080);

    const sh = (script) => docker(["exec", name, "sh", "-c", script]);
    sh("mkdir -p /media/movies /media/library-b /media/downloads /downloads/Film.2026.1080p.WEB-DL.x264-GOLDEN /downloads/Same.2027.1080p.WEB-DL.x264-GOLDEN");
    // A short real video (the image carries ffmpeg), so the import's own check can read it.
    const film = "/downloads/Film.2026.1080p.WEB-DL.x264-GOLDEN/Film.2026.1080p.WEB-DL.x264-GOLDEN.mkv";
    const makeVideo = (target) => sh(`ffmpeg -loglevel error -y -f lavfi -i testsrc=duration=130:size=64x48:rate=1 -f lavfi -i sine=duration=130 -c:v mpeg4 -c:a aac -shortest "${target}"`);
    assert.equal(makeVideo(film).status, 0, "a test video is made");
    assert.equal(makeVideo("/media/downloads/Same.2027.1080p.WEB-DL.x264-GOLDEN.mkv").status, 0);
    const filmSize = sh(`stat -c %s "${film}"`).out;
    sh("touch -d '1 hour ago' /downloads/Film.2026.1080p.WEB-DL.x264-GOLDEN/*.mkv /media/downloads/*.mkv");

    const profile = request(name, 8080, "POST", "/api/quality-profiles", {
      token,
      body: { name: "Container profile", mediaType: "movies", cutoffQuality: "WEB 1080p", allowedQualities: "WEB 720p,WEB 1080p", customFormatIds: "", upgradeUntilCutoff: true, upgradeUnknownItems: false },
    });
    assert.equal(profile.status, 200, `a quality profile: ${profile.text}`);
    const qualityProfileId = profile.json.id;
    const makeLibrary = (libraryName, rootPath, downloadsPath) => {
      const made = request(name, 8080, "POST", "/api/libraries", {
        token,
        body: {
          name: libraryName, mediaType: "movies", purpose: "Main library", rootPath, downloadsPath, qualityProfileId,
          autoSearchEnabled: false, missingSearchEnabled: false, upgradeSearchEnabled: false, removeEmptySourceFolders: false,
          searchIntervalMinutes: 720, retryDelayHours: 6, maxItemsPerRun: 10,
        },
      });
      assert.equal(made.status, 200, `making ${libraryName}: ${made.text}`);
      return made.json;
    };
    // Separate mounts: downloads on /downloads, the library on /media. One mount: both under /media.
    const apart = makeLibrary("Apart mounts", "/media/movies", "/downloads");
    const together = makeLibrary("One mount", "/media/library-b", "/media/downloads");

    assert.equal(request(name, 8080, "POST", "/api/system/health/run", { token }).status < 300, true);
    let rows = [];
    waitFor(() => {
      rows = request(name, 8080, "GET", "/api/system/health", { token }).json ?? [];
      rows = Array.isArray(rows) ? rows : rows.items ?? rows.checks ?? [];
      return rows.some((row) => row.id === `library:${apart.id}:volumes`) && rows.some((row) => row.id === `library:${together.id}:volumes`);
    }, 90, "the library health rows");
    const rowOf = (library) => rows.find((row) => row.id === `library:${library.id}:volumes`);
    assert.match(rowOf(apart).message, /different volumes/, "downloads on one mount and the library on another are different volumes");
    assert.match(rowOf(together).message, /share a volume/, "two folders on one mount are one volume");

    const preview = (library, source, title, year) => request(name, 8080, "POST", "/api/filesystem/import/preview", {
      token,
      body: { sourcePath: source, fileName: source.split("/").pop(), mediaType: "movies", title, year, genres: [], tags: [], libraryId: library.id },
    });
    const across = preview(apart, "/downloads/Film.2026.1080p.WEB-DL.x264-GOLDEN/Film.2026.1080p.WEB-DL.x264-GOLDEN.mkv", "Film", 2026);
    assert.equal(across.status, 200, across.text);
    assert.equal(across.json.hardlinkAvailable, false, "no hard link is promised across the two mounts");
    const within = preview(together, "/media/downloads/Same.2027.1080p.WEB-DL.x264-GOLDEN.mkv", "Same", 2027);
    assert.equal(within.status, 200, within.text);
    assert.equal(within.json.hardlinkAvailable, true, "a hard link is promised inside one mount");

    // The import across the mounts, asked for as a hard link with the copy fallback allowed: the whole film lands.
    const movie = request(name, 8080, "POST", "/api/movies", { token, body: { title: "Film", releaseYear: 2026, imdbId: null, monitored: false, libraryId: apart.id } });
    assert.ok(movie.status < 300, movie.text);
    const run = request(name, 8080, "POST", "/api/filesystem/import/execute-batch", {
      token,
      body: { items: [{ preview: across.json.request ?? {
        sourcePath: "/downloads/Film.2026.1080p.WEB-DL.x264-GOLDEN/Film.2026.1080p.WEB-DL.x264-GOLDEN.mkv",
        fileName: "Film.2026.1080p.WEB-DL.x264-GOLDEN.mkv", mediaType: "movies", title: "Film", year: 2026, genres: [], tags: [], libraryId: apart.id,
      }, transferMode: "hardlink", overwrite: false, allowCopyFallback: true }] },
    });
    const item = run.json?.results?.[0];
    assert.ok(item?.succeeded, `the import across the mounts: ${run.text}`);
    assert.equal(item.response.usedFallback, true, "it says it copied because the hard link could not cross");
    assert.equal(item.response.transferModeUsed, "copy");
    const landed = sh("find /media/movies -name '*.mkv' | head -1").out;
    assert.ok(landed, "the film is in the library");
    assert.equal(sh(`stat -c %s "${landed}"`).out, filmSize, "and it is whole (the same size as the source)");
    assert.equal(sh("find /media/movies -name '*.deluno*' -o -name '*.tmp' | head -1").out, "", "no staging file is left");
  } finally {
    removeAll([name], [volume]);
  }
});
