/**
 * Pure rules of the System tab (#1098): which Health area a check belongs to, how a reading is worded, how a trace is
 * drawn, how many whole rows the facts box holds. The components only draw what these return.
 */
import type { HealthCheckResultItem } from "../../../lib/api";
import { formatBytes, NO_SIZE } from "../../../lib/utils";

export type HealthArea = "library" | "downloads" | "connections" | "processing" | "storage" | "security" | "machine";

export const AREAS: ReadonlyArray<{ key: HealthArea; name: string }> = [
  { key: "library", name: "Library" },
  { key: "downloads", name: "Downloads" },
  { key: "connections", name: "Connections" },
  { key: "processing", name: "Processing" },
  { key: "storage", name: "Storage" },
  { key: "security", name: "Security" },
  { key: "machine", name: "Machine" }
];

export const areaName = (key: HealthArea) => AREAS.find((area) => area.key === key)?.name ?? "Machine";

const SECURITY = /password|sign-?in|allowed host|security|lan access|certificate|https|api key|authenticat/i;
const STORAGE = /drive|disk|free space|storage|space|full\b/i;
const MACHINE = /sleep|hibernat|reboot|smart|temperature|windows update|restart|service|background|power/i;

/** The Health area of a stored check: by its category, then by what it says for the catch-all "system" ones. */
export function areaOf(check: Pick<HealthCheckResultItem, "id" | "category" | "name">): HealthArea {
  const text = `${check.id} ${check.name}`;
  switch (check.category) {
    case "library":
    case "tags":
    case "notice":
      return "library";
    case "download-client":
    case "grab":
      return "downloads";
    case "indexer":
    case "media-server":
    case "metadata":
    case "subtitle-provider":
    case "seerr":
    case "proxy":
      return "connections";
    case "processor":
    case "remote-path":
      return "processing";
    // The Machine checks (sleep, drive health, a pending restart, processor heat) always belong to Machine.
    case "machine":
      return "machine";
    default:
      if (SECURITY.test(text)) return "security";
      if (STORAGE.test(text) && !MACHINE.test(text)) return "storage";
      return "machine";
  }
}

/** The area of a Needs-you row from the Dashboard's own list (by its id). */
export function areaOfAttention(id: string): HealthArea {
  if (id.startsWith("stalled") || id.includes("download") || id.startsWith("queue")) return "downloads";
  if (id.includes("handoff") || id.includes("processor")) return "processing";
  if (id === "clients" || id === "indexers" || id === "processors") return "connections";
  if (id.includes("leftover") || id.includes("folder") || id.includes("recovery") || id.includes("import") || id.includes("library")) return "library";
  if (id.includes("storage") || id.includes("disk") || id.includes("space")) return "storage";
  if (id.includes("security") || id.includes("password")) return "security";
  return "machine";
}

/* ---------------------------------------------------------------- wording */

const whole = (value: number) => Math.round(value).toLocaleString("en-AU");

/** "387 MB", "1.2 GB", "46 MB". */
export function sizeText(bytes: number | null | undefined): string {
  const text = formatBytes(bytes);
  return text === NO_SIZE ? "" : text;
}

/** A rate in MB/s with one decimal under 10 ("0.0", "3.4", "42"). */
export function mbps(bytesPerSecond: number | null | undefined): string {
  const value = Math.max(0, (bytesPerSecond ?? 0) / 1e6);
  return value < 10 ? value.toFixed(1) : String(Math.round(value));
}

/** "up 6 days", "up 3 h". */
export function upText(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds)) return "";
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `up ${Math.max(1, minutes)} min`;
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return `up ${hours} h`;
  return `up ${Math.floor(hours / 24)} days`;
}

/** "1:04", "2 h 5 min": how long a task has until it runs. */
export function countdown(ms: number): string {
  const s = Math.max(0, Math.round(ms / 1000));
  if (s >= 3600) return s >= 86_400 ? `${Math.round(s / 86_400)} days` : `${Math.floor(s / 3600)} h ${Math.floor((s % 3600) / 60)} min`;
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}

