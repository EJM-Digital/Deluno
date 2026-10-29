import type { ReactNode } from "react";
import { Link } from "react-router-dom";
import { indexerPageLink } from "../../lib/transfers-view";

/**
 * A file or release name that links to the indexer it came from (#1426): the Indexers list opens that indexer's own
 * page. Inside a row that opens a drawer, the click goes to the link and not to the row. No indexer name, no link.
 */
export function IndexerLink({ indexerName, children, className }: { indexerName: string | null | undefined; children: ReactNode; className?: string }) {
  const name = indexerName?.trim();
  if (!name) return <>{children}</>;
  return (
    <Link
      to={indexerPageLink(name)}
      title={`Open ${name}`}
      onClick={(event) => event.stopPropagation()}
      className={["hover:text-primary hover:underline focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-ring", className].filter(Boolean).join(" ")}
    >
      {children}
    </Link>
  );
}
