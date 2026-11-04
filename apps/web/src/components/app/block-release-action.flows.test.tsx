import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";

const lib = vi.hoisted(() => ({
  checkBlockedMatch: vi.fn(),
  checkQueueMatch: vi.fn(),
  liftBlock: vi.fn(),
  blockReleaseOnly: vi.fn(),
  stopBlockAndSearchAgain: vi.fn()
}));
const describeRequestFailure = vi.hoisted(() => vi.fn());
vi.mock("../../lib/block-release", () => lib);
vi.mock("../../lib/search-reasons", () => ({ describeRequestFailure }));
vi.mock("../shell/toaster", () => ({ toast: { success: vi.fn(), error: vi.fn() } }));
const { toast } = (await import("../shell/toaster")) as unknown as { toast: { success: ReturnType<typeof vi.fn>; error: ReturnType<typeof vi.fn> } };

import { BlockReleaseAction } from "./block-release-action";

const RELEASE = "Arrival.2016.2160p";
const blockedRow = { id: "b1", releaseName: RELEASE };
const ok = { ok: true } as Response;
const refused = { ok: false } as Response;

function renderAction(props: Partial<React.ComponentProps<typeof BlockReleaseAction>> = {}) {
  const onBlocked = vi.fn();
  render(<BlockReleaseAction releaseName={RELEASE} indexerName="Nebula" mediaType="movie" entityId="m1" title="Arrival" onBlocked={onBlocked} {...props} />);
  return onBlocked;
}

