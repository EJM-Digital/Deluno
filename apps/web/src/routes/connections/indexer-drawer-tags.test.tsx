import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { IndexerDrawerBody } from "./indexer-drawer-body";
import { emptyIndexerForm } from "./forms";
import { emptyPlatformSettingsSnapshot } from "../../lib/api/types/settings";

/** #1042: the indexer's tags use the shared picker, not a free-text box. */
describe("IndexerDrawerBody tags (#1042)", () => {
  it("shows the stored tags as chips and removing one updates the form's tag string", async () => {
    const setForm = vi.fn();
    render(
      <IndexerDrawerBody
        form={{ ...emptyIndexerForm(), tags: "Kids, Anime" }}
        setForm={setForm}
        editing={null}
        settings={emptyPlatformSettingsSnapshot}
        proxies={[]}
        throttle={null}
        errors={{}}
        clearError={() => {}}
        showKey={false}
        setShowKey={() => {}}
        fineTuneOpen
        setFineTuneOpen={() => {}}
        busy={null}
        onReset={() => {}}
        onAcknowledgeVip={() => {}}
        onRemove={() => {}}
      />
    );
    expect(screen.getByRole("textbox", { name: "Indexer tags" })).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Remove Anime from Indexer tags" }));
    const update = setForm.mock.calls.at(-1)![0] as (current: { tags: string }) => { tags: string };
    expect(update({ tags: "Kids, Anime" }).tags).toBe("Kids");
  });
});
