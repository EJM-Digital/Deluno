import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ListCard, ListCell, ListRow, ListTable, type ListColumn } from "./list-card";
import { useListColumns } from "../../lib/use-list-columns";
import { dropOrder } from "../../lib/list-column-drag";

interface Item {
  id: string;
  name: string;
  size: number | null;
  when: string;
}

const items: Item[] = [
  { id: "b", name: "Bravo", size: 30, when: "2026-01-02" },
  { id: "a", name: "alpha", size: 5, when: "2026-03-01" },
  { id: "c", name: "Charlie", size: null, when: "2026-02-01" }
];

const columns: Array<ListColumn<Item>> = [
  { id: "name", label: "Name", sort: (item) => item.name.toLowerCase() },
  { id: "size", label: "Size", sort: (item) => item.size },
  { id: "when", label: "Added", sort: (item) => new Date(item.when) },
  { id: "actions", label: "", sortable: false, width: "40px" }
];

function Demo({ listId = "demo" }: { listId?: string }) {
  const list = useListColumns(listId, columns, items);
  return (
    <ListTable list={list} chevron={false}>
      {list.items.map((item) => (
        <ListRow key={item.id} data-testid={`row-${item.id}`}>
          <ListCell primary={item.name} />
          <ListCell primary={String(item.size)} mobile />
          <ListCell primary={item.when} mobile />
          <ListCell primary="x" mobile />
        </ListRow>
      ))}
    </ListTable>
  );
}

/** Chooses Reset columns from the Columns menu. */
async function resetColumns(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole("button", { name: "Columns" }));
  await user.click(screen.getByRole("menuitem", { name: "Reset columns" }));
}

function InCard({ listId = "demo" }: { listId?: string }) {
  return (
    <ListCard title="Things" actions={<button type="button">Add</button>}>
      <Demo listId={listId} />
    </ListCard>
  );
}

const localStorageEmpty = () => window.localStorage.getItem("deluno:list-columns:demo") === null;
const rowOrder = () => screen.getAllByTestId(/^row-/).map((row) => row.dataset.testid);
const headerOrder = () => screen.getAllByRole("columnheader").map((header) => header.dataset.colId);

/** Headers are 100px wide, laid out left to right in DOM order. */
function mockLayout() {
  vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
    if (this.dataset.colId) {
      const index = Array.from(this.parentElement?.children ?? []).indexOf(this);
      return { left: index * 100, right: index * 100 + 100, width: 100, top: 0, bottom: 36, height: 36, x: index * 100, y: 0, toJSON: () => ({}) } as DOMRect;
    }
    return { left: 0, right: 400, width: 400, top: 0, bottom: 36, height: 36, x: 0, y: 0, toJSON: () => ({}) } as DOMRect;
  });
}

function pointer(type: string, x: number, target: Element | Window = window) {
  act(() => {
    target.dispatchEvent(new MouseEvent(type, { clientX: x, clientY: 10, button: 0, bubbles: true, cancelable: true }));
  });
}

function drag(header: Element, from: number, to: number) {
  pointer("pointerdown", from, header);
  pointer("pointermove", from + 6);
  pointer("pointermove", to);
  pointer("pointerup", to);
}

