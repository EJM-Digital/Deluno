import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

/** System > Metadata maintenance, when a check or a refresh cannot run (#1329). */

const fetchJsonMock = vi.hoisted(() => vi.fn());
vi.mock("../../lib/api", async (importOriginal) => ({ ...(await importOriginal<typeof import("../../lib/api")>()), fetchJson: fetchJsonMock }));

const { MetadataMaintenanceCard } = await import("./metadata-maintenance-card");

const ready = { provider: "deluno", isConfigured: true, mode: "broker", message: "ready", sources: [] };

afterEach(() => fetchJsonMock.mockReset());

describe("A Check now that cannot run", () => {
  it("shows the error text under the row, an unconfigured Failed chip, and turns the buttons back on", async () => {
    fetchJsonMock.mockImplementation(async (url: string) => {
      if (url === "/api/metadata/status") return ready;
      throw new Error("Deluno could not reach the metadata service.");
    });
    render(<MetadataMaintenanceCard />);
    await screen.findByText("Ready");

    fireEvent.click(screen.getByRole("button", { name: "Check now" }));

    expect(await screen.findByText("The check could not reach the service")).toBeInTheDocument();
    expect(screen.getByText("Deluno could not reach the metadata service.")).toBeInTheDocument();
    expect(screen.getByText("Failed")).toBeInTheDocument();
    expect(screen.queryByText("Ready")).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: "Check now" })).not.toBeDisabled());
  });

  it("with a typeless error it says 'The check could not be run.'", async () => {
    fetchJsonMock.mockImplementation(async (url: string) => {
      if (url === "/api/metadata/status") return ready;
      throw "nope";
    });
    render(<MetadataMaintenanceCard />);
    await screen.findByText("Ready");

    fireEvent.click(screen.getByRole("button", { name: "Check now" }));
    expect(await screen.findByText("The check could not be run.")).toBeInTheDocument();
  });
});

describe("A refresh job that fails to queue", () => {
  it("shows 'Could not queue' or the error text under that row only, with every button off while it runs", async () => {
    let fail: (error: unknown) => void = () => undefined;
    fetchJsonMock.mockImplementation((url: string) => (url === "/api/metadata/status" ? Promise.resolve(ready) : new Promise((_resolve, reject) => { fail = reject; })));
    render(<MetadataMaintenanceCard />);
    await screen.findByText("Ready");

    const row = (name: string) => screen.getByText(name).closest("[role=row]") as HTMLElement;
    fireEvent.click(within(row("Refresh all Movies")).getByRole("button", { name: "Run" }));
    await waitFor(() => expect(screen.getByRole("button", { name: "Check now" })).toBeDisabled());
    for (const button of screen.getAllByRole("button", { name: "Run" })) expect(button).toBeDisabled();

    fail(new Error("The metadata service is paused."));
    expect(await within(row("Refresh all Movies")).findByText("The metadata service is paused.")).toBeInTheDocument();
    expect(within(row("Refresh all TV shows")).queryByText("The metadata service is paused.")).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: "Check now" })).not.toBeDisabled());

    fetchJsonMock.mockImplementation((url: string) => (url === "/api/metadata/status" ? Promise.resolve(ready) : Promise.reject("odd")));
    fireEvent.click(within(row("Refresh all TV shows")).getByRole("button", { name: "Run" }));
    expect(await within(row("Refresh all TV shows")).findByText("Could not queue")).toBeInTheDocument();
  });
});
