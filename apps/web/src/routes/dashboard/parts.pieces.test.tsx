import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Bar, FitList, Group, Line, Panel, Plain } from "./parts";

// #1329: the Dashboard's shared pieces: the panel and its link, a line with its tones and link, the plain sentence, the group caption, the bar, and a list that is measured again when its panel changes size or its rows sit in columns.

afterEach(cleanup);
const inRouter = (node: React.ReactNode) => render(<MemoryRouter>{node}</MemoryRouter>);

describe("Panel", () => {
  it("shows its title, count and body, and the link in its header names where it goes", () => {
    inRouter(<Panel title="Queue" count="3" to="/queue" toLabel="Open the queue" bodyClassName="custom-body" className="custom-panel"><p>inside</p></Panel>);
    expect(screen.getByText("Queue")).toBeTruthy();
    expect(screen.getByText("3")).toBeTruthy();
    const link = screen.getByRole("link", { name: "Open the queue: Queue" });
    expect(link.getAttribute("href")).toBe("/queue");
    expect(link.textContent).toBe("Open the queue");
    expect(screen.getByText("inside").parentElement!.className).toContain("custom-body");
    expect(screen.getByText("inside").parentElement!.className).toContain("overflow-hidden");
  });

  it("says Open when the link has no label of its own, and has no link at all without a place to go", () => {
    inRouter(<Panel title="Queue" to="/queue"><p>inside</p></Panel>);
    expect(screen.getByRole("link", { name: "Open: Queue" }).textContent).toBe("Open");
    cleanup();
    inRouter(<Panel title="Queue"><p>inside</p></Panel>);
    expect(screen.queryByRole("link")).toBeNull();
  });
});

describe("Line", () => {
  it("is a plain row with a label and a value, and a hint under the label when it has one", () => {
    const { container } = inRouter(<Line label="Movies" value="12" hint="Last added today" />);
    expect(container.querySelector("a")).toBeNull();
    expect(screen.getByText("Movies")).toBeTruthy();
    expect(screen.getByText("12").className).toContain("text-foreground");
    expect(screen.getByText("Last added today")).toBeTruthy();
  });

  it("is a link when it has somewhere to go, and passes the measuring mark on", () => {
    inRouter(<Line label="Movies" value="12" to="/movies" data-fit-row="" />);
    const link = screen.getByRole("link");
    expect(link.getAttribute("href")).toBe("/movies");
    expect(link.hasAttribute("data-fit-row")).toBe(true);
  });

  it("colours the value by its tone, and a zero is still a value", () => {
    inRouter(<><Line label="A" value="1" tone="warn" /><Line label="B" value="2" tone="bad" /><Line label="C" value="3" tone="ok" /><Line label="D" value={0} /></>);
    expect(screen.getByText("1").className).toContain("text-warning");
    expect(screen.getByText("2").className).toContain("text-destructive");
    expect(screen.getByText("3").className).toContain("text-success");
    expect(screen.getByText("0").className).toContain("text-foreground");
  });

  it("shows no value and no hint when it has neither", () => {
    const { container } = inRouter(<Line label="Only a label" />);
    expect(container.querySelectorAll("span.tabular-nums")).toHaveLength(0);
    expect(container.querySelectorAll("span.text-muted-foreground")).toHaveLength(0);
  });
});

describe("Plain, Group and Bar", () => {
  it("Plain says its sentence and Group captions what follows, passing the measuring mark on", () => {
    inRouter(<><Plain>Nothing to show.</Plain><Group data-fit-row="">Recent</Group></>);
    expect(screen.getByText("Nothing to show.").tagName).toBe("P");
    expect(screen.getByText("Recent").hasAttribute("data-fit-row")).toBe(true);
  });

  it("Bar fills the share given, in the colour of its tone, and never past the ends", () => {
    const fill = (fraction: number, tone?: "primary" | "success" | "warning" | "danger") => {
      const { container, unmount } = render(<Bar fraction={fraction} tone={tone} />);
      const inner = container.querySelector("span > span") as HTMLElement;
      const result = { width: inner.style.width, className: inner.className };
      unmount();
      return result;
    };
    expect(fill(0.25)).toMatchObject({ width: "25%", className: expect.stringContaining("bg-primary") });
    expect(fill(0.5, "success").className).toContain("bg-success");
    expect(fill(0.5, "warning").className).toContain("bg-warning");
    expect(fill(0.5, "danger").className).toContain("bg-destructive");
    expect(fill(2).width).toBe("100%");
    expect(fill(-1).width).toBe("0%");
  });
});

