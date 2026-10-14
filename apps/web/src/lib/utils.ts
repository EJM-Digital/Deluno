import { type ClassValue, clsx } from "clsx";
import { twMerge } from "tailwind-merge";

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}

export function formatBytesFromGb(value: number | null | undefined) {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return "Unknown";
  }
  return `${value.toFixed(1)} GB`;
}

/** What a size reads as when it is missing, negative or not a number: a plain dash, never "NaN" or "undefined". */
export const NO_SIZE = "—";

/**
 * The one size formatter for every screen (#1310): "840 B", "12.5 MB", "120 GB".
 * The unit is always present and always the largest one that keeps the amount under 1024
 * (so just under a megabyte reads "1.0 MB", not "1024 KB"). A missing, negative or
 * non-finite size reads a dash.
 */
export function formatBytes(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value) || value < 0) return NO_SIZE;
  const units = ["B", "KB", "MB", "GB", "TB"];
  let index = 0;
  let amount = value;
  // Rounding reaches 1024 just under a boundary (1048575 B is 1023.999 KB): step up so it reads 1.0 MB.
  while (index < units.length - 1 && Math.round(amount) >= 1024) {
    index += 1;
    amount = value / 1024 ** index;
  }
  const text = index === 0 || amount >= 100 ? String(Math.round(amount)) : amount.toFixed(1);
  return `${text} ${units[index]}`;
}

export function formatRelative(value: string) {
  return value;
}

/** An IPv4 address written the IPv6 way (::ffff:192.168.1.20) shown as people write it (192.168.1.20) (#1141). */
export function plainAddress(address: string): string {
  const match = /^::ffff:(\d{1,3}(?:\.\d{1,3}){3})$/i.exec(address.trim());
  return match ? match[1] : address;
}

/**
 * The address another device on the network can use to reach Deluno (#1141). The host this
 * browser used is only right when it is not this computer's own loopback name: "localhost"
 * means the other device itself. Then this computer's LAN address is used, then its name.
 * Returns null when Deluno knows no address a different device could use.
 */
export function addressForOtherDevices(
  currentHost: string,
  detectedHosts: { host: string; reason: string }[]
): string | null {
  const isLoopback = (value: string) => {
    const host = value.trim().toLowerCase();
    return host === "" || host === "localhost" || host.endsWith(".localhost") || host === "::1" || host === "[::1]" || /^127(?:\.\d{1,3}){3}$/.test(host);
  };
  const current = plainAddress(currentHost.trim()).replace(/^([^:]+):\d+$/, "$1");
  if (!isLoopback(current)) return current;
  const lan = detectedHosts.find((candidate) => candidate.reason === "A LAN address of this computer" && !isLoopback(candidate.host));
  if (lan) return lan.host;
  const name = detectedHosts.find((candidate) => candidate.reason === "This computer's name" && !isLoopback(candidate.host));
  return name ? name.host : null;
}
