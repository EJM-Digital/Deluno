import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import type { ActivityEventItem } from "../../lib/api";
import { AuditTimeline, AuditTimelineFilters, useAuditTimeline } from "./audit-timeline";

/** #1329: the System timeline's chips, search, paging and rows, as a person uses them. */

function Harness({ events }: { events: ActivityEventItem[] }) {
  const timeline = useAuditTimeline(events, [], 500);
  return (
    <div>
      <AuditTimelineFilters timeline={timeline} />
      <input aria-label="Find an event" value={timeline.query} onChange={(e) => timeline.setQuery(e.target.value)} />
      <output aria-label="count">{timeline.countLabel}</output>
      <AuditTimeline timeline={timeline} />
    </div>
  );
}

const event = (id: string, category: string, message: string, createdUtc = "2026-09-30T01:00:00Z", detailsJson: string | null = null): ActivityEventItem => ({
  id, category, message, severity: "info", detailsJson, relatedJobId: null, relatedEntityType: null, relatedEntityId: null, createdUtc
}) as ActivityEventItem;

const mixed = [
  event("d1", "download.dispatched", "Sent Arrival to the client"),
  event("d2", "download.dispatched", "Sent Sintel to the client"),
  event("i1", "filesystem.import.completed", "Imported Arrival"),
  event("s1", "search.started", "Searched for Dune"),
  event("e1", "job.failed", "Import of Dune failed"),
  event("n1", "notification.sent", "Told you about Dune"),
  event("y1", "backup.finished", "Backup done")
];

const rowsShown = () => within(screen.getByRole("list", { name: "Audit timeline" })).queryAllByRole("listitem");

describe("System timeline chips, search and counts (#1329)", () => {
  it("each kind chip carries its count, picking one narrows the list, and the card says 'N events matching'", () => {
    render(<Harness events={mixed} />);

    expect(screen.getByRole("radio", { name: "All 7" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Download 2" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Import 1" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Search 1" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Error 1" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Notification 1" })).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "System 1" })).toBeInTheDocument();
    expect(screen.getByLabelText("count").textContent).toBe("7 events");

    fireEvent.click(screen.getByRole("radio", { name: "Download 2" }));
    expect(rowsShown()).toHaveLength(2);
    expect(screen.getByLabelText("count").textContent).toBe("2 events matching");
    expect(screen.queryByText("Imported Arrival")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("radio", { name: "Error 1" }));
    expect(screen.getByLabelText("count").textContent).toBe("1 event matching");
  });

  it("the severity picker narrows to the chosen severity", () => {
    render(<Harness events={mixed} />);

    fireEvent.click(screen.getByRole("button", { name: "More filters" }));
    fireEvent.click(screen.getByRole("radio", { name: "Error" }));
    expect(rowsShown().map((row) => row.textContent)).toEqual([expect.stringContaining("Import of Dune failed")]);
    expect(screen.getByLabelText("count").textContent).toBe("1 event matching");
  });

  it("'Find an event' keeps the events whose message or kind words contain the text, and says when none match", () => {
    render(<Harness events={mixed} />);

    fireEvent.change(screen.getByLabelText("Find an event"), { target: { value: "dune" } });
    expect(rowsShown()).toHaveLength(3);
    expect(screen.getByLabelText("count").textContent).toBe("3 events matching");

    fireEvent.change(screen.getByLabelText("Find an event"), { target: { value: "no such event" } });
    expect(rowsShown().map((row) => row.textContent).join("")).toContain("No events match the current filters.");
    expect(screen.getByLabelText("count").textContent).toBe("0 events matching");
  });

  it("'Load N more events' pages 50 at a time from 120 events, and the last page offers the rest", () => {
    const many = Array.from({ length: 120 }, (_, index) => event(`m${index}`, "download.dispatched", `Sent title ${index}`, new Date(Date.UTC(2026, 8, 30, 1, 0, 0) - index * 1000).toISOString()));
    render(<Harness events={many} />);

    expect(rowsShown()).toHaveLength(50);
    fireEvent.click(screen.getByRole("button", { name: "Load 50 more events" }));
    expect(rowsShown()).toHaveLength(100);
    fireEvent.click(screen.getByRole("button", { name: "Load 20 more events" }));
    expect(rowsShown()).toHaveLength(120);
    expect(screen.queryByRole("button", { name: /more events/ })).not.toBeInTheDocument();
  });

  it("a new filter starts the paging again from the first 50", () => {
    const many = Array.from({ length: 120 }, (_, index) => event(`m${index}`, "download.dispatched", `Sent title ${index}`));
    render(<Harness events={many} />);

    fireEvent.click(screen.getByRole("button", { name: "Load 50 more events" }));
    expect(rowsShown()).toHaveLength(100);
    fireEvent.change(screen.getByLabelText("Find an event"), { target: { value: "Sent" } });
    expect(rowsShown()).toHaveLength(50);
  });
});

describe("System timeline rows (#1329)", () => {
  it("a row shows its severity badge, kind words, relative and absolute time, and Show detail / Hide detail opens and closes the detail", () => {
    const longAgo = new Date(Date.now() - 3 * 86_400_000).toISOString();
    const justNow = new Date().toISOString();
    render(<Harness events={[
      event("r1", "import.failed", "Import of Dune failed", justNow, '{"reason":"no video"}'),
      event("r2", "download.dispatched", "Sent Sintel", longAgo)
    ]} />);

    const failed = screen.getByText("Import of Dune failed").closest("li") as HTMLElement;
    expect(within(failed).getByText("error")).toBeInTheDocument();
    expect(within(failed).getByText("just now")).toBeInTheDocument();
    const sent = screen.getByText("Sent Sintel").closest("li") as HTMLElement;
    expect(within(sent).getByText("success")).toBeInTheDocument();
    expect(within(sent).getByText("3d ago")).toBeInTheDocument();
    expect(within(sent).queryByRole("button", { name: /detail/ })).not.toBeInTheDocument();

    expect(within(failed).queryByText('{"reason":"no video"}')).not.toBeInTheDocument();
    fireEvent.click(within(failed).getByRole("button", { name: "Show detail" }));
    expect(within(failed).getByText('{"reason":"no video"}')).toBeInTheDocument();
    fireEvent.click(within(failed).getByRole("button", { name: "Hide detail" }));
    expect(within(failed).queryByText('{"reason":"no video"}')).not.toBeInTheDocument();
  });
});
