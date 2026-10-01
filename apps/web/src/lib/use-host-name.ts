import { useQuery } from "@tanstack/react-query";
import { fetchJson } from "./api";
import { useAuth } from "./use-auth";

/** What /api/system/info says about the machine Deluno runs on (#1101). */
export interface HostNameInfo {
  hostName?: string;
  hostLooksLikeContainerId?: boolean;
}

/** The same key and fetch System's "This installation" card reads, so the two share one cache entry. */
export const SYSTEM_INFO_KEY = ["src", "system-info"] as const;

/** The browser tab's title: "Deluno · WORKSTATION" once the name is known, plain "Deluno" before. */
export function hostTitle(hostName: string | null | undefined): string {
  const name = hostName?.trim();
  return name ? `Deluno · ${name}` : "Deluno";
}

/** The name of the computer this Deluno runs on, or null until it is known (or when signed out). */
export function useHostName(): string | null {
  const { token } = useAuth();
  const { data } = useQuery({
    queryKey: SYSTEM_INFO_KEY,
    queryFn: () => fetchJson<HostNameInfo>("/api/system/info").catch(() => null),
    enabled: Boolean(token),
    staleTime: 5 * 60_000
  });
  const name = data?.hostName?.trim();
  return token && name ? name : null;
}