beforeEach(() => {
  window.localStorage.clear();
  vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
    callback(0);
    return 0;
  });
  vi.stubGlobal("matchMedia", (query: string) => ({ matches: query.includes("reduce"), addEventListener() {}, removeEventListener() {} }));
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("ListTable column sorting (#1119)", () => {
  it("sorts ascending, then reverses, and alternates: there is no third click back to the list's own order, and aria-sort is only on the sorted column", async () => {
    const user = userEvent.setup();
    render(<Demo />);
    const header = screen.getByRole("columnheader", { name: /^Name/ });
    expect(header).not.toHaveAttribute("aria-sort");
    expect(rowOrder()).toEqual(["row-b", "row-a", "row-c"]);

    await user.click(within(header).getByRole("button"));
    expect(header).toHaveAttribute("aria-sort", "ascending");
    expect(screen.getByRole("columnheader", { name: /^Size/ })).not.toHaveAttribute("aria-sort");
    expect(rowOrder()).toEqual(["row-a", "row-b", "row-c"]);

    await user.click(within(header).getByRole("button"));
    expect(header).toHaveAttribute("aria-sort", "descending");
    expect(rowOrder()).toEqual(["row-c", "row-b", "row-a"]);

    await user.click(within(header).getByRole("button"));
    expect(header).toHaveAttribute("aria-sort", "ascending");
    expect(rowOrder()).toEqual(["row-a", "row-b", "row-c"]);

    // Only Reset columns brings the list's own order back.
    await resetColumns(user);
    expect(header).not.toHaveAttribute("aria-sort");
    expect(rowOrder()).toEqual(["row-b", "row-a", "row-c"]);
  });

  it("draws a thin line arrow on the sorted column only", async () => {
    const user = userEvent.setup();
    render(<Demo />);
    const header = screen.getByRole("columnheader", { name: /^Name/ });
    expect(header.querySelector("svg")).toBeNull();
    await user.click(within(header).getByRole("button"));
    expect(header.querySelector("svg path")).toHaveAttribute("d", "M5 8.5v-6M2.5 5 5 2.5 7.5 5");
    await user.click(within(header).getByRole("button"));
    expect(header.querySelector("svg path")).toHaveAttribute("d", "M5 1.5v6M2.5 5 5 7.5 7.5 5");
    expect(screen.getByRole("columnheader", { name: /^Size/ }).querySelector("svg")).toBeNull();
  });

  it("has no grip icon on a movable heading, only a grab cursor", () => {
    render(<Demo />);
    const header = screen.getByRole("columnheader", { name: /^Name/ });
    expect(header.querySelector("svg")).toBeNull();
    expect(header.className).toContain("cursor-grab");
  });

  it("sorts numbers by value and keeps rows with nothing to compare last in both directions", async () => {
    const user = userEvent.setup();
    render(<Demo />);
    const header = screen.getByRole("columnheader", { name: /^Size/ });
    await user.click(within(header).getByRole("button"));
    expect(rowOrder()).toEqual(["row-a", "row-b", "row-c"]);
    await user.click(within(header).getByRole("button"));
    expect(rowOrder()).toEqual(["row-b", "row-a", "row-c"]);
  });

  it("sorts dates by time", async () => {
    const user = userEvent.setup();
    render(<Demo />);
    await user.click(within(screen.getByRole("columnheader", { name: /^Added/ })).getByRole("button"));
    expect(rowOrder()).toEqual(["row-b", "row-c", "row-a"]);
  });

  it("does not offer sorting on a column marked sortable: false", () => {
    render(<Demo />);
    const header = screen.getAllByRole("columnheader")[3];
    expect(header).not.toHaveAttribute("aria-sort");
    expect(within(header).queryByRole("button")).toBeNull();
  });

  it("remembers the sort per list in this browser, and Reset columns clears it", async () => {
    const user = userEvent.setup();
    const { unmount } = render(<Demo />);
    await user.click(within(screen.getByRole("columnheader", { name: /^Name/ })).getByRole("button"));
    unmount();

    render(<Demo />);
    expect(rowOrder()).toEqual(["row-a", "row-b", "row-c"]);
    await resetColumns(user);
    expect(rowOrder()).toEqual(["row-b", "row-a", "row-c"]);
    expect(window.localStorage.getItem("deluno:list-columns:demo")).toBeNull();
  });

  it("always offers a Columns button with one item, Reset columns, and it is not a visible item until opened", async () => {
    const user = userEvent.setup();
    render(<Demo />);
    const button = screen.getByRole("button", { name: "Columns" });
    expect(button).toHaveAttribute("aria-haspopup", "menu");
    expect(screen.queryByRole("menuitem")).toBeNull();
    await user.click(button);
    expect(screen.getAllByRole("menuitem").map((item) => item.textContent)).toEqual(["Reset columns"]);
  });

  it("tells a screen reader how to move and sort a column", () => {
    render(<Demo />);
    expect(screen.getByText(/Drag a heading to move its column, or press Alt with the left or right arrow on it\./)).toHaveTextContent("Click a heading to sort by it, and click again to reverse.");
  });

  // #1162: three tables on one page used to say the same column help three times.
  it("says how to move and sort a column once per page, however many tables it has", () => {
    const { unmount } = render(<><Demo /><Demo /><Demo /></>);
    expect(screen.getAllByText(/Drag a heading to move its column/)).toHaveLength(1);
    unmount();
    expect(screen.queryByText(/Drag a heading to move its column/)).toBeNull();
  });

  it("inside a ListCard the Columns button is the last thing in the card header, with no per-page change", () => {
    render(<InCard />);
    const header = screen.getByRole("banner", { hidden: true }) as HTMLElement;
    const buttons = within(header).getAllByRole("button");
    expect(buttons.map((button) => button.textContent)).toEqual(["Add", "Columns"]);
    expect(screen.getAllByRole("button", { name: "Columns" })).toHaveLength(1);
  });

  it("offers no Columns button on a table with nothing to move or sort", () => {
    render(
      <ListCard title="Plain">
        <ListTable columns={[{ label: "Name" }]} chevron={false}>
          <ListRow>
            <ListCell primary="a" />
          </ListRow>
        </ListTable>
      </ListCard>
    );
    expect(screen.queryByRole("button", { name: "Columns" })).toBeNull();
  });

  it("keeps different lists apart", async () => {
    const user = userEvent.setup();
    const { unmount } = render(<Demo listId="one" />);
    await user.click(within(screen.getByRole("columnheader", { name: /^Name/ })).getByRole("button"));
    unmount();
    render(<Demo listId="two" />);
    expect(rowOrder()).toEqual(["row-b", "row-a", "row-c"]);
  });

  it("falls back to the default when saved state is damaged or storage throws", () => {
    window.localStorage.setItem("deluno:list-columns:demo", "{nope");
    render(<Demo />);
    expect(rowOrder()).toEqual(["row-b", "row-a", "row-c"]);
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    expect(() => render(<Demo listId="blocked" />)).not.toThrow();
  });
});

