import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { AlertAction, AlertActions } from "../../lib/alert-actions";
import type { BellRow } from "../../lib/bell-rows";

vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("../../components/shell/toaster", () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

const { AlertButtons, NeedsYouRow, RowActions, bellRowElement, runAlert } = await import("./needs-you");
const { useAction } = await import("./downloads-kit");

afterEach(cleanup);

const openFolder: AlertAction = { id: "open", label: "Open folder", hint: "Go to the folder.", to: "/library/folders" };

function Harness({ row }: { row: BellRow }) {
  const state = useAction();
  return <>{bellRowElement(row, state)}</>;
}

function renderRow(row: BellRow) {
  return render(<MemoryRouter><ul><Harness row={row} /></ul></MemoryRouter>);
}

const baseRow = { key: "r1", tone: "warn", title: "Sintel (2010) is stuck", text: "The client stopped answering.", actions: [openFolder] as AlertActions } as BellRow;

describe("the rows of Needs you", () => {
  it("makes the title a link when the row has somewhere to go, and plain text when it does not", () => {
    const { unmount } = renderRow({ ...baseRow, to: "/activity" });
    expect(screen.getByRole("link", { name: "Sintel (2010) is stuck" })).toHaveAttribute("href", "/activity");
    unmount();

    renderRow(baseRow);
    expect(screen.queryByRole("link", { name: "Sintel (2010) is stuck" })).toBeNull();
    expect(screen.getByText("Sintel (2010) is stuck")).toBeVisible();
  });

  it("names a button after the row's subject, or after its title when it has none", () => {
    const { unmount } = renderRow({ ...baseRow, subject: "Sintel" });
    expect(screen.getByRole("link", { name: "Open folder: Sintel" })).toHaveAttribute("href", "/library/folders");
    unmount();

    renderRow(baseRow);
    expect(screen.getByRole("link", { name: "Open folder: Sintel (2010) is stuck" })).toBeVisible();
  });

  it("keeps the second column empty on a row with one button, and fills it on a row with two", () => {
    const { container, unmount } = render(<RowActions primary={<button>One</button>} />);
    expect(container.querySelectorAll("[data-row-actions] > span")[1]).toBeEmptyDOMElement();
    unmount();

    const { container: two } = render(<RowActions primary={<button>One</button>} secondary={<button>Two</button>} />);
    expect(two.querySelectorAll("[data-row-actions] > span")[1]).toHaveTextContent("Two");
  });

  it("draws both buttons of an alert that has two", () => {
    function Two() {
      const state = useAction();
      return <AlertButtons actions={[openFolder, { id: "later", label: "Later", hint: "Come back to this.", to: "/later" }]} subject="Sintel" state={state} />;
    }
    render(<MemoryRouter><Two /></MemoryRouter>);
    expect(screen.getByRole("link", { name: "Open folder: Sintel" })).toBeVisible();
    expect(screen.getByRole("link", { name: "Later: Sintel" })).toBeVisible();
  });

  it("does nothing for a button that only links", () => {
    const state = { run: vi.fn() } as unknown as Parameters<typeof runAlert>[1];
    runAlert(openFolder, state);
    expect(state.run).not.toHaveBeenCalled();
  });

  it("draws a row without its own link from NeedsYouRow directly", () => {
    render(<ul><NeedsYouRow tone="bad" title="Disk is full" text="Nothing more can be saved." actions={<span>none</span>} /></ul>);
    expect(screen.getByText("Disk is full")).toHaveAttribute("title", "Disk is full");
  });
});
