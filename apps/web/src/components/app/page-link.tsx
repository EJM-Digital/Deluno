import { Link } from "react-router-dom";

/**
 * A sentence that names another page names it as a link (#1138): the words are
 * the page's current name, the target is its route, and both live here so a
 * rename changes one place.
 */
export const PAGE_ROUTES = {
  transfers: { to: "/queue", label: "Transfers" },
  failedDownloads: { to: "/search-cycles/failed-downloads", label: "Automation & Recovery, Failed Downloads" },
  failedDownloadsShort: { to: "/search-cycles/failed-downloads", label: "Failed Downloads" },
  downloadHealth: { to: "/search-cycles/download-health", label: "Automation & Recovery, Download Health" },
  keepSeeding: { to: "/search-cycles/keep-seeding", label: "Automation & Recovery, Keep Seeding" }
} as const;

export function PageLink({ page, label }: { page: keyof typeof PAGE_ROUTES; label?: string }) {
  const route = PAGE_ROUTES[page];
  return (
    <Link to={route.to} className="text-primary underline underline-offset-2 hover:no-underline">
      {label ?? route.label}
    </Link>
  );
}