describe("ListTable startOverWhen (#1418)", () => {
  function Searching({ results }: { results: Item[] }) {
    return (
      <ListTable listId="demo" columns={columns} items={results} startOverWhen={results} chevron={false}>
        {(rows) => rows.map((item) => (
          <ListRow key={item.id} data-testid={`row-${item.id}`}>
            <ListCell primary={item.name} />
            <ListCell primary={String(item.size)} mobile />
            <ListCell primary={item.when} mobile />
            <ListCell primary="x" mobile />
          </ListRow>
        ))}
      </ListTable>
    );
  }

  it("a fresh set of results starts from the list's own order, not the last chosen sort", async () => {
    const user = userEvent.setup();
    const { rerender } = render(<Searching results={items} />);
    const header = screen.getByRole("columnheader", { name: /^Name/ });
    await user.click(within(header).getByRole("button"));
    expect(rowOrder()).toEqual(["row-a", "row-b", "row-c"]);
    // The same results again (a re-render) keep the person's sort.
    rerender(<Searching results={items} />);
    expect(rowOrder()).toEqual(["row-a", "row-b", "row-c"]);
    // A new search brings a new list.
    rerender(<Searching results={[...items]} />);
    expect(header).not.toHaveAttribute("aria-sort");
    expect(rowOrder()).toEqual(["row-b", "row-a", "row-c"]);
    expect(localStorageEmpty()).toBe(true);
  });
});

describe("ListTable column order (#1119)", () => {
  it("drags a header past its neighbour: headers and cells reorder together, once, on drop", () => {
    mockLayout();
    render(<Demo />);
    expect(headerOrder()).toEqual(["name", "size", "when", "actions"]);
    // Name (0-100) dragged to the middle of Size (100-200).
    drag(screen.getByRole("columnheader", { name: /^Name/ }), 50, 160);
    expect(headerOrder()).toEqual(["size", "name", "when", "actions"]);
    const cells = within(screen.getByTestId("row-b")).getAllByRole("cell").map((cell) => cell.textContent);
    expect(cells).toEqual(["30", "Bravo", "2026-01-02", "x"]);
    // A held column sits at rest again once it is dropped.
    expect(screen.getByRole("columnheader", { name: /^Size/ }).style.zIndex).toBe("");
  });

  it("lifts the held column on the raised surface with the popover shadow-sm: no scale and no tint, and the page shows a grabbing cursor", () => {
    mockLayout();
    render(<Demo />);
    const header = screen.getByRole("columnheader", { name: /^Name/ });
    pointer("pointerdown", 50, header);
    pointer("pointermove", 60);
    pointer("pointermove", 90);
    expect(document.documentElement.classList.contains("deluno-column-dragging")).toBe(true);
    for (const held of [header, within(screen.getByTestId("row-b")).getAllByRole("cell")[0]]) {
      expect(held.style.position).toBe("relative");
      expect(held.style.zIndex).toBe("3");
      expect(held.style.backgroundColor).toBe("hsl(var(--surface-2))");
      expect(held.style.boxShadow).toBe("var(--shadow-md)");
      expect(held.style.transform).not.toContain("scale");
    }
    act(() => {
      window.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
    });
    expect(document.documentElement.classList.contains("deluno-column-dragging")).toBe(false);
  });

  it("does not take a click as a drag: a small movement still sorts", async () => {
    mockLayout();
    render(<Demo />);
    const header = screen.getByRole("columnheader", { name: /^Name/ });
    pointer("pointerdown", 50, header);
    pointer("pointermove", 52);
    pointer("pointerup", 52);
    await userEvent.setup().click(within(header).getByRole("button"));
    expect(headerOrder()).toEqual(["name", "size", "when", "actions"]);
    expect(header).toHaveAttribute("aria-sort", "ascending");
  });

  it("keeps a column that was dragged where it was put after a reload", () => {
    mockLayout();
    const { unmount } = render(<Demo />);
    drag(screen.getByRole("columnheader", { name: /^Name/ }), 50, 160);
    unmount();
    render(<Demo />);
    expect(headerOrder()).toEqual(["size", "name", "when", "actions"]);
  });

  it("Escape puts a column back and changes nothing", () => {
    mockLayout();
    render(<Demo />);
    const header = screen.getByRole("columnheader", { name: /^Name/ });
    pointer("pointerdown", 50, header);
    pointer("pointermove", 60);
    pointer("pointermove", 160);
    act(() => {
      window.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
    });
    expect(headerOrder()).toEqual(["name", "size", "when", "actions"]);
    expect(localStorageEmpty()).toBe(true);
  });

  it("moves a column from the keyboard with Alt and the arrow keys, and says where it went", async () => {
    const user = userEvent.setup();
    render(<Demo />);
    const header = screen.getByRole("columnheader", { name: /^Size/ });
    within(header).getByRole("button").focus();
    await user.keyboard("{Alt>}{ArrowLeft}{/Alt}");
    expect(headerOrder()).toEqual(["size", "name", "when", "actions"]);
    expect(screen.getByRole("status")).toHaveTextContent("Size moved to position 1 of 3");
  });

  it("never moves a pinned column and keeps its slot", async () => {
    const user = userEvent.setup();
    render(<Demo />);
    within(screen.getByRole("columnheader", { name: /^Added/ })).getByRole("button").focus();
    await user.keyboard("{Alt>}{ArrowRight}{/Alt}");
    expect(headerOrder()).toEqual(["name", "size", "when", "actions"]);
    expect(screen.getAllByRole("columnheader")[3]).not.toHaveAttribute("tabindex");
  });

  it("Reset columns puts the order and the sort back", async () => {
    mockLayout();
    const user = userEvent.setup();
    render(<Demo />);
    drag(screen.getByRole("columnheader", { name: /^Name/ }), 50, 160);
    await user.click(within(screen.getByRole("columnheader", { name: /^Size/ })).getByRole("button"));
    await resetColumns(user);
    expect(headerOrder()).toEqual(["name", "size", "when", "actions"]);
    expect(rowOrder()).toEqual(["row-b", "row-a", "row-c"]);
  });

  it("does nothing special without a list id", () => {
    render(
      <ListTable columns={[{ label: "Name" }, { label: "Size" }]} chevron={false}>
        <ListRow>
          <ListCell primary="a" />
          <ListCell primary="b" />
        </ListRow>
      </ListTable>
    );
    expect(screen.getAllByRole("columnheader")[0]).not.toHaveAttribute("aria-sort");
    expect(screen.queryByRole("button")).toBeNull();
  });
});

