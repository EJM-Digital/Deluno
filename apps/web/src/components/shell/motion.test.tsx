import { render, screen } from "@testing-library/react";
import { Link, MemoryRouter, Route, Routes } from "react-router-dom";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import { PageTransition } from "./motion";

/** A route change replaces the page in place: the old page is gone at once and the new one starts fresh. */
function Counter({ name }: { name: string }) {
  const [count, setCount] = useState(0);
  return <button onClick={() => setCount(count + 1)}>{`${name} ${count}`}</button>;
}

function app(start: string) {
  return render(
    <MemoryRouter initialEntries={[start]}>
      <Link to="/b">go b</Link>
      <Link to="/a">go a</Link>
      <Routes>
        <Route element={<PageTransition />}>
          <Route path="/a" element={<Counter name="A" />} />
          <Route path="/b" element={<Counter name="B" />} />
        </Route>
      </Routes>
    </MemoryRouter>
  );
}

describe("PageTransition", () => {
  it("holds the page in a box at least 60% of the screen tall", () => {
    app("/a");
    expect(screen.getByRole("button", { name: "A 0" }).parentElement).toHaveClass("min-h-[60vh]");
  });

  it("replaces the page at once on a route change, leaving the old one nowhere", async () => {
    const user = userEvent.setup();
    app("/a");
    await user.click(screen.getByRole("button", { name: "A 0" }));
    expect(screen.getByRole("button", { name: "A 1" })).toBeInTheDocument();
    await user.click(screen.getByRole("link", { name: "go b" }));
    expect(screen.queryByRole("button", { name: /^A/ })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "B 0" })).toBeInTheDocument();
  });

  it("starts a page fresh when it is visited again", async () => {
    const user = userEvent.setup();
    app("/a");
    await user.click(screen.getByRole("button", { name: "A 0" }));
    await user.click(screen.getByRole("link", { name: "go b" }));
    await user.click(screen.getByRole("link", { name: "go a" }));
    expect(screen.getByRole("button", { name: "A 0" })).toBeInTheDocument();
  });

  it("shows nothing inside the box when the route has no page", () => {
    const { container } = render(
      <MemoryRouter>
        <Routes>
          <Route path="/" element={<PageTransition />} />
        </Routes>
      </MemoryRouter>
    );
    expect(container.firstElementChild).toHaveClass("min-h-[60vh]");
    expect(container.firstElementChild?.childElementCount).toBe(0);
  });
});
