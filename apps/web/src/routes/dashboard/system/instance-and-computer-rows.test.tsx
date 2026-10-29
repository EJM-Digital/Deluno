import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { SystemOverview } from "../../../lib/api/types/system-overview";
import { ComputerCard, InstanceCard, NetworkCard } from "./band";

afterEach(cleanup);

const health = (pass: number, total: number, need: number, anyBad: boolean) => ({ pass, total, need, anyBad });

describe("System: This instance", () => {
  it("shows a ring of \"N of M health checks pass\", amber when a problem is bad and green otherwise", () => {
    const { container, unmount } = render(<InstanceCard instance={null} summary={undefined} health={health(7, 9, 2, true)} short={false} onJump={() => {}} />);
    expect(screen.getByText("of 9 health checks pass")).toBeInTheDocument();
    const ring = container.querySelector<SVGCircleElement>("circle.sy-val")!;
    expect(ring.style.stroke).toBe("var(--sy-warning)");
    unmount();

    const calm = render(<InstanceCard instance={null} summary={undefined} health={health(9, 9, 0, false)} short={false} onJump={() => {}} />);
    expect(screen.getByText("of 9 health checks pass")).toBeInTheDocument();
    expect(calm.container.querySelector<SVGCircleElement>("circle.sy-val")!.style.stroke).toBe("var(--sy-success)");
  });

  it("shows \"up 3 h 2 min\" from the instance start", () => {
    const started = new Date(Date.now() - (3 * 3600 + 2 * 60) * 1000).toISOString();
    const instance = { startedUtc: started, version: "1.0.0", latest: true, cpuPercent: 1, memoryBytes: 1, answerMs: 2, answerSlowestMs: 3, workersBusy: 0, oldestWaitingSeconds: null, jobsToday: 0, jobsFailedToday: 0, requestsToday: 0, requestErrorsToday: 0, runsAs: "service", dataBytes: 0, databaseCount: 1, lastBackupUtc: null, lastBackupBytes: null, restartsThisWeek: 0, browsersLive: 0 } as unknown as SystemOverview["instance"];
    render(<InstanceCard instance={instance} summary={undefined} health={health(9, 9, 0, false)} short={false} onJump={() => {}} />);
    expect(screen.getByText("up 3 h 2 min")).toBeInTheDocument();
  });

  it("shows \"N need you\" as a jump that calls onJump, and \"All good\" with no link when nothing needs a person", () => {
    const onJump = vi.fn();
    const { unmount } = render(<InstanceCard instance={null} summary={undefined} health={health(7, 9, 2, true)} short={false} onJump={onJump} />);
    const link = screen.getByRole("link", { name: "2 need you →" });
    fireEvent.click(link);
    expect(onJump).toHaveBeenCalledTimes(1);
    unmount();

    render(<InstanceCard instance={null} summary={undefined} health={health(9, 9, 0, false)} short={false} onJump={onJump} />);
    expect(screen.getByText("All good")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /need you/ })).toBeNull();
    expect(onJump).toHaveBeenCalledTimes(1);
  });
});

describe("System: This computer", () => {
  const overview = {
    machine: { os: "Windows 11", uptimeSeconds: 7_200, rebootPending: true, cpuTemperatureC: 61.4, cpuPercent: 42.4, cores: 8, memoryUsedBytes: 4_000_000_000, memoryTotalBytes: 16_000_000_000, diskReadBytesPerSec: 2_000_000, diskWriteBytesPerSec: 3_000_000, diskIops: 120 },
    instance: { cpuPercent: 3.2, memoryBytes: 250_000_000 },
    history: { cpu: [], memory: [], disk: [] }
  } as unknown as SystemOverview;

  it("reads CPU with its cores and Deluno's share, memory of the total with Deluno's share, and the disk rate with read, write and IOPS", () => {
    const { container } = render(<ComputerCard overview={overview} />);
    const card = screen.getByRole("region", { name: "This computer" });
    const cpu = container.querySelectorAll<HTMLElement>(".sy-col")[0];
    expect(within(cpu).getByText("42")).toBeInTheDocument();
    expect(cpu.querySelector(".sy-sub")!.textContent).toBe("8 cores · Deluno 3%");
    const memory = container.querySelectorAll<HTMLElement>(".sy-col")[1];
    expect(memory.querySelector(".sy-sub")!.textContent).toBe("of 14.9 GB · Deluno 238 MB");
    const disk = container.querySelectorAll<HTMLElement>(".sy-col")[2];
    expect(disk.querySelector(".sy-sub")!.textContent).toBe("read 2.0 · write 3.0 · 120 IOPS");
    expect(within(card).getByText("This computer")).toBeInTheDocument();
  });

  it("puts the machine's own tags under the columns: OS, uptime, reboot state and CPU temperature", () => {
    const { container } = render(<ComputerCard overview={overview} />);
    const tags = Array.from(container.querySelectorAll(".sy-machine span")).map((span) => span.textContent);
    expect(tags).toEqual(["Windows 11", "up 2 h", "reboot pending", "CPU 61 °C"]);
  });

  it("says \"Deluno has not read the network yet.\" on Network when there is no reading", () => {
    render(<NetworkCard overview={null} />);
    expect(screen.getByText("Deluno has not read the network yet.")).toBeInTheDocument();
  });

  it("says \"Deluno has not read this computer yet\" with the first-reading note when there is no reading", () => {
    render(<ComputerCard overview={null} />);
    expect(screen.getByText("Deluno has not read this computer yet. The first reading arrives within a few seconds.")).toBeInTheDocument();
  });
});
