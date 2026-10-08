import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { IndexerDrawerBody } from "./indexer-drawer-body";
import { emptyIndexerForm } from "./forms";
import { emptyPlatformSettingsSnapshot } from "../../lib/api/types/settings";

/**
 * #975: "What MULTI means here" (the indexer's own languages for a release
 * tagged MULTI) must show for every indexer scope. It used to be gated to
 * "tv" or "both" even though the backend applies the setting to movies too
 * (ReleaseDecisionEngine.cs -> ReleasePreferenceFactFactory.cs), so a
 * Movies-only indexer had no way to set it.
 */

function renderDrawer(scope: "movies" | "tv" | "both") {
  const form = { ...emptyIndexerForm(), scope };
  render(
    <IndexerDrawerBody
      form={form}
      setForm={() => {}}
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
}

describe("IndexerDrawerBody MULTI languages field (#975)", () => {
  it("shows for a Movies-only indexer", () => {
    renderDrawer("movies");
    expect(screen.getByText("What MULTI means here")).toBeTruthy();
  });

  it("shows for a TV-only indexer", () => {
    renderDrawer("tv");
    expect(screen.getByText("What MULTI means here")).toBeTruthy();
  });

  it("shows for a Movies and TV indexer", () => {
    renderDrawer("both");
    expect(screen.getByText("What MULTI means here")).toBeTruthy();
  });
});

describe("IndexerDrawerBody name field", () => {
  function renderWith(baseUrl: string, name = "") {
    render(
      <IndexerDrawerBody
        form={{ ...emptyIndexerForm(), protocol: "torznab", baseUrl, name }}
        setForm={() => {}}
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
    return screen.getByLabelText("Name") as HTMLInputElement;
  }

  it("hints the address's own host name, never the computer's", () => {
    expect(renderWith("https://www.tracker.example.test/api").placeholder).toBe("tracker.example.test");
  });

  it("hints the address as written for an indexer on this computer", () => {
    expect(renderWith("http://localhost:9117/api/v2.0/indexers/x/results/torznab/").placeholder).toBe("localhost");
  });

  it("shows a name that was typed", () => {
    expect(renderWith("https://tracker.example.test/api", "My tracker").value).toBe("My tracker");
  });
});
