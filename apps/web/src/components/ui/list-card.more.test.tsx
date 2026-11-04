import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ListCard, ListCell, ListEmpty, ListNameCell, ListRow, ListTable, type ListColumn } from "./list-card";
import { COLUMN_MOVE_MS } from "../../lib/list-column-drag";
import { useListColumns } from "../../lib/use-list-columns";

// #1329: the rest of the collection card: its header (count, info, filter, columns), keyboard moves that slide columns, rows that open, and the cells.

interface Item { id: string; name: string; size: number }
const items: Item[] = [{ id: "a", name: "Alpha", size: 1 }, { id: "b", name: "Bravo", size: 2 }];
const columns: Array<ListColumn<Item>> = [
  { id: "name", label: "Name", sort: (item) => item.name },
  { id: "size", label: "Size", sort: (item) => item.size },
  { id: "kind", label: "Kind" },
  { id: "end", label: "Total", align: "end", movable: false },
  { id: "hidden", label: "Hidden", srOnly: true }
];

function Demo({ listId = "more", children }: { listId?: string; children?: React.ReactNode }) {
  const list = useListColumns(listId, columns, items);
  return (
    <ListTable list={list} chevron={false}>
      {list.items.map((item) => (
        <ListRow key={item.id} data-testid={`row-${item.id}`}>
          <ListCell primary={item.name} />
          <ListCell primary={String(item.size)} />
          <ListCell primary="thing" />
          <ListCell primary="9" />
          <ListCell primary="" />
        </ListRow>
      ))}
      {children}
    </ListTable>
  );
}

const headerOrder = () => screen.getAllByRole("columnheader").map((header) => header.dataset.colId);

beforeEach(() => {
  window.localStorage.clear();
  vi.stubGlobal("matchMedia", (query: string) => ({ matches: false, media: query, addEventListener() {}, removeEventListener() {} }));
});
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("a collection card's header", () => {
  it("shows a title, a count and a filter with its own placeholder, and passes what is typed on", () => {
    const onChange = vi.fn();
    render(<ListCard title="Indexers" count="4 of 5" filter={{ value: "", onChange, placeholder: "Find an indexer" }}><p>body</p></ListCard>);
    expect(screen.getByRole("heading", { name: "Indexers" })).toBeTruthy();
    expect(screen.getByText("4 of 5")).toBeTruthy();
    fireEvent.change(screen.getByRole("searchbox"), { target: { value: "geek" } });
    expect(onChange).toHaveBeenCalledWith("geek");
    expect(screen.getByRole("searchbox").getAttribute("placeholder")).toBe("Find an indexer");
  });

  it("calls the filter Filter when it has no placeholder, and shows neither count nor filter when not given", () => {
    const { unmount } = render(<ListCard title="Indexers" filter={{ value: "x", onChange: vi.fn() }}><p>body</p></ListCard>);
    expect(screen.getByRole("searchbox").getAttribute("placeholder")).toBe("Filter");
    unmount();
    render(<ListCard title="Indexers"><p>body</p></ListCard>);
    expect(screen.queryByRole("searchbox")).toBeNull();
  });

  it("keeps a title that is not text as it is, and a text title that must keep its casing", () => {
    const { unmount } = render(<ListCard title={<em>Four things</em>} info="More words."><p>body</p></ListCard>);
    expect(screen.getByRole("button", { name: "More about this section" })).toBeTruthy();
    unmount();
    render(<ListCard title="4 indexers · 3 of 4 healthy" titleCase={false}><p>body</p></ListCard>);
    expect(screen.getByRole("heading").textContent).toBe("4 indexers · 3 of 4 healthy");
  });

  it("shows its longer description only after the (i) is pressed, and hides it again", async () => {
    const user = userEvent.setup();
    render(<ListCard title="Indexers" info="Why these matter."><p>body</p></ListCard>);
    expect(screen.queryByRole("note")).toBeNull();
    await user.click(screen.getByRole("button", { name: "More about Indexers" }));
    expect(screen.getByRole("note").textContent).toBe("Why these matter.");
    await user.click(screen.getByRole("button", { name: "More about Indexers" }));
    expect(screen.queryByRole("note")).toBeNull();
  });

  it("draws no description for a card that was given none", () => {
    render(<ListCard title="Indexers"><p>body</p></ListCard>);
    expect(screen.queryByRole("button", { name: /more about/i })).toBeNull();
  });
});

