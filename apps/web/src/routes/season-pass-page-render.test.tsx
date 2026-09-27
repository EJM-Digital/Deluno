import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { SeriesSeasonPassItem } from "../lib/api";
import { SeasonPassPage } from "./season-pass-page";

const authedFetchMock = vi.fn();
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const revalidateMock = vi.fn();
let loaderSeasons: SeriesSeasonPassItem[] = [];

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => ({ seasons: loaderSeasons }),
  useRevalidator: () => ({ revalidate: revalidateMock, state: "idle" })
}));

function season(overrides: Partial<SeriesSeasonPassItem> = {}): SeriesSeasonPassItem {
  return {
    seriesId: "series-1",
    seriesTitle: "Severance",
    seasonNumber: 1,
    episodeCount: 9,
    monitoredCount: 0,
    ...overrides
  };
}

describe("Season Pass bulk-toggle action (#791)", () => {
  beforeEach(() => {
    authedFetchMock.mockReset();
    revalidateMock.mockReset();
  });

  it("lists every series' seasons with episode counts and monitor state", () => {
    loaderSeasons = [season({ episodeCount: 9, monitoredCount: 4 })];
    render(
      <MemoryRouter>
        <SeasonPassPage />
      </MemoryRouter>
    );

    expect(screen.getByText("Severance")).toBeInTheDocument();
    expect(screen.getByText("Season 1")).toBeInTheDocument();
    expect(screen.getByText("4/9 (partial)")).toBeInTheDocument();
  });

  it("toggling a season's switch sends a scoped monitoring request for that season only", async () => {
    loaderSeasons = [season({ episodeCount: 9, monitoredCount: 0 })];
    authedFetchMock.mockResolvedValue({ ok: true } as Response);

    render(
      <MemoryRouter>
        <SeasonPassPage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("switch", { name: "Monitor Severance season 1" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith(
      "/api/series/season-pass/monitoring",
      expect.objectContaining({ method: "PUT" })
    ));
    const body = JSON.parse((authedFetchMock.mock.calls[0][1] as RequestInit).body as string);
    expect(body).toEqual({ scopes: [{ seriesId: "series-1", seasonNumber: 1 }], monitored: true });
    await waitFor(() => expect(revalidateMock).toHaveBeenCalled());
  });

  it("toggling the whole-series switch sends a series-wide scope", async () => {
    loaderSeasons = [
      season({ seasonNumber: 1, episodeCount: 9, monitoredCount: 9 }),
      season({ seasonNumber: 2, episodeCount: 8, monitoredCount: 8 })
    ];
    authedFetchMock.mockResolvedValue({ ok: true } as Response);

    render(
      <MemoryRouter>
        <SeasonPassPage />
      </MemoryRouter>
    );

    fireEvent.click(screen.getByRole("switch", { name: "Monitor all of Severance" }));

    await waitFor(() => expect(authedFetchMock).toHaveBeenCalledWith(
      "/api/series/season-pass/monitoring",
      expect.objectContaining({ method: "PUT" })
    ));
    const body = JSON.parse((authedFetchMock.mock.calls[0][1] as RequestInit).body as string);
    expect(body).toEqual({ scopes: [{ seriesId: "series-1", seasonNumber: null }], monitored: false });
  });
});