describe("blocking a release from every angle", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    lib.checkBlockedMatch.mockResolvedValue(null);
    lib.checkQueueMatch.mockResolvedValue(null);
    lib.liftBlock.mockResolvedValue(ok);
    lib.blockReleaseOnly.mockResolvedValue({ response: ok, blocked: blockedRow });
    lib.stopBlockAndSearchAgain.mockResolvedValue(ok);
    describeRequestFailure.mockResolvedValue({ title: "Explained", description: "Because." });
  });

  it("is a small outlined button by default, and takes the size, look and class it was given", () => {
    renderAction();
    const button = screen.getByRole("button", { name: 'Block "Arrival"' });
    expect(button).toHaveClass("border-hairline", "h-(--control-height-sm)");
    expect(button).toHaveTextContent("Block this release");
  });

  it("takes another size, look and class when asked", () => {
    renderAction({ size: "default", variant: "destructive", className: "extra" });
    expect(screen.getByRole("button", { name: 'Block "Arrival"' })).toHaveClass("h-(--control-height)", "text-destructive", "extra");
  });

  it("names the release when the title is missing", () => {
    renderAction({ title: undefined });
    expect(screen.getByRole("button", { name: `Block "${RELEASE}"` })).toBeInTheDocument();
  });

  it("asks again whether it is blocked when a different release is shown, and not when something else changes", async () => {
    const view = render(<BlockReleaseAction releaseName="One" indexerName="Nebula" mediaType="movie" entityId="m1" />);
    await waitFor(() => expect(lib.checkBlockedMatch).toHaveBeenCalledTimes(1));
    view.rerender(<BlockReleaseAction releaseName="One" indexerName="Nebula" mediaType="movie" entityId="m2" />);
    expect(lib.checkBlockedMatch).toHaveBeenCalledTimes(1);
    view.rerender(<BlockReleaseAction releaseName="Two" indexerName="Nebula" mediaType="movie" entityId="m2" />);
    await waitFor(() => expect(lib.checkBlockedMatch).toHaveBeenCalledTimes(2));
    view.rerender(<BlockReleaseAction releaseName="Two" hash="abc" indexerName="Nebula" mediaType="movie" entityId="m2" />);
    await waitFor(() => expect(lib.checkBlockedMatch).toHaveBeenCalledTimes(3));
  });

  it("does not turn into Lift the block from an answer that arrives after it has gone", async () => {
    let answer: (value: unknown) => void = () => undefined;
    lib.checkBlockedMatch.mockReturnValue(new Promise((resolve) => { answer = resolve; }));
    const { unmount } = render(<BlockReleaseAction releaseName={RELEASE} indexerName="Nebula" mediaType="movie" entityId="m1" />);
    unmount();
    answer(blockedRow);
    await Promise.resolve();
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("checks the queue when opened, and says so in the question while it checks", async () => {
    let answer: (value: unknown) => void = () => undefined;
    lib.checkQueueMatch.mockReturnValue(new Promise((resolve) => { answer = resolve; }));
    renderAction();

    await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));

    expect(await screen.findByText('Block "Arrival"?')).toBeInTheDocument();
    expect(screen.getByText("Checking whether it's downloading right now…")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Block this release" })).toBeDisabled();
    answer(null);
    expect(await screen.findByText(`Refuses every indexer's copy of "${RELEASE}" from now on. Nothing already downloaded is touched.`)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Block this release" })).toBeEnabled();
  });

  it("only blocks it when nothing is downloading, tells the person, and turns into Lift the block", async () => {
    lib.checkBlockedMatch.mockResolvedValueOnce(null).mockResolvedValue(blockedRow);
    const onBlocked = renderAction();

    await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));
    await userEvent.click(await screen.findByRole("button", { name: "Block this release" }));

    await waitFor(() => expect(toast.success).toHaveBeenCalledWith(`"Arrival" blocked; every indexer's copy is refused from now on.`));
    expect(lib.blockReleaseOnly).toHaveBeenCalledWith(expect.objectContaining({ releaseName: RELEASE }));
    expect(onBlocked).toHaveBeenCalledTimes(1);
    expect(await screen.findByRole("button", { name: 'Lift the block on "Arrival"' })).toBeInTheDocument();
    expect(screen.queryByText('Block "Arrival"?')).toBeNull();
  });

  it("works without anyone listening for the block", async () => {
    renderAction({ onBlocked: undefined });
    await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));
    await userEvent.click(await screen.findByRole("button", { name: "Block this release" }));
    await waitFor(() => expect(toast.success).toHaveBeenCalled());
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("explains in the server's words when the block is refused, keeps the question open and frees the button", async () => {
    lib.blockReleaseOnly.mockResolvedValue({ response: refused, blocked: null });
    const onBlocked = renderAction();

    await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));
    await userEvent.click(await screen.findByRole("button", { name: "Block this release" }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Explained", { description: "Because." }));
    expect(describeRequestFailure).toHaveBeenCalledWith(null, expect.any(Error), { action: "block this release" });
    expect(describeRequestFailure.mock.calls[0][1].message).toBe("block-release-failed");
    expect(onBlocked).not.toHaveBeenCalled();
    expect(toast.success).not.toHaveBeenCalled();
    expect(screen.getByText('Block "Arrival"?')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("button", { name: "Block this release" })).toBeEnabled());
  });

  describe("a release that is downloading right now", () => {
    const match = { clientId: "c1", clientName: "qBittorrent", queueItemId: "q1" };
    beforeEach(() => { lib.checkQueueMatch.mockResolvedValue(match); });

    it("says where it found it, and offers to stop it, delete it and find another, ticked", async () => {
      renderAction();
      await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));

      expect(await screen.findByText("Deluno found this release downloading in qBittorrent right now. Blocking it refuses every indexer's copy from now on.")).toBeInTheDocument();
      expect(screen.getByRole("checkbox", { name: /Stop it, delete it and find another/ })).toBeChecked();
      expect(screen.getByText("Also removes it from qBittorrent, deletes its files, and starts a replacement search. Left unticked, only the block itself happens.")).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Stop it, delete its files and find another" })).toBeEnabled();
    });

    it("stops it, tells the person what was done, and reports the block", async () => {
      const onBlocked = renderAction();
      await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));
      await userEvent.click(await screen.findByRole("button", { name: "Stop it, delete its files and find another" }));

      await waitFor(() => expect(toast.success).toHaveBeenCalledWith(`"Arrival" stopped in qBittorrent, its files deleted, blocked everywhere, and a replacement search started.`));
      expect(lib.stopBlockAndSearchAgain).toHaveBeenCalledWith("c1", "q1");
      expect(lib.blockReleaseOnly).not.toHaveBeenCalled();
      expect(onBlocked).toHaveBeenCalledTimes(1);
      await waitFor(() => expect(screen.queryByText('Block "Arrival"?')).toBeNull());
    });

    it("works without anyone listening for the block", async () => {
      renderAction({ onBlocked: undefined });
      await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));
      await userEvent.click(await screen.findByRole("button", { name: "Stop it, delete its files and find another" }));
      await waitFor(() => expect(toast.success).toHaveBeenCalled());
    expect(toast.error).not.toHaveBeenCalled();
    });

    it("only blocks it when the person unticks the box, and says so on the button", async () => {
      renderAction();
      await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));
      await userEvent.click(await screen.findByRole("checkbox", { name: /Stop it, delete it and find another/ }));
      await userEvent.click(screen.getByRole("button", { name: "Just block it" }));

      await waitFor(() => expect(lib.blockReleaseOnly).toHaveBeenCalled());
      expect(lib.stopBlockAndSearchAgain).not.toHaveBeenCalled();
    });

    it("ticks the box again each time the question is opened", async () => {
      renderAction();
      await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));
      await userEvent.click(await screen.findByRole("checkbox", { name: /Stop it, delete it and find another/ }));
      await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
      await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));
      expect(await screen.findByRole("checkbox", { name: /Stop it, delete it and find another/ })).toBeChecked();
    });

    it("forgets what it found the last time when the queue no longer holds it", async () => {
      renderAction();
      await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));
      await screen.findByRole("checkbox", { name: /Stop it/ });
      await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
      lib.checkQueueMatch.mockReset();
      lib.checkQueueMatch.mockReturnValue(new Promise(() => undefined));
      await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));
      expect(await screen.findByText("Checking whether it's downloading right now…")).toBeInTheDocument();
      expect(screen.queryByRole("checkbox", { name: /Stop it/ })).toBeNull();
    });

    it("explains when stopping it fails, naming the action", async () => {
      lib.stopBlockAndSearchAgain.mockResolvedValue(refused);
      const onBlocked = renderAction();
      await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));
      await userEvent.click(await screen.findByRole("button", { name: "Stop it, delete its files and find another" }));

      await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Explained", { description: "Because." }));
      expect(describeRequestFailure).toHaveBeenCalledWith(null, expect.any(Error), { action: "stop and block this release" });
      expect(describeRequestFailure.mock.calls[0][1].message).toBe("stop-and-block-failed");
      expect(onBlocked).not.toHaveBeenCalled();
      expect(screen.getByText('Block "Arrival"?')).toBeInTheDocument();
      await waitFor(() => expect(screen.getByRole("button", { name: "Stop it, delete its files and find another" })).toBeEnabled());
    });

    it("keeps the box and the buttons still while it is working", async () => {
      lib.stopBlockAndSearchAgain.mockReturnValue(new Promise(() => undefined));
      renderAction();
      await userEvent.click(screen.getByRole("button", { name: 'Block "Arrival"' }));
      await userEvent.click(await screen.findByRole("button", { name: "Stop it, delete its files and find another" }));

      await waitFor(() => expect(screen.getByRole("checkbox", { name: /Stop it/ })).toBeDisabled());
      expect(screen.getByRole("button", { name: "Cancel" })).toBeDisabled();
    });
  });

  describe("a release that is already blocked", () => {
    beforeEach(() => { lib.checkBlockedMatch.mockResolvedValue(blockedRow); });

    it("opens straight to the lift question without checking the queue", async () => {
      renderAction();
      await userEvent.click(await screen.findByRole("button", { name: 'Lift the block on "Arrival"' }));

      expect(await screen.findByText('Lift the block on "Arrival"?')).toBeInTheDocument();
      expect(screen.getByText("Deluno will consider this release again. Nothing is searched for automatically — search for the title yourself when you want it.")).toBeInTheDocument();
      expect(lib.checkQueueMatch).not.toHaveBeenCalled();
    });

    it("lifts it, says it can be used again, reports it and turns back into Block", async () => {
      const onBlocked = renderAction();
      await userEvent.click(await screen.findByRole("button", { name: 'Lift the block on "Arrival"' }));
      await userEvent.click(await screen.findByRole("button", { name: "Lift the block" }));

      await waitFor(() => expect(toast.success).toHaveBeenCalledWith(`"Arrival" can be used again. Search for the title when you want it.`));
      expect(lib.liftBlock).toHaveBeenCalledWith("b1");
      expect(onBlocked).toHaveBeenCalledTimes(1);
      expect(await screen.findByRole("button", { name: 'Block "Arrival"' })).toBeInTheDocument();
      expect(screen.queryByText('Lift the block on "Arrival"?')).toBeNull();
    });

    it("works without anyone listening for the lift", async () => {
      renderAction({ onBlocked: undefined });
      expect(await screen.findByRole("button", { name: 'Lift the block on "Arrival"' })).toHaveTextContent("Lift the block");
      await userEvent.click(await screen.findByRole("button", { name: 'Lift the block on "Arrival"' }));
      await userEvent.click(await screen.findByRole("button", { name: "Lift the block" }));
      await waitFor(() => expect(toast.success).toHaveBeenCalled());
    expect(toast.error).not.toHaveBeenCalled();
    });

    it("explains when lifting fails, keeps the question open and frees the button", async () => {
      lib.liftBlock.mockResolvedValue(refused);
      const onBlocked = renderAction();
      await userEvent.click(await screen.findByRole("button", { name: 'Lift the block on "Arrival"' }));
      await userEvent.click(await screen.findByRole("button", { name: "Lift the block" }));

      await waitFor(() => expect(toast.error).toHaveBeenCalledWith("Explained", { description: "Because." }));
      expect(describeRequestFailure).toHaveBeenCalledWith(null, expect.any(Error), { action: "lift this block" });
      expect(describeRequestFailure.mock.calls[0][1].message).toBe("lift-block-failed");
      expect(onBlocked).not.toHaveBeenCalled();
      expect(screen.getByText('Lift the block on "Arrival"?')).toBeInTheDocument();
      await waitFor(() => expect(screen.getByRole("button", { name: "Lift the block" })).toBeEnabled());
    });

    it("keeps its buttons still while the lift is under way", async () => {
      lib.liftBlock.mockReturnValue(new Promise(() => undefined));
      renderAction();
      await userEvent.click(await screen.findByRole("button", { name: 'Lift the block on "Arrival"' }));
      await userEvent.click(await screen.findByRole("button", { name: "Lift the block" }));
      await waitFor(() => expect(screen.getByRole("button", { name: "Cancel" })).toBeDisabled());
    });
  });
});