describe("the Columns button in a card", () => {
  it("goes away with the table that registered it, and Reset columns resets every table in the card", async () => {
    const user = userEvent.setup();
    const { rerender } = render(<ListCard title="Things"><Demo /></ListCard>);
    await user.click(within(screen.getByRole("columnheader", { name: /^Size/ })).getByRole("button"));
    expect(screen.getAllByRole("columnheader")[1].getAttribute("aria-sort")).toBe("ascending");

    await user.click(screen.getByRole("button", { name: "Columns" }));
    await user.click(screen.getByRole("menuitem", { name: "Reset columns" }));
    expect(screen.getAllByRole("columnheader")[1].getAttribute("aria-sort")).toBeNull();

    rerender(<ListCard title="Things"><p>nothing to move</p></ListCard>);
    expect(screen.queryByRole("button", { name: "Columns" })).toBeNull();
  });
});

describe("moving a column from the keyboard slides the columns into place", () => {
  /** Each header is 100px wide, laid out left to right in the order the page has them. */
  function layout() {
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockImplementation(function (this: HTMLElement) {
      const index = this.dataset.colId ? Array.from(this.parentElement?.children ?? []).indexOf(this) : 0;
      return { left: index * 100, right: index * 100 + 100, width: 100, top: 0, bottom: 36, height: 36, x: index * 100, y: 0, toJSON: () => ({}) } as DOMRect;
    });
  }
  const moveSizeLeft = () => fireEvent.keyDown(screen.getByRole("columnheader", { name: /^Size/ }), { key: "ArrowLeft", altKey: true });

  it("starts the swapped headers and their cells at the old spot, then lets them go and clears the transition", () => {
    layout();
    vi.useFakeTimers();
    render(<Demo>{<div role="row"><span>a row with no cells</span></div>}</Demo>);
    // a row that shows fewer cells than there are columns keeps its own order
    render(<ListTable columns={[{ label: "One" }, { label: "Two" }]} chevron={false}><ListRow><ListCell primary="only" /></ListRow></ListTable>);

    moveSizeLeft();

    expect(headerOrder().slice(0, 2)).toEqual(["size", "name"]);
    const size = screen.getAllByRole("columnheader").find((header) => header.dataset.colId === "size")!;
    expect(size.style.transition).toContain("transform");
    expect(size.style.transform).toBe("");
    const bravoRow = screen.getByTestId("row-b");
    expect(bravoRow.children[0].textContent).toBe("2");
    expect((bravoRow.children[0] as HTMLElement).style.transition).toContain("transform");
    act(() => { vi.advanceTimersByTime(COLUMN_MOVE_MS + 29); });
    expect(size.style.transition).toContain("transform");
    act(() => { vi.advanceTimersByTime(1); });
    expect(size.style.transition).toBe("");
  });

  it("slides nothing when the headers did not actually move, and nothing when motion is reduced", () => {
    vi.spyOn(HTMLElement.prototype, "getBoundingClientRect").mockReturnValue({ left: 0, right: 100, width: 100, top: 0, bottom: 36, height: 36, x: 0, y: 0, toJSON: () => ({}) } as DOMRect);
    const { unmount } = render(<Demo />);
    moveSizeLeft();
    expect(screen.getAllByRole("columnheader")[0].style.transition).toBe("");
    unmount();

    vi.restoreAllMocks();
    layout();
    vi.stubGlobal("matchMedia", (query: string) => ({ matches: query.includes("reduce"), media: query, addEventListener() {}, removeEventListener() {} }));
    render(<Demo listId="calm" />);
    moveSizeLeft();
    expect(screen.getAllByRole("columnheader")[0].style.transition).toBe("");
  });

  it("does nothing at the edge, for another key, or without Alt, and names a column whose heading is not text", () => {
    render(<Demo />);
    const name = screen.getByRole("columnheader", { name: /^Name/ });
    fireEvent.keyDown(name, { key: "ArrowLeft", altKey: true });
    fireEvent.keyDown(name, { key: "ArrowRight" });
    fireEvent.keyDown(name, { key: "a", altKey: true });
    expect(headerOrder().slice(0, 3)).toEqual(["name", "size", "kind"]);
    expect(screen.queryByRole("status")?.textContent ?? "").toBe("");
  });

  it("says Column when the moved heading is not plain text", () => {
    const nodes: Array<ListColumn<Item>> = [{ id: "one", label: "One" }, { id: "two", label: <b>Two</b> }];
    function Nodes() {
      const list = useListColumns("nodes", nodes, items);
      return <ListTable list={list} chevron={false}><ListRow><ListCell primary="1" /><ListCell primary="2" /></ListRow></ListTable>;
    }
    render(<Nodes />);
    fireEvent.keyDown(screen.getAllByRole("columnheader")[1], { key: "ArrowLeft", altKey: true });
    expect(screen.getByRole("status").textContent).toBe("Column moved to position 1 of 2");
  });
});

