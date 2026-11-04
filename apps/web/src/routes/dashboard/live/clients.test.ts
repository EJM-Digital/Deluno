import { describe, expect, it } from "vitest";
import type { DownloadClientItem } from "../../../lib/api";
import type { DashboardClientRow } from "../../../lib/api/types/operations";
import { clientLights, clientMeta, clientsSummary, type ClientLight } from "./clients";

function row(overrides: Partial<DashboardClientRow> = {}): DashboardClientRow {
  return { id: "c1", name: "qBittorrent", protocol: "qbittorrent", reachable: true, paused: false, version: null, warnings: 0, freeSpaceBytes: null, healthStatus: "ok", active: 0, queued: 0, ...overrides } as DashboardClientRow;
}
function client(overrides: Partial<DownloadClientItem> = {}): DownloadClientItem {
  return { id: "c1", name: "qBittorrent", isEnabled: true, ...overrides } as DownloadClientItem;
}
const light = (overrides: Partial<ClientLight> = {}): ClientLight => ({ id: "c1", name: "q", tone: "ok", moving: false, words: "", speedMbps: 0, seeding: 0, ...overrides });

describe("one light for each download client", () => {
  it("is grey and says switched off for a client that is turned off, whatever it reads", () => {
    const [one] = clientLights([row()], [client({ isEnabled: false })], new Map([["c1", 5]]), new Map([["c1", 3]]));
    expect(one).toEqual({ id: "c1", name: "qBittorrent", tone: "idle", moving: false, words: "switched off", speedMbps: 0, seeding: 3 });
  });

  it("is red and says not answering for a client that does not answer", () => {
    const [one] = clientLights([row({ reachable: false })], [client()], new Map([["c1", 5]]), new Map([["c1", 2]]));
    expect(one).toEqual({ id: "c1", name: "qBittorrent", tone: "bad", moving: false, words: "not answering", speedMbps: 0, seeding: 2 });
  });

  it("is amber and says paused for a client with its own pause on", () => {
    const [one] = clientLights([row({ paused: true })], [client()], new Map([["c1", 5]]));
    expect(one).toEqual({ id: "c1", name: "qBittorrent", tone: "warn", moving: false, words: "paused", speedMbps: 0, seeding: 0 });
  });

  it("is blue and pulsing with its speed while it moves data", () => {
    const [one] = clientLights([row()], [client()], new Map([["c1", 4.25]]), new Map([["c1", 7]]));
    expect(one).toEqual({ id: "c1", name: "qBittorrent", tone: "info", moving: true, words: "moving 4.3 MB/s", speedMbps: 4.25, seeding: 7 });
    expect(clientLights([row()], [client()], new Map([["c1", 42]]))[0].words).toBe("moving 42 MB/s");
  });

  it("is green when it answers and nothing moves, saying whether it has downloads waiting", () => {
    expect(clientLights([row({ active: 2 })], [client()], new Map())[0]).toMatchObject({ tone: "ok", moving: false, words: "connected, nothing moving", speedMbps: 0 });
    expect(clientLights([row({ active: 0 })], [client()], new Map())[0].words).toBe("connected, idle");
    expect(clientLights([], [client()], new Map())[0]).toMatchObject({ tone: "ok", words: "connected, idle" });
  });

  it("is green for a client the summary has no row for, rather than red", () => {
    expect(clientLights([], [client()], new Map())[0].tone).toBe("ok");
  });

  it("counts a client as not moving at a speed of exactly nothing", () => {
    expect(clientLights([row()], [client()], new Map([["c1", 0]]))[0].moving).toBe(false);
  });

  it("shows a client the summary reads but the list does not hold yet, in each state, without the pause or the busy wording a listed client gets", () => {
    const rows = [
      row({ id: "x1", name: "Alpha", reachable: false }),
      row({ id: "x2", name: "Bravo", paused: true }),
      row({ id: "x3", name: "Charlie" }),
      row({ id: "x4", name: "Delta", active: 3 }),
      row({ id: "x5", name: "Echo" })
    ];
    const lights = clientLights(rows, [], new Map([["x3", 12], ["x5", 0]]), new Map([["x1", 1], ["x2", 2], ["x3", 3], ["x4", 4], ["x5", 5]]));

    expect(lights.find((l) => l.id === "x1")).toEqual({ id: "x1", name: "Alpha", tone: "bad", moving: false, words: "not answering", speedMbps: 0, seeding: 1 });
    expect(lights.find((l) => l.id === "x2")).toEqual({ id: "x2", name: "Bravo", tone: "ok", moving: false, words: "connected, idle", speedMbps: 0, seeding: 2 });
    expect(lights.find((l) => l.id === "x3")).toEqual({ id: "x3", name: "Charlie", tone: "info", moving: true, words: "moving 12 MB/s", speedMbps: 12, seeding: 3 });
    expect(lights.find((l) => l.id === "x4")).toMatchObject({ tone: "ok", words: "connected, idle", seeding: 4 });
    expect(lights.find((l) => l.id === "x5")).toMatchObject({ tone: "ok", words: "connected, idle", seeding: 5 });
  });

  it("does not show a client twice when the list holds it as well", () => {
    expect(clientLights([row()], [client()], new Map())).toHaveLength(1);
  });

  it("drops the machine name when every client is on the same machine, and keeps it when they are on several", () => {
    const one = clientLights([row({ id: "a", name: "qBittorrent on box" })], [client({ id: "a", name: "qBittorrent on box" })], new Map());
    expect(one[0].name).toBe("qBittorrent");
    const two = clientLights(
      [row({ id: "a", name: "qBittorrent on box" }), row({ id: "b", name: "qBittorrent on nas" })],
      [client({ id: "a", name: "qBittorrent on box" }), client({ id: "b", name: "qBittorrent on nas" })],
      new Map()
    );
    expect(two.map((l) => l.name)).toEqual(["qBittorrent on box", "qBittorrent on nas"]);
  });

  it("puts the clients that are moving first, then the rest by name", () => {
    const lights = clientLights(
      [row({ id: "a", name: "Zulu" }), row({ id: "b", name: "Alpha" }), row({ id: "c", name: "Mike" })],
      [client({ id: "a", name: "Zulu" }), client({ id: "b", name: "Alpha" }), client({ id: "c", name: "Mike" })],
      new Map([["a", 3]])
    );
    expect(lights.map((l) => l.name)).toEqual(["Zulu", "Alpha", "Mike"]);
  });

  it("puts a name that sorts first ahead of one that sorts later when neither moves, and a moving one ahead of both", () => {
    const lights = clientLights([], [client({ id: "b", name: "Bravo" }), client({ id: "a", name: "Alpha" })], new Map());
    expect(lights.map((l) => l.name)).toEqual(["Alpha", "Bravo"]);
  });
});

