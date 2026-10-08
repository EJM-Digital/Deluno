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

/** Whole bytes in the largest unit that still reads as a number: "12.0 GB", "840 B". */
export function formatBytes(value: number) {
  if (!value) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  const index = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1);
  return `${(value / 1024 ** index).toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
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
