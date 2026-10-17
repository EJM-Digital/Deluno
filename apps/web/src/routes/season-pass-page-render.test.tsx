import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { SeriesSeasonPassItem } from "../lib/api";
import { SeasonPassPage } from "./season-pass-page";

const authedFetchMock = vi.fn();
const toastError = vi.hoisted(() => vi.fn());
vi.mock("../components/shell/toaster", () => ({ toast: { error: toastError, success: vi.fn() } }));
vi.mock("../lib/use-auth", () => ({ authedFetch: (...args: unknown[]) => authedFetchMock(...args) }));

const revalidateMock = vi.fn();
let loaderSeasons: SeriesSeasonPassItem[] = [];
let loaderUnreadable = false;

vi.mock("react-router-dom", async (importOriginal) => ({
  ...(await importOriginal<typeof import("react-router-dom")>()),
  useLoaderData: () => ({ seasons: loaderSeasons, unreadable: loaderUnreadable }),
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
    loaderUnreadable = false;
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

  it("a failed change says Monitoring could not be changed. and leaves the switches usable again", async () => {
    loaderSeasons = [season({ episodeCount: 9, monitoredCount: 0 })];
    authedFetchMock.mockResolvedValue({ ok: false } as Response);

    render(<MemoryRouter><SeasonPassPage /></MemoryRouter>);
    fireEvent.click(screen.getByRole("switch", { name: "Monitor Severance season 1" }));

    await waitFor(() => expect(toastError).toHaveBeenCalledWith("Monitoring could not be changed."));
    await waitFor(() => expect(screen.getByRole("switch", { name: "Monitor Severance season 1" })).toBeEnabled());
    expect(revalidateMock).not.toHaveBeenCalled();
  });

  it("every switch is off while one change is saving", async () => {
    loaderSeasons = [season({ seasonNumber: 1, episodeCount: 9, monitoredCount: 0 }), season({ seasonNumber: 2, episodeCount: 8, monitoredCount: 0 })];
    let finish!: (value: Response) => void;
    authedFetchMock.mockReturnValue(new Promise<Response>((resolve) => { finish = resolve; }));

    render(<MemoryRouter><SeasonPassPage /></MemoryRouter>);
    fireEvent.click(screen.getByRole("switch", { name: "Monitor Severance season 1" }));

    await waitFor(() => expect(screen.getByRole("switch", { name: "Monitor Severance season 2" })).toBeDisabled());
    expect(screen.getByRole("switch", { name: "Monitor all of Severance" })).toBeDisabled();
    finish({ ok: true } as Response);
    await waitFor(() => expect(screen.getByRole("switch", { name: "Monitor Severance season 2" })).toBeEnabled());
  });

  it("a list that could not be read says so, with Try again, instead of No seasons yet", () => {
    loaderSeasons = [];
    loaderUnreadable = true;

    render(<MemoryRouter><SeasonPassPage /></MemoryRouter>);

    expect(screen.getByText("The seasons could not be read")).toBeInTheDocument();
    expect(screen.queryByText("No seasons yet")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(revalidateMock).toHaveBeenCalled();
  });

  it("a list that reads fine and is empty says No seasons yet", () => {
    loaderSeasons = [];

    render(<MemoryRouter><SeasonPassPage /></MemoryRouter>);

    expect(screen.getByText("No seasons yet")).toBeInTheDocument();
  });
});

describe("the Season Pass loader", () => {
  it("marks a failed read instead of swallowing it as an empty list", async () => {
    const api = await import("../lib/api");
    const spy = vi.spyOn(api, "fetchJson").mockRejectedValueOnce(new Error("down"));
    const { seasonPassLoader } = await import("./season-pass-page");

    expect(await seasonPassLoader()).toEqual({ seasons: [], unreadable: true });
    spy.mockRestore();
  });
});
