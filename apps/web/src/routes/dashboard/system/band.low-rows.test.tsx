import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { SystemOverview } from "../../../lib/api/types/system-overview";
import { ComputerCard, InstanceCard, NetworkCard, networkChecks } from "./band";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const health = { pass: 7, total: 9, need: 0, anyBad: false };
const network = (extra: object = {}) => ({
  adapter: "Ethernet", linkMbps: 1000, ip: "192.168.1.10", inBytesPerSec: 2_500_000, outBytesPerSec: 500_000,
  internet: true, dnsLookupMs: 12.4, nasMs: 3.2, nasName: "Media box", metadataMs: 150, clockInSync: true, ...extra
}) as SystemOverview["network"];

describe("System: instance card on a short band", () => {
  it("uses the short caption \"of N checks\" on a short band and the full caption otherwise", () => {
    const { unmount } = render(<InstanceCard instance={null} summary={undefined} health={health} short onJump={() => {}} />);
    expect(screen.getByText("of 9 checks")).toBeInTheDocument();
    expect(screen.queryByText("of 9 health checks pass")).toBeNull();
    unmount();
    render(<InstanceCard instance={null} summary={undefined} health={health} short={false} onJump={() => {}} />);
    expect(screen.getByText("of 9 health checks pass")).toBeInTheDocument();
  });

  it("shows only the whole fact rows that fit the room, so a short band shows fewer facts", () => {
    const height = vi.spyOn(HTMLElement.prototype, "clientHeight", "get");
    height.mockReturnValue(42);
    const small = render(<InstanceCard instance={null} summary={undefined} health={health} short onJump={() => {}} />);
    expect(small.getByRole("group", { name: "Facts about this instance" }).style.height).toBe("42px");
    small.unmount();
    height.mockReturnValue(47 * 3);
    const tall = render(<InstanceCard instance={null} summary={undefined} health={health} short={false} onJump={() => {}} />);
    expect(tall.getByRole("group", { name: "Facts about this instance" }).style.height).toBe("136px");
  });
});

describe("System: network checks", () => {
  it("lists Internet, DNS lookup, the NAS by name, Metadata service and Clock, all fine when healthy", () => {
    expect(networkChecks(network())).toEqual([
      { name: "Internet", value: "reachable", ok: true },
      { name: "DNS lookup", value: "12 ms", ok: true },
      { name: "Media box", value: "3 ms", ok: true },
      { name: "Metadata service", value: "150 ms", ok: true },
      { name: "Clock", value: "in sync", ok: true }
    ]);
  });

  it("marks a failing internet, a metadata answer of 2 s or more and a clock out of sync, and hides unread checks", () => {
    expect(networkChecks(network({ internet: false, metadataMs: 2000, clockInSync: false, dnsLookupMs: null, nasMs: null }))).toEqual([
      { name: "Internet", value: "not reachable", ok: false },
      { name: "Metadata service", value: "2000 ms", ok: false },
      { name: "Clock", value: "out of sync", ok: false }
    ]);
    expect(networkChecks(network({ nasName: null }))[2].name).toBe("Your NAS");
    expect(networkChecks(network({ metadataMs: 1999 })).find((c) => c.name === "Metadata service")!.ok).toBe(true);
  });

  it("draws the adapter with inbound MB/s, the out, link speed and ip line, and a warning mark on a failing check", () => {
    const overview = { network: network({ internet: false }), history: { cpu: [], memory: [], disk: [], netIn: [1, 2, 3] } } as unknown as SystemOverview;
    const { container } = render(<NetworkCard overview={overview} />);
    const col = container.querySelector(".sy-net2 .sy-col")!;
    expect(col.querySelector(".sy-mh")!.textContent).toBe("Ethernet");
    expect(col.querySelector(".sy-v")!.textContent).toBe("2.5 MB/s in");
    expect(col.querySelector(".sy-sub")!.textContent).toBe("↑ 0.5 out · 1 Gb/s · 192.168.1.10");
    const warn = container.querySelector(".sy-reach .sy-warn")!;
    expect(warn.textContent).toBe("!Internetnot reachable");
    expect(container.querySelectorAll(".sy-reach span:not(.sy-warn) em")[0].textContent).toBe("✓");
  });
});

describe("System: this computer, unreadable readings", () => {
  it("leaves out a reboot state and CPU temperature the machine did not give, and draws a trace per column", () => {
    const overview = {
      machine: { os: "Linux", uptimeSeconds: 7_200, rebootPending: null, cpuTemperatureC: null, cpuPercent: 10, cores: 4, memoryUsedBytes: 1e9, memoryTotalBytes: 8e9, diskReadBytesPerSec: 0, diskWriteBytesPerSec: 0, diskIops: 0 },
      instance: null, history: { cpu: [1, 2, 3], memory: [4, 5, 6], disk: [0, 0, 1], netIn: [] }
    } as unknown as SystemOverview;
    const { container } = render(<ComputerCard overview={overview} />);
    expect(Array.from(container.querySelectorAll(".sy-machine span")).map((s) => s.textContent)).toEqual(["Linux", "up 2 h"]);
    expect(container.querySelectorAll(".sy-col svg").length).toBe(3);
    expect(screen.getByText("last 10 minutes")).toBeInTheDocument();
  });
});