describe("a row that opens something", () => {
  function Rows({ onOpen, selected = false }: { onOpen?: () => void; selected?: boolean }) {
    return (
      <ListTable columns={[{ label: "Name" }]}>
        <ListRow onClick={onOpen} selected={selected} data-testid="row"><ListCell primary="Alpha" /></ListRow>
      </ListTable>
    );
  }

  it("opens on a click, on Enter and on Space, and not on another key", () => {
    const open = vi.fn();
    render(<Rows onOpen={open} />);
    const row = screen.getByTestId("row");
    fireEvent.click(row);
    fireEvent.keyDown(row, { key: "Enter" });
    fireEvent.keyDown(row, { key: " " });
    expect(open).toHaveBeenCalledTimes(3);
    fireEvent.keyDown(row, { key: "a" });
    expect(open).toHaveBeenCalledTimes(3);
  });

  it("does not open when Enter was pressed on something inside the row", () => {
    const open = vi.fn();
    render(
      <ListTable columns={[{ label: "Name" }]}>
        <ListRow onClick={open} data-testid="row"><ListCell><button type="button">Inner</button></ListCell></ListRow>
      </ListTable>
    );
    fireEvent.keyDown(screen.getByRole("button", { name: "Inner" }), { key: "Enter" });
    expect(open).not.toHaveBeenCalled();
  });

  it("is a plain row without a click handler: not focusable, nothing on Enter, no chevron", () => {
    render(<Rows />);
    const row = screen.getByTestId("row");
    expect(row.getAttribute("tabindex")).toBeNull();
    expect(row.getAttribute("aria-selected")).toBeNull();
    fireEvent.keyDown(row, { key: "Enter" });
    expect(row.querySelector("svg")).toBeNull();
  });

  it("shows a chevron and its selected state when it opens something", () => {
    render(<Rows onOpen={() => undefined} selected />);
    const row = screen.getByTestId("row");
    expect(row.getAttribute("tabindex")).toBe("0");
    expect(row.getAttribute("aria-selected")).toBe("true");
    expect(row.className).toContain("bg-primary/8");
    expect(row.querySelector("svg")).not.toBeNull();
  });

  it("must be inside a table", () => {
    vi.spyOn(console, "error").mockImplementation(() => undefined);
    expect(() => render(<ListRow>x</ListRow>)).toThrow("ListRow must be rendered inside ListTable");
  });
});

