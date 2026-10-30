import { render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { describe, expect, it } from "vitest";
import { MoviesWorkspaceLayout, TvWorkspaceLayout } from "./media-workspace-shell";

describe("the movies and TV workspace shells", () => {
  it.each([
    ["movies", MoviesWorkspaceLayout],
    ["tv", TvWorkspaceLayout]
  ])("add nothing around the page inside %s", (path, Layout) => {
    render(
      <MemoryRouter initialEntries={[`/${path}/list`]}>
        <Routes>
          <Route path={`/${path}`} element={<Layout />}>
            <Route path="list" element={<p>The list</p>} />
          </Route>
        </Routes>
      </MemoryRouter>
    );
    expect(screen.getByText("The list")).toBeVisible();
  });
});