describe("dropOrder", () => {
  const mids = { a: 50, b: 150, c: 250, d: 350 };
  it("puts the dragged column where its centre is", () => {
    expect(dropOrder(["a", "b", "c", "d"], ["a", "b", "c"], ["a", "b", "c", "d"], mids, "a", 260)).toEqual(["b", "c", "a", "d"]);
    expect(dropOrder(["a", "b", "c", "d"], ["a", "b", "c"], ["a", "b", "c", "d"], mids, "c", 10)).toEqual(["c", "a", "b", "d"]);
  });
});

describe("values a row cuts short stay reachable (#1168)", () => {
  it("gives a plain-text cell its full value as a tooltip and lets a second line wrap to two lines", () => {
    const path = "C:/Downloads/Completed/A.Very.Long.Release.Name.2026.1080p.WEB-DL.DDP5.1.H.264-GROUP/file.mkv";
    render(
      <ListTable chevron={false} columns={[{ id: "a", label: "A", sortable: false }]}>
        <ListRow>
          <ListCell primary={path} secondary={`From ${path}`} />
        </ListRow>
      </ListTable>
    );
    expect(screen.getByTitle(path)).toBeInTheDocument();
    const secondary = screen.getByTitle(`From ${path}`);
    expect(secondary.className).toContain("line-clamp-2");
    expect(secondary.className).not.toContain("truncate");
  });
});

describe("a phone row lets its name keep its room (#1168 C3 to C9)", () => {
  const rowOf = (columns: Array<ListColumn<never>>) => {
    render(
      <ListTable columns={columns}>
        <ListRow><ListCell primary="Root folder" /><ListCell primary="D:/A/Very/Long/Path" mobile /></ListRow>
      </ListTable>
    );
    return screen.getAllByRole("row")[1];
  };

  it("flows as a wrapping line, so a long value goes under the name instead of squeezing it", () => {
    const row = rowOf([{ label: "Setting" }, { label: "Value", mobile: true }]);
    expect(row.className).toContain("max-md:flex-wrap");
    expect(row.className).toContain("max-md:[&>:first-child]:basis-36");
  });

  it("keeps the grid when a column asks for a fixed phone track, so its rows line up", () => {
    const row = rowOf([{ label: "Setting" }, { label: "Value", mobile: true, mobileWidth: "6rem" }]);
    expect(row.className).not.toContain("max-md:flex-wrap");
  });
});