describe("FitList measuring", () => {
  const ROW = 40;
  let rect: typeof HTMLElement.prototype.getBoundingClientRect;
  let boxHeight = 200;
  let across = 1;
  beforeEach(() => {
    rect = HTMLElement.prototype.getBoundingClientRect;
    HTMLElement.prototype.getBoundingClientRect = function (this: HTMLElement) {
      const make = (top: number, left: number, height: number) => ({ top, bottom: top + height, height, left, right: left, width: 0, x: left, y: top, toJSON: () => ({}) }) as DOMRect;
      if (this.hasAttribute("data-fit-row")) {
        const index = Array.from(document.querySelectorAll("[data-fit-row]")).indexOf(this);
        return make(Math.floor(index / across) * ROW, index % across, ROW);
      }
      if (this.className.includes("overflow-hidden")) return make(0, 0, boxHeight);
      return make(0, 0, 0);
    };
  });
  afterEach(() => {
    HTMLElement.prototype.getBoundingClientRect = rect;
    vi.unstubAllGlobals();
    boxHeight = 200;
    across = 1;
  });

  const rows = (count: number) => Array.from({ length: count }, (_, index) => <li key={index}>Row {index + 1}</li>);
  const shown = () => screen.queryAllByText(/^Row /).length;

  it("shows every row, and no link, when the rows it was given cannot all be measured", async () => {
    boxHeight = 80;
    const Opaque = () => <li>Row opaque</li>;
    const unmarked = [<Opaque key="a" />, <Opaque key="b" />, <Opaque key="c" />];
    inRouter(<FitList rows={unmarked} moreTo="/queue" />);
    await waitFor(() => expect(screen.getAllByText("Row opaque")).toHaveLength(3));
    expect(screen.queryByRole("link")).toBeNull();
  });

  it("with its rows in columns, shows a whole number of rows of the grid, so no row ends with a hole", async () => {
    across = 3;
    boxHeight = 120;
    inRouter(<FitList rows={rows(12)} moreTo="/queue" columns />);
    // 120 - 36 = 84: rows end at 40, 40, 40, 80, 80, 80, 120 ... so 6 fit, which is two whole rows of three.
    await waitFor(() => expect(shown()).toBe(6));
    expect(screen.getByRole("link", { name: /and 6 more/ })).toBeTruthy();
  });

  it("with its rows in columns, drops the incomplete last row of the grid", async () => {
    across = 3;
    boxHeight = 156;
    inRouter(<FitList rows={rows(12)} moreTo="/queue" columns />);
    // 156 - 36 = 120: ten rows fit by height only if they end by 120; the grid of three keeps nine.
    await waitFor(() => expect(shown()).toBe(9));
  });

  it("with columns but a single row across, keeps the count it measured", async () => {
    across = 1;
    boxHeight = 200;
    inRouter(<FitList rows={rows(10)} moreTo="/queue" columns />);
    await waitFor(() => expect(shown()).toBe(4));
  });

  it("with columns and everything fitting, shows everything", async () => {
    across = 3;
    boxHeight = 400;
    inRouter(<FitList rows={rows(7)} moreTo="/queue" columns />);
    await waitFor(() => expect(shown()).toBe(7));
    expect(screen.queryByRole("link")).toBeNull();
  });

  it("does not end on a caption, and says the rest are to be found where the link goes", async () => {
    boxHeight = 156;
    inRouter(<FitList rows={rows(10)} moreTo="/queue" isHeading={(index) => index === 2} />);
    // 156 - 36 = 120 holds three rows, but the third is a caption, so two are shown.
    await waitFor(() => expect(shown()).toBe(2));
  });

  it("shares the room left over between the rows when it is asked to fill its panel", async () => {
    boxHeight = 400;
    const { container } = inRouter(<FitList rows={rows(3)} moreTo="/queue" fill />);
    const inner = () => container.querySelector("ul")!.parentElement!;
    await waitFor(() => expect(inner().className).toContain("[&>ul]:flex-1"));
    expect(inner().className).not.toContain("invisible");
    cleanup();
    const plain = inRouter(<FitList rows={rows(3)} moreTo="/queue" />);
    await waitFor(() => expect(shown()).toBe(3));
    expect(plain.container.querySelector("ul")!.parentElement!.className).not.toContain("[&>ul]");
    expect(plain.container.querySelector("ul")!.parentElement!.className).not.toContain("invisible");
  });

  it("with no rows at all it shows an empty box, and no link", () => {
    const { container } = inRouter(<FitList rows={[]} moreTo="/queue" wrap={() => <ol data-testid="wrapped" />} />);
    expect(screen.queryByTestId("wrapped")).toBeNull();
    expect(container.querySelector("a")).toBeNull();
  });

  it("wraps the shown rows the way it is told to", async () => {
    boxHeight = 400;
    inRouter(<FitList rows={rows(2)} moreTo="/queue" wrap={(rowsShown) => <ol data-testid="wrapped">{rowsShown}</ol>} />);
    await waitFor(() => expect(screen.getByTestId("wrapped").children).toHaveLength(2));
  });

  describe("when its panel changes size", () => {
    type Callback = () => void;
    let callbacks: Callback[] = [];
    let disconnected = 0;
    beforeEach(() => {
      callbacks = [];
      disconnected = 0;
      vi.stubGlobal("ResizeObserver", class {
        constructor(callback: Callback) { callbacks.push(callback); }
        observe() {}
        disconnect() { disconnected += 1; }
      });
    });

    it("measures again when the height really changed, and ignores a change of under a pixel", async () => {
      boxHeight = 200;
      inRouter(<FitList rows={rows(30)} moreTo="/queue" />);
      await waitFor(() => expect(shown()).toBe(4));
      boxHeight = 200.4;
      act(() => callbacks.forEach((callback) => callback()));
      expect(shown()).toBe(4);
      boxHeight = 320;
      act(() => callbacks.forEach((callback) => callback()));
      await waitFor(() => expect(shown()).toBe(7));
      act(() => callbacks.forEach((callback) => callback()));
      expect(shown()).toBe(7);
    });

    it("stops watching when it goes away", async () => {
      const { unmount } = inRouter(<FitList rows={rows(30)} moreTo="/queue" />);
      await waitFor(() => expect(shown()).toBe(4));
      unmount();
      expect(disconnected).toBe(1);
    });
  });
});