describe("the cells", () => {
  it("sets a number to the right with tabular figures, a path in the code face, and both lines of a cell", () => {
    render(
      <ListTable columns={[{ label: "A" }, { label: "B" }]} chevron={false}>
        <ListRow>
          <ListCell primary="C:\Films" secondary="the second line" mono align="end" numeric />
          <ListCell>custom</ListCell>
        </ListRow>
      </ListTable>
    );
    const cells = screen.getAllByRole("cell");
    expect(cells[0].className).toContain("text-right");
    expect(cells[0].className).toContain("tabular-nums");
    expect(screen.getByText("C:\\Films").className).toContain("font-mono");
    expect(screen.getByText("the second line").getAttribute("title")).toBe("the second line");
    expect(cells[1].textContent).toBe("custom");
  });

  it("shows only the line it was given", () => {
    render(
      <ListTable columns={[{ label: "A" }]} chevron={false}>
        <ListRow><ListCell secondary="only below" /></ListRow>
        <ListRow><ListCell primary="only above" mobile /></ListRow>
      </ListTable>
    );
    expect(screen.getByText("only below")).toBeTruthy();
    expect(screen.getByText("only above").className).not.toContain("font-mono");
  });

  it("names a row with a title and a subline, a subline-less name, and a name with its own full text", () => {
    render(
      <>
        <ListNameCell name="Alpha" sub="the first" />
        <ListNameCell name="Bravo" />
        <ListNameCell name={<span>Charlie 2</span>} nameTitle="Charlie 2, full" className="extra" />
      </>
    );
    expect(screen.getByText("Alpha").getAttribute("title")).toBe("Alpha");
    expect(screen.getByText("the first")).toBeTruthy();
    expect(screen.getByText("Bravo").parentElement!.children).toHaveLength(1);
    expect(screen.getByText("Charlie 2").parentElement!.getAttribute("title")).toBe("Charlie 2, full");
    expect(screen.getByText("Charlie 2").parentElement!.parentElement!.className).toContain("extra");
  });

  it("keeps cells in the order the person chose, even when the page wraps some in a fragment", async () => {
    function Wrapped() {
      const list = useListColumns("wrapped", [{ id: "a", label: "A" }, { id: "b", label: "B" }, { id: "c", label: "C" }], [1]);
      return (
        <ListTable list={list} chevron={false}>
          <ListRow data-testid="row">
            <ListCell primary="cell-a" />
            <><ListCell primary="cell-b" /><ListCell primary="cell-c" /></>
          </ListRow>
        </ListTable>
      );
    }
    render(<Wrapped />);
    fireEvent.keyDown(screen.getByRole("columnheader", { name: "A" }), { key: "ArrowRight", altKey: true });
    expect(screen.getAllByRole("columnheader").map((header) => header.dataset.colId)).toEqual(["b", "a", "c"]);
    expect(within(screen.getByTestId("row")).getAllByRole("cell").map((cell) => cell.textContent)).toEqual(["cell-b", "cell-a", "cell-c"]);
    const cells = within(screen.getByTestId("row")).getAllByRole("cell");
    expect(cells[0].className).toContain("hidden");
    expect(cells[1].className).not.toContain("hidden");
  });
});

describe("a table whose heading is not text, or whose row has fewer cells than columns", () => {
  it("sorts by a heading made of markup, which has no tooltip of its own", () => {
    function Markup() {
      const list = useListColumns<Item>("markup", [{ id: "n", label: <b>Name</b>, sort: (item) => item.name }, { id: "s", label: "Size", sort: (item) => item.size }], items);
      return <ListTable list={list} chevron={false}>{list.items.map((item) => <ListRow key={item.id}><ListCell primary={item.name} /><ListCell primary={String(item.size)} /></ListRow>)}</ListTable>;
    }
    render(<Markup />);
    const button = within(screen.getAllByRole("columnheader")[0]).getByRole("button");
    expect(button.querySelector("span")!.getAttribute("title")).toBeNull();
    fireEvent.click(button);
    expect(screen.getAllByRole("columnheader")[0].getAttribute("aria-sort")).toBe("ascending");
  });

  it("leaves a short row as the page wrote it once the columns have been moved", () => {
    function Short() {
      const list = useListColumns("short", [{ id: "a", label: "A" }, { id: "b", label: "B" }], [1]);
      return <ListTable list={list} chevron={false}><ListRow data-testid="row"><ListCell primary="only-a" /></ListRow></ListTable>;
    }
    render(<Short />);
    fireEvent.keyDown(screen.getByRole("columnheader", { name: "A" }), { key: "ArrowRight", altKey: true });
    expect(within(screen.getByTestId("row")).getAllByRole("cell").map((cell) => cell.textContent)).toEqual(["only-a"]);
  });
});

describe("an empty list", () => {
  it("inside a table is one row with one cell, with its description and actions", () => {
    render(
      <ListTable columns={[{ label: "Name" }]}>
        <ListEmpty title="Nothing here" description="Add one to start." actions={<button type="button">Add</button>} />
      </ListTable>
    );
    expect(screen.getByText("Nothing here").parentElement!.getAttribute("role")).toBe("cell");
    expect(screen.getByText("Add one to start.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Add" })).toBeTruthy();
  });

  it("outside a table is a plain block, with only its title when that is all it has", () => {
    render(<ListEmpty title="Nothing here" />);
    expect(screen.getByText("Nothing here").parentElement!.getAttribute("role")).toBeNull();
    expect(screen.queryByRole("button")).toBeNull();
  });
});