describe("the lights in the mixed cases", () => {
  it("skips only the summary rows the list already holds, and shows the others", () => {
    const lights = clientLights([row({ id: "a", name: "Alpha" }), row({ id: "b", name: "Bravo" })], [client({ id: "a", name: "Alpha" })], new Map());
    expect(lights.map((l) => l.id)).toEqual(["a", "b"]);
  });

  it("keeps the seeding count on a green light", () => {
    expect(clientLights([row()], [client()], new Map(), new Map([["c1", 9]]))[0].seeding).toBe(9);
  });

  it("keeps the machine name when one machine is only in the summary and the other only in the list", () => {
    const lights = clientLights([row({ id: "a", name: "Qbit on box" })], [client({ id: "b", name: "Sab on nas" })], new Map());
    expect(lights.map((l) => l.name).sort()).toEqual(["Qbit on box", "Sab on nas"]);
    const other = clientLights([row({ id: "b", name: "Sab on nas" })], [client({ id: "a", name: "Qbit on box" })], new Map());
    expect(other.map((l) => l.name).sort()).toEqual(["Qbit on box", "Sab on nas"]);
  });

  it("falls back to the client name when naming it shortens to nothing", () => {
    expect(clientLights([], [client({ id: "a", name: "" })], new Map())[0].name).toBe("");
    expect(clientLights([row({ id: "a", name: "" })], [], new Map())[0].name).toBe("");
  });

  it("puts every moving client ahead of every quiet one, whatever their names", () => {
    const lights = clientLights(
      [],
      [client({ id: "a", name: "Alpha" }), client({ id: "b", name: "Bravo" }), client({ id: "c", name: "Charlie" }), client({ id: "d", name: "Delta" })],
      new Map([["c", 1], ["d", 2]])
    );
    expect(lights.map((l) => l.name)).toEqual(["Charlie", "Delta", "Alpha", "Bravo"]);
  });
});

describe("lights for clients with nothing seeding", () => {
  it("count zero seeding for a client the seeding counts do not mention, in every state", () => {
    expect(clientLights([row()], [client({ isEnabled: false })], new Map())[0].seeding).toBe(0);
    expect(clientLights([row({ reachable: false })], [client()], new Map())[0].seeding).toBe(0);
    expect(clientLights([row({ paused: true })], [client()], new Map())[0].seeding).toBe(0);
    expect(clientLights([row()], [client()], new Map([["c1", 2]]))[0].seeding).toBe(0);
    expect(clientLights([row({ id: "z", reachable: false })], [], new Map())[0].seeding).toBe(0);
    expect(clientLights([row({ id: "z" })], [], new Map([["z", 2]]))[0].seeding).toBe(0);
    expect(clientLights([row({ id: "z" })], [], new Map())[0].seeding).toBe(0);
  });
});

describe("the clients header", () => {
  it("counts the clients that are moving and the torrents they are seeding", () => {
    expect(clientsSummary([light({ moving: true, seeding: 1200 }), light({ seeding: 14 }), light({ moving: true })])).toBe("2 moving · 1,214 seeding");
    expect(clientsSummary([])).toBe("0 moving · 0 seeding");
  });
});

describe("a client's second line", () => {
  it("gives the speed while it moves, with the seeding count when there is one", () => {
    expect(clientMeta(light({ moving: true, speedMbps: 3.5, seeding: 1200 }))).toBe("3.5 MB/s · 1,200 seeding");
    expect(clientMeta(light({ moving: true, speedMbps: 20 }))).toBe("20 MB/s");
  });

  it("says why a client that is not moving is quiet", () => {
    expect(clientMeta(light({ tone: "idle", seeding: 4 }))).toBe("switched off");
    expect(clientMeta(light({ tone: "bad", seeding: 4 }))).toBe("not answering");
    expect(clientMeta(light({ tone: "warn", seeding: 4 }))).toBe("paused");
  });

  it("gives the seeding count for a client that answers and is not moving, or idle when it seeds nothing", () => {
    expect(clientMeta(light({ seeding: 4 }))).toBe("4 seeding");
    expect(clientMeta(light())).toBe("idle");
  });
});
