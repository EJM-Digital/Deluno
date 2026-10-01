/**
 * Storage (#1098), the top-right card: per drive its free space and when it will be full, a bar of Deluno's own share and
 * everything else, read and write speed, IOPS, how busy it is, and the drive's own health. From the overview's `drives`;
 * without it the Dashboard summary's volumes stand in (free space only: no reading is made up).
 */
import type { JSX } from "react";
import { Link } from "react-router-dom";
import type { DashboardSummary } from "../../../lib/api";
import type { SystemOverviewDrive } from "../../../lib/api/types/system-overview";
import { fullIn, mbps, plainCount, sizeText } from "./logic";

export interface DriveView {
  key: string;
  name: string;
  path: string;
  total: number;
  free: number;
  mine: number;
  fullInDays: number | null;
  io: { read: number; write: number; iops: number; busy: number } | null;
  health: SystemOverviewDrive["health"];
  healthNote: string | null;
}

export function drivesOf(overview: SystemOverviewDrive[] | undefined, summary: DashboardSummary | undefined): DriveView[] {
  if (overview && overview.length > 0) {
    return overview.map((drive) => ({ key: `${drive.name}:${drive.path}`, name: drive.name, path: drive.path, total: drive.totalBytes, free: drive.freeBytes, mine: drive.delunoBytes, fullInDays: drive.fullInDays, io: { read: drive.readBytesPerSec, write: drive.writeBytesPerSec, iops: drive.iops, busy: drive.busyPercent }, health: drive.health, healthNote: drive.healthNote }));
  }
  return (summary?.storage?.volumes ?? [])
    .filter((volume) => volume.totalBytes && volume.freeBytes !== null)
    .map((volume) => ({ key: volume.id, name: volume.label, path: volume.paths[0] ?? "", total: volume.totalBytes ?? 0, free: volume.freeBytes ?? 0, mine: 0, fullInDays: null, io: null, health: "unknown" as const, healthNote: null }));
}

function freeText(bytes: number): string {
  return sizeText(bytes) || "0 B";
}

export function StorageCard({ drives }: { drives: DriveView[] }): JSX.Element {
  const free = drives.reduce((sum, drive) => sum + drive.free, 0);
  return (
    <section className="sy-card sy-store" aria-label="Storage">
      <div className="sy-card-h">
        <span className="sy-label">Storage</span>
        <span className="sy-faint sy-num" style={{ fontSize: 12 }}>{drives.length > 0 ? `${freeText(free)} free` : ""}</span>
        <Link className="sy-link" to="/settings/libraries" aria-label="Open storage settings">Storage →</Link>
      </div>
      <div className="sy-drives sy-scroll">
        {drives.length === 0 ? <div className="sy-muted" style={{ fontSize: 12.5 }}>No drive has been read yet, so free space is unknown.</div> : null}
        {drives.map((drive) => {
          const warn = drive.total > 0 && drive.free / drive.total < 0.15;
          const days = fullIn(drive.fullInDays);
          const others = Math.max(0, drive.total - drive.free - drive.mine);
          return (
            <div key={drive.key} className={`sy-drive${warn ? " sy-warn" : ""}`} title={drive.path}>
              <span className="sy-n">{drive.name} {drive.path ? <small>{drive.path}</small> : null}</span>
              <span className="sy-f"><b>{freeText(drive.free)}</b> free{days ? ` · full in ~${days}` : ""}</span>
              <span className="sy-bar" aria-hidden="true">
                <i className="sy-mine" style={{ width: `${drive.total > 0 ? (drive.mine / drive.total) * 100 : 0}%` }} title="Deluno’s own" />
                <i style={{ width: `${drive.total > 0 ? (others / drive.total) * 100 : 0}%` }} />
              </span>
              {drive.io || drive.health !== "unknown" ? (
                <span className="sy-io">
                  <span>{drive.io ? <>read <b>{mbps(drive.io.read)}</b> · write <b>{mbps(drive.io.write)}</b> MB/s · <b>{plainCount(drive.io.iops)}</b> IOPS · {Math.round(drive.io.busy)}% busy</> : null}</span>
                  {drive.health === "good" ? <span className="sy-ok">✓ healthy</span> : drive.health === "unknown" ? null : <span className="sy-warn">⚠ {drive.healthNote || (drive.health === "failing" ? "failing" : "a warning")}</span>}
                </span>
              ) : null}
            </div>
          );
        })}
      </div>
    </section>
  );
}