/** "5 min", "3 h", "2 days": a span with no "ago". */
export function span(ms: number): string {
  const minutes = Math.max(1, Math.round(ms / 60_000));
  if (minutes < 60) return `${minutes} min`;
  if (minutes < 1440) return `${Math.round(minutes / 60)} h`;
  const days = Math.round(minutes / 1440);
  return `${days} ${days === 1 ? "day" : "days"}`;
}

/** The host and port of an address ("http://192.168.1.10:5099/" gives 192.168.1.10 and 5099). */
export function splitAddress(address: string | null | undefined): { host: string; port: string | null } {
  const raw = (address ?? "").trim().replace(/^[a-z]+:\/\//i, "").replace(/\/.*$/, "");
  const match = /^(.*):(\d+)$/.exec(raw);
  return match ? { host: match[1], port: match[2] } : { host: raw, port: null };
}

/** When something started, said as people do: "9:14 am" today, "3 Oct" otherwise. */
export function sinceText(iso: string | null | undefined, now = Date.now()): string {
  const at = iso ? new Date(iso) : null;
  if (!at || Number.isNaN(at.getTime())) return "";
  const today = new Date(now).toDateString() === at.toDateString();
  return today ? at.toLocaleTimeString("en-AU", { hour: "numeric", minute: "2-digit" }).replace(/\s/g, " ") : at.toLocaleDateString("en-AU", { day: "numeric", month: "short" });
}

/** "full in ~3 weeks" style, from days. */
export function fullIn(days: number | null | undefined): string | null {
  if (days === null || days === undefined || !Number.isFinite(days) || days <= 0) return null;
  const rounded = Math.round(days);
  return rounded > 90 ? `${Math.round(rounded / 30)} months` : `${rounded} days`;
}

/* ---------------------------------------------------------------- traces */

/** The SVG path of a trace in a 200 x 50 box; null with fewer than two readings. */
export function tracePaths(values: readonly number[], max: number): { line: string; area: string } | null {
  const points = values.filter((value) => Number.isFinite(value));
  if (points.length < 2 || !(max > 0)) return null;
  const pts = points.map((value, index) => `${((index / (points.length - 1)) * 200).toFixed(1)},${(50 - Math.min(1, Math.max(0, value / max)) * 46).toFixed(1)}`);
  return { line: `M${pts.join(" L")}`, area: `M0,50 L${pts.join(" L")} L200,50 Z` };
}

/** The top of a trace's scale: fixed for a percentage, else the highest reading with some headroom (never zero). */
export function traceMax(values: readonly number[], fixed?: number, headroom = 1.25): number {
  if (fixed) return fixed;
  const top = Math.max(0, ...values.filter((value) => Number.isFinite(value)));
  return top > 0 ? top * headroom : 1;
}

/* --------------------------------------------------------------- facts box */

/** How many whole rows of `rowPx` (with `gapPx` between) fit `room` px: at least one. */
export function wholeRows(room: number, rowPx: number, gapPx: number): number {
  return Math.max(1, Math.floor((room + gapPx) / (rowPx + gapPx)));
}

export const plainCount = whole;

/** "3:00 am" from a Date. */
export const clockText = (at: Date) => at.toLocaleTimeString("en-AU", { hour: "numeric", minute: "2-digit" }).replace(/\s/g, " ");

/** "03:00" (a time-of-day setting) as "3:00 am". */
export function timeOfDayText(value: string | null | undefined): string {
  const match = /^(\d{1,2}):(\d{2})/.exec(value ?? "");
  if (!match) return value ?? "";
  return clockText(new Date(2000, 0, 1, Number(match[1]), Number(match[2])));
}

/** "Today 3:00 am", "Yesterday 3:00 am", "Tue 3:00 am", "5 Oct 3:00 am". */
export function whenText(iso: string, now = Date.now()): string {
  const at = new Date(iso);
  if (Number.isNaN(at.getTime())) return "";
  const day = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  const apart = Math.round((day(new Date(now)) - day(at)) / 86_400_000);
  const clock = clockText(at);
  if (apart <= 0) return `Today ${clock}`;
  if (apart === 1) return `Yesterday ${clock}`;
  if (apart < 7) return `${at.toLocaleDateString("en-AU", { weekday: "short" })} ${clock}`;
  return `${at.toLocaleDateString("en-AU", { day: "numeric", month: "short" })} ${clock}`;
}
