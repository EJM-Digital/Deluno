import { render, screen, within, fireEvent } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";
import type { IndexerItem, OutboundThrottleHostState } from "../../lib/api";
import { emptyPlatformSettingsSnapshot } from "../../lib/api/types/settings";
import { describeCustomSharingRule, describeGlobalSharingRule, describeSeasonPackSeedTime, describeStrictSharingRule } from "../../lib/sharing-rule";
import { emptyIndexerForm, STRICT_SHARING, type IndexerForm } from "./forms";
import { describeCallLimitStatus } from "./format";
import { IndexerDrawerBody } from "./indexer-drawer-body";

vi.mock("../../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../lib/api")>()),
  fetchJson: vi.fn(async (url: string) => {
    if (url === "/api/tags") return [{ id: "t1", name: "kids", color: "slate", description: "", createdUtc: "", updatedUtc: "" }];
    return [];
  })
}));
vi.mock("../../lib/use-auth", () => ({ authedFetch: vi.fn() }));
vi.mock("./tracker-fields", () => ({
  TrackerFields: (props: { cloudflareExplained: boolean }) => <div data-testid="tracker-fields" data-cloudflare={String(props.cloudflareExplained)} />
}));

/**
 * An indexer's drawer, on a real form: what each box says, what typing in it does
 * to the form (and to its error), and what the Health section tells a person
 * about an indexer that is already saved.
 */

const DAY = 24 * 60 * 60 * 1000;

function saved(overrides: Partial<IndexerItem> = {}): IndexerItem {
  return {
    id: "ix1",
    name: "Example",
    protocol: "torznab",
    privacy: "unknown",
    baseUrl: "https://tracker.example.test",
    priority: 10,
    categories: "2000",
    tags: "",
    isEnabled: true,
    healthStatus: "healthy",
    lastHealthMessage: null,
    consecutiveFailures: 0,
    rateLimitedUntilUtc: null,
    disabledReason: null,
    createdUtc: "2026-10-01T00:00:00Z",
    updatedUtc: "2026-10-01T00:00:00Z",
    ...overrides
  } as IndexerItem;
}

type Options = {
  form?: Partial<IndexerForm>;
  editing?: IndexerItem | null;
  errors?: Record<string, string>;
  busy?: string | null;
  throttle?: OutboundThrottleHostState | null;
  testMessage?: string | null;
  fineTuneOpen?: boolean;
  timingOpen?: boolean;
  showKey?: boolean;
  routeOnAdd?: boolean;
  noRouteHandler?: boolean;
};

function open(options: Options = {}) {
  const seen = { form: { ...emptyIndexerForm(), ...options.form }, fineTuneOpen: options.fineTuneOpen ?? true, timingOpen: options.timingOpen ?? options.fineTuneOpen ?? true, showKey: options.showKey ?? false };
  const spies = {
    clearError: vi.fn(),
    onReset: vi.fn(),
    onAcknowledgeVip: vi.fn(),
    onRemove: vi.fn(),
    onRouteOnAddChange: vi.fn(),
    setShowKey: vi.fn()
  };
  function Live() {
    const [form, setForm] = useState(seen.form);
    const [fineTuneOpen, setFineTuneOpen] = useState(seen.fineTuneOpen);
    const [timingOpen, setTimingOpen] = useState(seen.timingOpen);
    const [showKey, setShowKey] = useState(seen.showKey);
    seen.form = form;
    seen.fineTuneOpen = fineTuneOpen;
    seen.timingOpen = timingOpen;
    seen.showKey = showKey;
    return (
      <MemoryRouter>
        <IndexerDrawerBody
          form={form}
          setForm={setForm}
          editing={options.editing ?? null}
          settings={emptyPlatformSettingsSnapshot}
          proxies={[{ id: "px1", name: "Home proxy" } as never, { id: "px2", name: "Office proxy" } as never]}
          throttle={options.throttle ?? null}
          errors={options.errors ?? {}}
          clearError={spies.clearError}
          showKey={showKey}
          setShowKey={(value) => { spies.setShowKey(value); setShowKey(value); }}
          fineTuneOpen={fineTuneOpen}
          setFineTuneOpen={setFineTuneOpen}
          timingOpen={timingOpen}
          setTimingOpen={setTimingOpen}
          busy={options.busy ?? null}
          onReset={spies.onReset}
          onAcknowledgeVip={spies.onAcknowledgeVip}
          onRemove={spies.onRemove}
          testMessage={options.testMessage}
          {...(options.noRouteHandler ? {} : { routeOnAdd: options.routeOnAdd, onRouteOnAddChange: spies.onRouteOnAddChange })}
        />
      </MemoryRouter>
    );
  }
  render(<Live />);
  return { seen, ...spies };
}

/** The value printed beside a label in the Health list. */
function fact(label: string) {
  return screen.getByText(label, { selector: "dt" }).nextElementSibling as HTMLElement;
}

describe("the protocol and address", () => {
  it("offers the five kinds, with the hint for the chosen one while the indexer is new", () => {
    open({ form: { protocol: "torznab" } });
    const options = within(screen.getByLabelText("Protocol")).getAllByRole("option");
    expect(options.map((option) => [option.getAttribute("value"), option.textContent])).toEqual([
      ["tracker", "Tracker (built in)"],
      ["torznab", "Torznab"],
      ["newznab", "Newznab"],
      ["rss", "RSS feed"],
      ["custom", "Custom"]
    ]);
    expect(screen.getByLabelText("Protocol")).toBeEnabled();
    expect(screen.getByText("Any Torznab-compatible tracker or private site.")).toBeInTheDocument();
  });

  it("locks the protocol, and drops the hint, once the indexer is saved", () => {
    open({ form: { protocol: "torznab" }, editing: saved() });
    expect(screen.getByLabelText("Protocol")).toBeDisabled();
    expect(screen.queryByText("Any Torznab-compatible tracker or private site.")).not.toBeInTheDocument();
  });

  it("starts the categories again for the new protocol and keeps the scope", async () => {
    const user = userEvent.setup();
    const { seen } = open({ form: { protocol: "newznab", scope: "movies", categories: "9999" } });
    await user.selectOptions(screen.getByLabelText("Protocol"), "torznab");
    expect(seen.form).toMatchObject({ protocol: "torznab", scope: "movies", categories: "2000,2010,2020,2030,2040,2045,2050,2060,2070" });
    await user.selectOptions(screen.getByLabelText("Protocol"), "rss");
    expect(seen.form).toMatchObject({ protocol: "rss", categories: "" });
  });

  it.each([
    ["torznab", "Torznab URL", "http://localhost:9117/api/v2.0/indexers/XXX/results/torznab/"],
    ["newznab", "Newznab URL", "https://api.nzbgeek.info"],
    ["rss", "URL", "https://example.com/feed.rss"],
    ["custom", "URL", "https://example.com"]
  ] as const)("asks a %s indexer for its %s", (protocol, label, placeholder) => {
    open({ form: { protocol } });
    const box = screen.getByLabelText(label);
    expect(box).toHaveAttribute("placeholder", placeholder);
    expect(box).toHaveClass("font-mono");
    expect(box).toHaveAttribute("autocomplete", "off");
    expect(box).toHaveAttribute("spellcheck", "false");
  });

  it("shows the API path only for Newznab and Torznab", () => {
    open({ form: { protocol: "newznab" } });
    expect(screen.getByLabelText("API path")).toHaveAttribute("placeholder", "/api");
    expect(screen.getByText("Only needed if this indexer's API does not live at the default /api.")).toBeInTheDocument();
  });

  it.each(["rss", "custom"] as const)("hides the API path for %s", (protocol) => {
    open({ form: { protocol } });
    expect(screen.queryByLabelText("API path")).not.toBeInTheDocument();
  });

  it("hints the name from the address, without www or api in front", () => {
    open({ form: { protocol: "torznab", baseUrl: "https://www.tracker.example.test/path" } });
    expect(screen.getByLabelText("Name")).toHaveAttribute("placeholder", "tracker.example.test");
    expect(screen.getByLabelText("Name")).toHaveAttribute("autocomplete", "off");
    expect(screen.getByLabelText("Name")).toHaveAttribute("spellcheck", "false");
  });

  it("takes www or api off the front only, not out of the middle", () => {
    open({ form: { protocol: "torznab", baseUrl: "https://tracker-api.example.test" } });
    expect(screen.getByLabelText("Name")).toHaveAttribute("placeholder", "tracker-api.example.test");
  });

  it("falls back to a plain hint when the address is not an address", () => {
    open({ form: { protocol: "torznab", baseUrl: "not a url" } });
    expect(screen.getByLabelText("Name")).toHaveAttribute("placeholder", "The indexer's name");
  });

  it("explains the name", () => {
    open();
    expect(screen.getByText("Leave it blank to use the address's own name. It is never the name of the computer Deluno runs on.")).toBeInTheDocument();
  });
});

describe("the API key", () => {
  it("is asked for by the kinds that need one, with where to find it", () => {
    open({ form: { protocol: "newznab" } });
    expect(screen.getByLabelText("API key")).toHaveAttribute("placeholder", "Paste your API key");
    expect(screen.getByLabelText("API key")).toHaveAttribute("type", "password");
    expect(screen.getByText("From your indexer's account or settings page.")).toBeInTheDocument();
  });

  it("is not asked for by a kind that has none", () => {
    open({ form: { protocol: "rss" } });
    expect(screen.queryByLabelText("API key")).not.toBeInTheDocument();
  });

  it("is shown for a kind that has none once a key has been typed", () => {
    open({ form: { protocol: "rss", apiKey: "abc" } });
    expect(screen.getByLabelText("API key")).toHaveValue("abc");
  });

  it("says a saved key is kept unless a new one is pasted", () => {
    open({ form: { protocol: "torznab" }, editing: saved() });
    expect(screen.getByText("Stored encrypted. Leave empty to keep the current key; paste a new one to rotate it.")).toBeInTheDocument();
    expect(screen.getByLabelText("API key")).toHaveAttribute("placeholder", "••••••••••••  (unchanged)");
  });

  it("is revealed and hidden by the button beside it", async () => {
    const user = userEvent.setup();
    const { setShowKey } = open({ form: { protocol: "torznab", apiKey: "secret" } });
    expect(screen.getByLabelText("API key")).toHaveAttribute("type", "password");
    await user.click(screen.getByRole("button", { name: "Reveal" }));
    expect(setShowKey).toHaveBeenLastCalledWith(true);
    expect(screen.getByLabelText("API key")).toHaveAttribute("type", "text");
    await user.click(screen.getByRole("button", { name: "Hide" }));
    expect(setShowKey).toHaveBeenLastCalledWith(false);
    expect(screen.getByLabelText("API key")).toHaveAttribute("type", "password");
  });

  it("is already revealed when the drawer is told so", () => {
    open({ form: { protocol: "torznab" }, showKey: true });
    expect(screen.getByLabelText("API key")).toHaveAttribute("type", "text");
    expect(screen.getByRole("button", { name: "Hide" })).toBeInTheDocument();
  });
});

describe("a built-in tracker", () => {
  it("shows the tracker's own fields instead of the address, name and key", () => {
    open({ form: { protocol: "tracker" } });
    expect(screen.getByTestId("tracker-fields")).toBeInTheDocument();
    expect(screen.queryByLabelText("Name")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("API key")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("URL")).not.toBeInTheDocument();
  });

  it("does not show them for the other kinds", () => {
    open({ form: { protocol: "torznab" } });
    expect(screen.queryByTestId("tracker-fields")).not.toBeInTheDocument();
  });

  it("tells the fields a Cloudflare note is already on screen when the last test said so", () => {
    open({ form: { protocol: "tracker" }, testMessage: "Behind a Cloudflare check." });
    expect(screen.getByTestId("tracker-fields")).toHaveAttribute("data-cloudflare", "true");
  });

  it("tells them the same when the saved health message says so", () => {
    open({ form: { protocol: "tracker" }, editing: saved({ lastHealthMessage: "Blocked by Cloudflare." }) });
    expect(screen.getByTestId("tracker-fields")).toHaveAttribute("data-cloudflare", "true");
  });

  it("tells them there is nothing to repeat otherwise", () => {
    open({ form: { protocol: "tracker" }, editing: saved({ lastHealthMessage: "Reached it." }), testMessage: "Fine." });
    expect(screen.getByTestId("tracker-fields")).toHaveAttribute("data-cloudflare", "false");
  });

  it("tells them the same for a new tracker with no test yet", () => {
    open({ form: { protocol: "tracker" } });
    expect(screen.getByTestId("tracker-fields")).toHaveAttribute("data-cloudflare", "false");
  });
});

describe("what each box does to the form", () => {
  const boxes = [
    ["Name", "name", null],
    ["Torznab URL", "baseUrl", "baseUrl"],
    ["API path", "apiPath", null],
    ["API key", "apiKey", "apiKey"],
    ["Request interval", "requestIntervalSeconds", "requestIntervalSeconds"],
    ["Query limit", "queryLimit", "queryLimit"],
    ["Grab limit", "grabLimit", "grabLimit"],
    ["Additional parameters", "additionalParameters", null],
    ["What MULTI means here", "multiLanguages", "multiLanguages"],
    ["Minimum age", "minimumAgeMinutes", "minimumAgeMinutes"],
    ["Retention", "retentionDays", "retentionDays"],
    ["Maximum size", "maximumSizeMb", "maximumSizeMb"],
    ["Availability delay", "availabilityDelayDays", "availabilityDelayDays"],
    ["Minimum seeders", "minimumSeeders", "minimumSeeders"],
    ["Required words", "requiredWords", "requiredWords"],
    ["Ignored words", "ignoredWords", "ignoredWords"]
  ] as const;

  it.each(boxes)("%s changes only %s, and clears the error for %s", async (label, key, errorKey) => {
    const user = userEvent.setup();
    const { seen, clearError } = open({ form: { protocol: "torznab", priority: "25", minimumSeeders: "" } });
    await user.type(screen.getByLabelText(label), "7");
    expect(seen.form[key as keyof IndexerForm]).toBe("7");
    expect(seen.form.priority).toBe("25");
    if (["API path", "API key", "Additional parameters"].includes(label)) {
      expect(screen.getByLabelText(label)).toHaveAttribute("spellcheck", "false");
      expect(screen.getByLabelText(label)).toHaveAttribute("autocomplete", "off");
    }
    if (errorKey) expect(clearError).toHaveBeenCalledWith(errorKey);
    else expect(clearError).not.toHaveBeenCalled();
  });

  it("changes only the VIP expiry for a saved indexer, and clears its error", () => {
    const { seen, clearError } = open({ form: { protocol: "torznab" }, editing: saved() });
    fireEvent.change(screen.getByLabelText("VIP/subscription expiry"), { target: { value: "2027-01-31" } });
    expect(seen.form.vipExpiresUtc).toBe("2027-01-31");
    expect(clearError).toHaveBeenCalledWith("vipExpiresUtc");
  });

  it("shows every problem beside its own box", () => {
    const errors = {
      baseUrl: "e-baseUrl", apiKey: "e-apiKey", requestIntervalSeconds: "e-interval", queryLimit: "e-query", grabLimit: "e-grab",
      multiLanguages: "e-multi", minimumAgeMinutes: "e-age", retentionDays: "e-retention", maximumSizeMb: "e-size",
      availabilityDelayDays: "e-delay", minimumSeeders: "e-seeders", preferIndexerFlags: "e-flags", requiredWords: "e-required",
      ignoredWords: "e-ignored", vipExpiresUtc: "e-vip"
    };
    open({ form: { protocol: "torznab" }, editing: saved(), errors });
    for (const message of Object.values(errors)) expect(screen.getByText(message)).toBeInTheDocument();
  });

  it("shows no problem when there are none", () => {
    open({ form: { protocol: "torznab" }, editing: saved() });
    expect(screen.queryByText(/^e-/)).not.toBeInTheDocument();
  });

  it("limits what can be typed in the numeric boxes", () => {
    open({ form: { protocol: "torznab" } });
    const interval = screen.getByLabelText("Request interval");
    expect(interval).toHaveAttribute("type", "number");
    expect(interval).toHaveAttribute("min", "2");
    expect(interval).toHaveAttribute("max", "60");
    expect(interval).toHaveAttribute("step", "1");
    expect(interval).toHaveAttribute("placeholder", "Deluno default (2 seconds)");
    expect(screen.getByLabelText("Query limit")).toHaveAttribute("min", "0");
    expect(screen.getByLabelText("Grab limit")).toHaveAttribute("min", "0");
    expect(screen.getByLabelText("Minimum age")).toHaveAttribute("min", "0");
    expect(screen.getByLabelText("Retention")).toHaveAttribute("min", "0");
    expect(screen.getByLabelText("Maximum size")).toHaveAttribute("min", "0");
    expect(screen.getByLabelText("Availability delay")).toHaveAttribute("min", "0");
    expect(screen.getByLabelText("Minimum seeders")).toHaveAttribute("min", "0");
    // #1483/#1484: the owner's words, 5 Nov - 0 is no minimum, 1 is the default, 2 or 3 is the advice.
    expect(screen.getByText("0 means no minimum, so even a torrent nobody is sharing can be grabbed. 1, the default, refuses only those. 2 or 3 is recommended.")).toBeInTheDocument();
    expect(screen.getByText("Custom intervals must be between 2 and 60 seconds. The default is 2 seconds.")).toBeInTheDocument();
  });

  it("says in what unit each timing and limit is counted", () => {
    open({ form: { protocol: "torznab" } });
    for (const unit of ["seconds", "min", "MB", "seeders"]) expect(screen.getAllByText(unit).length).toBeGreaterThan(0);
    expect(screen.getAllByText("days")).toHaveLength(2);
  });
});

describe("the preferred flags", () => {
  it("toggles a known flag and clears the flags problem", async () => {
    const user = userEvent.setup();
    const { seen, clearError } = open({ form: { protocol: "torznab" } });
    const freeleech = screen.getByRole("button", { name: "Freeleech" });
    expect(freeleech).toHaveAttribute("aria-pressed", "false");
    await user.click(freeleech);
    expect(seen.form.preferIndexerFlags).toBe("Freeleech");
    expect(clearError).toHaveBeenCalledWith("preferIndexerFlags");
    expect(screen.getByRole("button", { name: "Freeleech" })).toHaveAttribute("aria-pressed", "true");
    await user.click(screen.getByRole("button", { name: "Freeleech" }));
    expect(seen.form.preferIndexerFlags).toBe("");
  });

  it("takes a flag typed by hand, beside the known ones", async () => {
    const user = userEvent.setup();
    const { seen, clearError } = open({ form: { protocol: "torznab" } });
    await user.type(screen.getByPlaceholderText("freeleech, double-upload"), "x");
    expect(seen.form.preferIndexerFlags).toBe("x");
    expect(clearError).toHaveBeenCalledWith("preferIndexerFlags");
  });
});

describe("what the indexer is used for and how soon it is tried", () => {
  it("starts the categories again for the new scope", async () => {
    const user = userEvent.setup();
    const { seen } = open({ form: { protocol: "newznab", scope: "both", categories: "9999" } });
    await user.click(screen.getByRole("radio", { name: "TV" }));
    expect(seen.form).toMatchObject({ scope: "tv", categories: "5000,5010,5020,5030,5040,5045,5050,5060,5070" });
    await user.click(screen.getByRole("radio", { name: "Movies" }));
    expect(seen.form).toMatchObject({ scope: "movies", categories: "2000,2010,2020,2030,2040,2045,2050,2060,2070" });
  });

  it("can be put back to both", async () => {
    const user = userEvent.setup();
    const { seen } = open({ form: { protocol: "newznab", scope: "tv", categories: "5000" } });
    await user.click(screen.getByRole("radio", { name: "Both" }));
    expect(seen.form).toMatchObject({ scope: "both", categories: "2000,2010,2020,2030,2040,2045,2050,2060,5000,5010,5020,5030,5040,5045,5050" });
  });

  it("keeps the categories typed for a kind that has no standard ones", async () => {
    const user = userEvent.setup();
    const { seen } = open({ form: { protocol: "rss", scope: "both", categories: "1234" } });
    await user.click(screen.getByRole("radio", { name: "Movies" }));
    expect(seen.form).toMatchObject({ scope: "movies", categories: "1234" });
  });

  it("shows which scope is chosen", () => {
    open({ form: { scope: "tv" } });
    expect(screen.getByRole("radio", { name: "TV" })).toBeChecked();
    expect(screen.getByRole("radio", { name: "Movies" })).not.toBeChecked();
    expect(screen.getByRole("radio", { name: "Both" })).not.toBeChecked();
  });

  it("offers the priorities by name and says lower is tried first", async () => {
    const user = userEvent.setup();
    const { seen } = open();
    expect(screen.getByText("Lower numbers are tried first.")).toBeInTheDocument();
    const priority = screen.getByRole("combobox", { name: /Priority/ });
    expect(within(priority).getAllByRole("option").map((option) => option.textContent)).toEqual([
      "Highest (1)", "High (5)", "Normal (10)", "Low (25)", "Fallback only (50)", "Custom priority"
    ]);
    await user.selectOptions(priority, "5");
    expect(seen.form.priority).toBe("5");
  });

  it("takes a priority of your own", async () => {
    const user = userEvent.setup();
    const { seen } = open();
    await user.selectOptions(screen.getByRole("combobox", { name: /Priority/ }), "Custom priority");
    expect(seen.form.priority).toBe("");
    const custom = screen.getByLabelText("Custom priority");
    expect(custom).toHaveAttribute("placeholder", "1–50");
    expect(custom).toHaveAttribute("type", "number");
    await user.type(custom, "7");
    expect(seen.form.priority).toBe("7");
  });
});

describe("a new indexer's first switches", () => {
  it.each([
    ["movies", "movie"],
    ["tv", "TV"],
    ["both", "movie and TV"]
  ] as const)("offers to search it in your %s libraries, and says which", (scope, words) => {
    open({ form: { scope } });
    expect(screen.getByText(`Switches this indexer on in Library Routing for your ${words} libraries, so they search it straight away. Turn off to choose in Library Routing yourself.`)).toBeInTheDocument();
  });

  it("starts ticked, and reports a change", async () => {
    const user = userEvent.setup();
    const { onRouteOnAddChange } = open();
    const toggle = screen.getByRole("switch", { name: /Search it in my libraries/ });
    expect(toggle).toBeChecked();
    await user.click(toggle);
    expect(onRouteOnAddChange).toHaveBeenCalledWith(false);
  });

  it("shows it unticked when told so", () => {
    open({ routeOnAdd: false });
    expect(screen.getByRole("switch", { name: /Search it in my libraries/ })).not.toBeChecked();
  });

  it("can be flipped even when the screen does not listen", async () => {
    const user = userEvent.setup();
    open({ noRouteHandler: true });
    await user.click(screen.getByRole("switch", { name: /Search it in my libraries/ }));
  });

  it("is not offered for a saved indexer", () => {
    open({ editing: saved() });
    expect(screen.queryByRole("switch", { name: /Search it in my libraries/ })).not.toBeInTheDocument();
  });
});

describe("tags, switches and the proxy", () => {
  it("takes a tag off and changes only the tags", async () => {
    const user = userEvent.setup();
    const { seen } = open({ form: { tags: "kids", priority: "25" } });
    expect(screen.getByText(/Leave blank to search this indexer for any title\./)).toBeInTheDocument();
    await user.click(await screen.findByRole("button", { name: "Remove kids from Indexer tags" }));
    expect(seen.form).toMatchObject({ tags: "", priority: "25" });
  });

  it("switches the indexer on and off", async () => {
    const user = userEvent.setup();
    const { seen } = open({ form: { isEnabled: true } });
    const toggle = screen.getByRole("switch", { name: /Enabled/ });
    expect(toggle).toBeChecked();
    expect(screen.getByText("Included in automatic and interactive searches.")).toBeInTheDocument();
    await user.click(toggle);
    expect(seen.form.isEnabled).toBe(false);
  });

  it.each([
    ["RSS feed", "rssEnabled", "Include this indexer's RSS results when RSS is the source."],
    ["Automatic search", "automaticSearchEnabled", "Allow scheduled library searches to query this indexer."],
    ["Interactive search", "interactiveSearchEnabled", "Allow searches started from a title or episode page to query this indexer."]
  ] as const)("switches %s", async (label, key, description) => {
    const user = userEvent.setup();
    const { seen } = open({ form: { protocol: "torznab", isEnabled: true } });
    expect(screen.getByText(description)).toBeInTheDocument();
    const toggle = screen.getByRole("switch", { name: new RegExp(label) });
    expect(toggle).toBeChecked();
    await user.click(toggle);
    expect(seen.form[key]).toBe(false);
    expect(seen.form.isEnabled).toBe(true);
  });

  it("switches Prefer magnet links for a torrent kind only", async () => {
    const user = userEvent.setup();
    const { seen } = open({ form: { protocol: "torznab" } });
    const toggle = screen.getByRole("switch", { name: /Prefer magnet links/ });
    expect(toggle).not.toBeChecked();
    await user.click(toggle);
    expect(seen.form.preferMagnetUrl).toBe(true);
  });

  it("leaves out what only a torrent has when the indexer is Usenet", () => {
    open({ form: { protocol: "newznab" } });
    expect(screen.queryByRole("switch", { name: /Prefer magnet links/ })).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Minimum seeders")).not.toBeInTheDocument();
  });

  it("offers a direct connection and each proxy, and remembers the choice", async () => {
    const user = userEvent.setup();
    const { seen } = open({ form: { proxyId: "" } });
    const proxy = screen.getByLabelText("Proxy");
    expect(within(proxy).getAllByRole("option").map((option) => [option.getAttribute("value"), option.textContent])).toEqual([
      ["", "Direct connection (no proxy)"],
      ["px1", "Home proxy"],
      ["px2", "Office proxy"]
    ]);
    await user.selectOptions(proxy, "px2");
    expect(seen.form.proxyId).toBe("px2");
  });
});

describe("the sharing question", () => {
  it("starts on the normal rule and says what that is", () => {
    open();
    expect(screen.getByRole("radio", { name: "No, my normal rule" })).toBeChecked();
    expect(screen.getByText(describeGlobalSharingRule(emptyPlatformSettingsSnapshot))).toBeInTheDocument();
    expect(screen.queryByLabelText("Seed time")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Season-pack seed time")).not.toBeInTheDocument();
  });

  it("says what strict means, and offers a season-pack seed time", async () => {
    const user = userEvent.setup();
    const { seen } = open();
    await user.click(screen.getByRole("radio", { name: "Yes, it is strict" }));
    expect(seen.form).toMatchObject({ sharingRule: "strict", sharingSeedHours: "", sharingSeedRatio: "" });
    expect(screen.getByText(describeStrictSharingRule())).toBeInTheDocument();
    expect(screen.queryByLabelText("Seed time")).not.toBeInTheDocument();
    expect(screen.getByText(describeSeasonPackSeedTime(null, STRICT_SHARING.forHours))).toBeInTheDocument();
    expect(screen.getByLabelText("Season-pack seed time")).toHaveAttribute("placeholder", "Same as normal");
  });

  it("starts the custom numbers from the strict ones", async () => {
    const user = userEvent.setup();
    const { seen } = open();
    await user.click(screen.getByRole("radio", { name: "Set my own numbers" }));
    expect(seen.form).toMatchObject({ sharingRule: "custom", sharingSeedHours: String(STRICT_SHARING.forHours), sharingSeedRatio: String(STRICT_SHARING.untilRatio) });
    expect(screen.getByLabelText("Seed time")).toHaveValue(STRICT_SHARING.forHours);
    expect(screen.getByLabelText("Seed ratio")).toHaveValue(STRICT_SHARING.untilRatio);
  });

  it("keeps numbers already typed when switching to custom", async () => {
    const user = userEvent.setup();
    const { seen } = open({ form: { sharingRule: "strict", sharingSeedHours: "48", sharingSeedRatio: "2" } });
    await user.click(screen.getByRole("radio", { name: "Set my own numbers" }));
    expect(seen.form).toMatchObject({ sharingSeedHours: "48", sharingSeedRatio: "2" });
  });

  it("fills only the number that is missing", async () => {
    const user = userEvent.setup();
    const { seen } = open({ form: { sharingRule: "strict", sharingSeedHours: "48", sharingSeedRatio: "" } });
    await user.click(screen.getByRole("radio", { name: "Set my own numbers" }));
    expect(seen.form).toMatchObject({ sharingSeedHours: "48", sharingSeedRatio: String(STRICT_SHARING.untilRatio) });
  });

  it("leaves the numbers alone when switching away from custom", async () => {
    const user = userEvent.setup();
    const { seen } = open({ form: { sharingRule: "custom", sharingSeedHours: "", sharingSeedRatio: "" } });
    await user.click(screen.getByRole("radio", { name: "No, my normal rule" }));
    expect(seen.form).toMatchObject({ sharingRule: "inherit", sharingSeedHours: "", sharingSeedRatio: "" });
  });

  it("types the custom numbers into their own boxes", async () => {
    const user = userEvent.setup();
    const { seen } = open({ form: { sharingRule: "custom", sharingSeedHours: "", sharingSeedRatio: "" } });
    await user.type(screen.getByLabelText("Seed time"), "5");
    await user.type(screen.getByLabelText("Seed ratio"), "2");
    await user.type(screen.getByLabelText("Season-pack seed time"), "9");
    expect(seen.form).toMatchObject({ sharingSeedHours: "5", sharingSeedRatio: "2", seasonPackSeedForHours: "9" });
    expect(screen.getByLabelText("Seed time")).toHaveAttribute("min", "0");
    expect(screen.getByLabelText("Seed time")).toHaveAttribute("step", "1");
    expect(screen.getByLabelText("Seed ratio")).toHaveAttribute("step", "0.05");
    expect(screen.getByLabelText("Seed ratio")).toHaveAttribute("placeholder", "e.g. 1.0");
    expect(screen.getByLabelText("Seed time")).toHaveAttribute("placeholder", "e.g. 336");
  });

  it.each([
    ["48", "1.5", 48, 1.5],
    ["", "1.5", null, 1.5],
    ["48", "", 48, null],
    ["", "", null, null],
    ["  ", "  ", null, null]
  ] as const)("says the custom rule back from hours %j and ratio %j", (hours, ratio, expectedHours, expectedRatio) => {
    open({ form: { sharingRule: "custom", sharingSeedHours: hours, sharingSeedRatio: ratio } });
    expect(screen.getByText(describeCustomSharingRule(expectedHours, expectedRatio))).toBeInTheDocument();
  });

  it.each([
    ["custom", "24", "72", 72, 24],
    ["custom", "", "", null, null],
    ["custom", "  ", "  ", null, null],
    ["custom", "24", "", null, 24],
    ["strict", "24", "72", 72, STRICT_SHARING.forHours],
    ["strict", "", "  ", null, STRICT_SHARING.forHours]
  ] as const)("says the season-pack time back for a %s rule with hours %j and packs %j", (rule, hours, packs, expectedPacks, expectedHours) => {
    open({ form: { sharingRule: rule, sharingSeedHours: hours, seasonPackSeedForHours: packs } });
    expect(screen.getByText(describeSeasonPackSeedTime(expectedPacks, expectedHours))).toBeInTheDocument();
  });
});

describe("the categories", () => {
  it("are folded away, and described in words when they are the standard ones", () => {
    open({ form: { protocol: "newznab", scope: "both" } });
    expect(screen.getByRole("button", { name: /Categories/ })).toHaveAttribute("aria-expanded", "false");
    expect(screen.getByText("The standard Movies and TV categories for Newznab.")).toBeInTheDocument();
    expect(screen.queryByLabelText("Category ids")).not.toBeInTheDocument();
  });

  it.each([
    ["movies", "Movies"],
    ["tv", "TV"],
    ["both", "Movies and TV"]
  ] as const)("name the %s scope in words", (scope, words) => {
    open({ form: { protocol: "torznab", scope, categories: "" } });
    // An empty list is not the standard one for these kinds, so the summary says it is custom.
    expect(screen.getByText("Custom: 0 categories chosen for this indexer.")).toBeInTheDocument();
    expect(screen.queryByText(new RegExp(`The standard ${words}`))).not.toBeInTheDocument();
  });

  it("count the ids, ignoring blanks and spaces, when they are not the standard ones", () => {
    open({ form: { protocol: "torznab", categories: "2000, ,5000," } });
    expect(screen.getByText("Custom: 2 categories chosen for this indexer.")).toBeInTheDocument();
  });

  it("count one in the singular", () => {
    open({ form: { protocol: "torznab", categories: "2000" } });
    expect(screen.getByText("Custom: 1 category chosen for this indexer.")).toBeInTheDocument();
  });

  it("are the standard ones whatever the order or spacing", () => {
    open({ form: { protocol: "newznab", scope: "movies", categories: " 2070, 2060,2050,2045,2040,2030,2020,2010,2000 " } });
    expect(screen.getByText("The standard Movies categories for Newznab.")).toBeInTheDocument();
  });

  it("are the standard ones with blanks in the list", () => {
    open({ form: { protocol: "newznab", scope: "movies", categories: "2000,,2010, ,2020,2030,2040,2045,2050,2060,2070," } });
    expect(screen.getByText("The standard Movies categories for Newznab.")).toBeInTheDocument();
  });

  it("are not the standard ones when two ids have run together", () => {
    open({ form: { protocol: "newznab", scope: "movies", categories: "20002010,2020,2030,2040,2045,2050,2060,2070" } });
    expect(screen.getByText("Custom: 8 categories chosen for this indexer.")).toBeInTheDocument();
  });

  it("are not the standard ones when only the digits are the same", () => {
    open({ form: { protocol: "newznab", scope: "movies", categories: "2000,2010,2020,2030,2040,2045,2050,2060,2070".split("").reverse().join("") } });
    expect(screen.getByText(/^Custom: /)).toBeInTheDocument();
  });

  it("are the standard ones for a kind with none when none are set", () => {
    open({ form: { protocol: "rss", scope: "tv", categories: "" } });
    expect(screen.getByText("The standard TV categories for RSS feed.")).toBeInTheDocument();
  });

  it("open and close from their heading", async () => {
    const user = userEvent.setup();
    open({ form: { protocol: "newznab" } });
    const heading = screen.getByRole("button", { name: /Categories/ });
    await user.click(heading);
    expect(heading).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByLabelText("Category ids")).toBeInTheDocument();
    await user.click(heading);
    expect(heading).toHaveAttribute("aria-expanded", "false");
  });

  it("can be ticked in the picker, and typed as ids", async () => {
    const user = userEvent.setup();
    const { seen } = open({ form: { protocol: "newznab", scope: "movies", categories: "2000" } });
    await user.click(screen.getByRole("button", { name: /Categories/ }));
    await user.click(screen.getByRole("checkbox", { name: /2000/ }));
    expect(seen.form.categories).toBe("");
    await user.type(screen.getByLabelText("Category ids"), "2010");
    expect(seen.form.categories).toBe("2010");
    expect(screen.getByLabelText("Category ids")).toHaveAttribute("placeholder", "2000,2040,5000,5040");
    expect(screen.getByLabelText("Category ids")).toHaveClass("font-mono");
  });

  it("offer the standard ones back when they have been changed, and not otherwise", async () => {
    const user = userEvent.setup();
    const { seen } = open({ form: { protocol: "newznab", scope: "tv", categories: "9999" } });
    await user.click(screen.getByRole("button", { name: /Categories/ }));
    await user.click(screen.getByRole("button", { name: "Use the standard TV categories" }));
    expect(seen.form.categories).toBe("5000,5010,5020,5030,5040,5045,5050,5060,5070");
    expect(screen.queryByRole("button", { name: /Use the standard/ })).not.toBeInTheDocument();
  });

  it("name Movies and TV on the button for both", async () => {
    const user = userEvent.setup();
    open({ form: { protocol: "newznab", scope: "both", categories: "9999" } });
    await user.click(screen.getByRole("button", { name: /Categories/ }));
    expect(screen.getByRole("button", { name: "Use the standard Movies and TV categories" })).toBeInTheDocument();
  });

  it("name Movies on the button for movies", async () => {
    const user = userEvent.setup();
    open({ form: { protocol: "newznab", scope: "movies", categories: "9999" } });
    await user.click(screen.getByRole("button", { name: /Categories/ }));
    expect(screen.getByRole("button", { name: "Use the standard Movies categories" })).toBeInTheDocument();
  });

  it("offer anime categories and the TV search scope for TV and both, not for movies", async () => {
    const user = userEvent.setup();
    const { seen } = open({ form: { protocol: "newznab", scope: "tv", categories: "5000" } });
    await user.click(screen.getByRole("button", { name: /Categories/ }));
    await user.type(screen.getByLabelText("Anime categories"), "5070");
    expect(seen.form.animeCategories).toBe("5070");
    expect(screen.getByLabelText("Anime categories")).toHaveAttribute("placeholder", "5070");
    expect(screen.getAllByRole("radio", { name: "Both" })).toHaveLength(2);
    expect(screen.getByRole("radio", { name: "Season packs only" })).toBeInTheDocument();
    await user.click(screen.getByRole("radio", { name: "Episodes only" }));
    expect(seen.form.seasonPackSearchMode).toBe("episodes-only");
    await user.click(screen.getByRole("radio", { name: "Season packs only" }));
    expect(seen.form.seasonPackSearchMode).toBe("season-packs-only");
    await user.click(screen.getAllByRole("radio", { name: "Both" })[1]);
    expect(seen.form.seasonPackSearchMode).toBe("both");
  });

  it("offer them for both too", async () => {
    const user = userEvent.setup();
    open({ form: { protocol: "newznab", scope: "both" } });
    await user.click(screen.getByRole("button", { name: /Categories/ }));
    expect(screen.getByLabelText("Anime categories")).toBeInTheDocument();
    expect(screen.getByText("TV search scope")).toBeInTheDocument();
  });

  it("leave them out for movies", async () => {
    const user = userEvent.setup();
    open({ form: { protocol: "newznab", scope: "movies" } });
    await user.click(screen.getByRole("button", { name: /Categories/ }));
    expect(screen.queryByLabelText("Anime categories")).not.toBeInTheDocument();
    expect(screen.queryByText("TV search scope")).not.toBeInTheDocument();
  });
});

describe("the fine-tune section", () => {
  it("is folded away until opened, and its summary says what the default is", () => {
    open({ fineTuneOpen: false });
    expect(screen.getByRole("button", { name: /Fine-tune/ })).toHaveAttribute("aria-expanded", "false");
    expect(screen.getByText("Use Deluno's safe 2-second default, or follow this indexer's published limit.")).toBeInTheDocument();
    expect(screen.queryByLabelText("Request interval")).not.toBeInTheDocument();
  });

  it("opens and closes from its heading", async () => {
    const user = userEvent.setup();
    const { seen } = open({ fineTuneOpen: false, form: { protocol: "torznab" } });
    await user.click(screen.getByRole("button", { name: /Fine-tune/ }));
    expect(seen.fineTuneOpen).toBe(true);
    expect(screen.getByLabelText("Request interval")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /Fine-tune/ }));
    expect(seen.fineTuneOpen).toBe(false);
  });

  it("explains the acquisition rules, and its timing section opens from its own heading", async () => {
    const user = userEvent.setup();
    const { seen } = open({ fineTuneOpen: false });
    expect(screen.getByText("These rules decide which feed results Deluno can use. An empty limit keeps the indexer unrestricted.")).toBeInTheDocument();
    expect(screen.getByText("Applied before a release can be sent to a download client.")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /Timing, limits, and flags/ }));
    expect(seen.timingOpen).toBe(true);
    expect(screen.getByText("The timing rules hold or reject a candidate; they do not alter the indexer's results.")).toBeInTheDocument();
  });

  it("opens Fine-tune and Timing, limits, and flags each on its own (#1466)", async () => {
    const user = userEvent.setup();
    const { seen } = open({ fineTuneOpen: false, timingOpen: false, form: { protocol: "torznab" } });
    await user.click(screen.getByRole("button", { name: /Fine-tune/ }));
    expect(screen.getByRole("button", { name: /Fine-tune/ })).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByRole("button", { name: /Timing, limits, and flags/ })).toHaveAttribute("aria-expanded", "false");
    await user.click(screen.getByRole("button", { name: /Timing, limits, and flags/ }));
    await user.click(screen.getByRole("button", { name: /Fine-tune/ }));
    expect(seen.fineTuneOpen).toBe(false);
    expect(seen.timingOpen).toBe(true);
  });

  it("shows the limits unit only once a limit is set", async () => {
    const user = userEvent.setup();
    const { seen } = open({ form: { protocol: "torznab" } });
    expect(screen.queryByText("Limits unit")).not.toBeInTheDocument();
    await user.type(screen.getByLabelText("Query limit"), "5");
    expect(screen.getByText("Limits unit")).toBeInTheDocument();
    expect(screen.getByRole("radio", { name: "Per day" })).toBeChecked();
    await user.click(screen.getByRole("radio", { name: "Per hour" }));
    expect(seen.form.limitsUnit).toBe("hour");
  });

  it("shows the limits unit for a grab limit alone, and not for spaces", () => {
    open({ form: { protocol: "torznab", grabLimit: "3" } });
    expect(screen.getByText("Limits unit")).toBeInTheDocument();
  });

  it("does not count a limit of only spaces", () => {
    open({ form: { protocol: "torznab", queryLimit: "  ", grabLimit: " " } });
    expect(screen.queryByText("Limits unit")).not.toBeInTheDocument();
  });

  it("offers extra parameters to Newznab as well as Torznab", () => {
    open({ form: { protocol: "newznab" } });
    expect(screen.getByLabelText("Additional parameters")).toHaveAttribute("placeholder", "token=abc123");
  });

  it("leaves out the extra parameters for a kind that has none", () => {
    open({ form: { protocol: "rss" } });
    expect(screen.queryByLabelText("Additional parameters")).not.toBeInTheDocument();
    expect(screen.getByLabelText("What MULTI means here")).toBeInTheDocument();
  });
});

describe("an indexer that is already saved", () => {
  const ago = (ms: number) => new Date(Date.now() - ms).toISOString();

  it("says nothing of health for a new one", () => {
    open();
    expect(screen.queryByText("Health")).not.toBeInTheDocument();
    expect(screen.queryByText("Remove this indexer")).not.toBeInTheDocument();
  });

  it("says how the last test went, when it was, and how long it took", () => {
    open({ form: { protocol: "torznab" }, editing: saved({ lastHealthTestUtc: ago(125 * 60 * 1000), lastHealthLatencyMs: 120 }) });
    expect(screen.getByText("Health")).toBeInTheDocument();
    expect(fact("Last test")).toHaveTextContent("Healthy");
    expect(fact("Last test")).toHaveTextContent("2 h ago · 120 ms");
  });

  it("says a zero-millisecond answer too, and none when the time is not known", () => {
    open({ editing: saved({ lastHealthTestUtc: null, lastHealthLatencyMs: 0 }) });
    expect(fact("Last test").textContent).toBe("HealthyNever · 0 ms");
  });

  it("leaves the time out when it was not measured", () => {
    open({ editing: saved({ lastHealthTestUtc: null, lastHealthLatencyMs: null }) });
    expect(fact("Last test").textContent).toBe("HealthyNever");
  });

  it("says nothing is being held back when the pacing is quiet", () => {
    open({ editing: saved() });
    expect(fact("Pacing")).toHaveTextContent("No request is waiting. Deluno will still follow this indexer's safe request interval.");
    expect(fact("Pacing")).toHaveAttribute("aria-live", "polite");
  });

  it("says what Deluno is waiting on", () => {
    open({ editing: saved(), throttle: { host: "tracker.example.test", waiting: 2, grantedCount: 0, refusedCount: 0, totalWaitedSeconds: 0, nextPermitInSeconds: 5 } });
    expect(fact("Pacing")).toHaveTextContent("Deluno is waiting on tracker.example.test before sending 2 requests.");
  });

  it("says when the next request will go", () => {
    open({ editing: saved(), throttle: { host: "tracker.example.test", waiting: 0, grantedCount: 0, refusedCount: 0, totalWaitedSeconds: 0, nextPermitInSeconds: 3 } });
    expect(fact("Pacing")).toHaveTextContent("Deluno will send the next request to tracker.example.test in about 3 seconds.");
  });

  it("says nothing is waiting when the host has no pending request and no permit due", () => {
    open({ editing: saved(), throttle: { host: "tracker.example.test", waiting: 0, grantedCount: 0, refusedCount: 0, totalWaitedSeconds: 0, nextPermitInSeconds: 0 } });
    expect(fact("Pacing")).toHaveTextContent("No request is waiting.");
  });

  it("shows the message the last test gave", () => {
    open({ editing: saved({ lastHealthMessage: "Reached tracker.example.test" }) });
    expect(fact("Message")).toHaveTextContent("Reached tracker.example.test");
  });

  it("links a message that points to Preferences", () => {
    open({ editing: saved({ lastHealthMessage: "A helper, set in Preferences, General, gets Deluno past it." }) });
    expect(within(fact("Message")).getByRole("link", { name: "Open Preferences, General" })).toBeInTheDocument();
  });

  it("shows no message row when there is none", () => {
    open({ editing: saved({ lastHealthMessage: null }) });
    expect(screen.queryByText("Message", { selector: "dt" })).not.toBeInTheDocument();
  });

  it("says the unreadable-key sentence once, not again as a message", () => {
    open({
      editing: saved({ secretUnreadable: true, secretProblem: "Stored on another computer.", lastHealthMessage: "Stored on another computer." })
    });
    expect(screen.getByRole("alert")).toHaveTextContent("Stored on another computer.");
    expect(screen.queryByText("Message", { selector: "dt" })).not.toBeInTheDocument();
  });

  it("still shows a message that is about something else when the key is unreadable", () => {
    open({ editing: saved({ secretUnreadable: true, secretProblem: "Stored on another computer.", lastHealthMessage: "Timed out." }) });
    expect(fact("Message")).toHaveTextContent("Timed out.");
  });

  it("says what went wrong, what to do and when it will try again", () => {
    open({
      editing: saved({
        lastHealthFailure: {
          summary: "The tracker refused the key.",
          nextAction: "Paste the key again.",
          retryAfterUtc: new Date(Date.now() + 90 * 60 * 1000).toISOString()
        } as never
      })
    });
    const cause = fact("Cause");
    expect(cause).toHaveTextContent("The tracker refused the key.");
    expect(cause).toHaveTextContent("Paste the key again.");
    expect(cause).toHaveTextContent(/Next eligible attempt: 2 h ago|Next eligible attempt: 1 h ago/);
  });

  it("says no cause row when there is none, and no retry time when none is known", () => {
    open({ editing: saved({ lastHealthFailure: { summary: "Refused.", nextAction: "Try later.", retryAfterUtc: null } as never }) });
    expect(fact("Cause")).not.toHaveTextContent("Next eligible attempt");
  });

  it("shows no cause row for a healthy indexer", () => {
    open({ editing: saved() });
    expect(screen.queryByText("Cause", { selector: "dt" })).not.toBeInTheDocument();
  });

  it("does not repeat the Cloudflare advice when the message above already gives it", () => {
    open({
      editing: saved({
        lastHealthMessage: "Behind a Cloudflare check. A helper, set in Preferences, General, gets Deluno past it.",
        lastHealthFailure: { summary: "Blocked.", nextAction: "Set a Cloudflare helper in Preferences, General.", retryAfterUtc: null } as never
      })
    });
    expect(fact("Cause")).toHaveTextContent("Blocked.");
    expect(fact("Cause")).not.toHaveTextContent("Set a Cloudflare helper");
  });

  it("gives the Cloudflare advice when the message above does not", () => {
    open({
      editing: saved({
        lastHealthMessage: "Timed out.",
        lastHealthFailure: { summary: "Blocked.", nextAction: "Set a Cloudflare helper in Preferences, General.", retryAfterUtc: null } as never
      })
    });
    expect(fact("Cause")).toHaveTextContent("Set a Cloudflare helper");
  });

  it("gives the advice when only the message mentions Cloudflare", () => {
    open({
      editing: saved({
        lastHealthMessage: "Behind a Cloudflare check.",
        lastHealthFailure: { summary: "Blocked.", nextAction: "Check the address.", retryAfterUtc: null } as never
      })
    });
    expect(fact("Cause")).toHaveTextContent("Check the address.");
  });

  it("counts the failures in a row, with the reason Deluno stopped using it, and offers a reset", async () => {
    const user = userEvent.setup();
    const { onReset } = open({ editing: saved({ consecutiveFailures: 3, disabledReason: "Too many failures" }) });
    expect(fact("Failures")).toHaveTextContent("3 in a row — Too many failures");
    await user.click(screen.getByRole("button", { name: "Reset failures" }));
    expect(onReset).toHaveBeenCalledTimes(1);
  });

  it("counts the failures without a reason when there is none", () => {
    open({ editing: saved({ consecutiveFailures: 1, disabledReason: null }) });
    expect(fact("Failures").textContent).toBe("1 in a row");
  });

  it("offers no reset and no failure row when it has not failed", () => {
    open({ editing: saved({ consecutiveFailures: 0 }) });
    expect(screen.queryByRole("button", { name: "Reset failures" })).not.toBeInTheDocument();
    expect(screen.queryByText("Failures", { selector: "dt" })).not.toBeInTheDocument();
  });

  it("holds the reset while something else is under way, and shows it working when it is the reset", () => {
    open({ editing: saved({ consecutiveFailures: 2 }), busy: "save" });
    expect(screen.getByRole("button", { name: "Reset failures" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Reset failures" }).querySelector(".animate-spin")).toBeNull();
  });

  it("shows the reset working while it is the reset", () => {
    open({ editing: saved({ id: "ix1", consecutiveFailures: 2 }), busy: "reset:ix1" });
    expect(screen.getByRole("button", { name: "Reset failures" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Reset failures" }).querySelector(".animate-spin")).not.toBeNull();
  });

  it("does not show the reset working for another indexer's reset", () => {
    open({ editing: saved({ id: "ix1", consecutiveFailures: 2 }), busy: "reset:other" });
    expect(screen.getByRole("button", { name: "Reset failures" }).querySelector(".animate-spin")).toBeNull();
  });

  it("says when a search or grab limit has been reached", () => {
    const status = { allowed: false, limit: 100, count: 100, resetsAtUtc: "2026-10-08T10:00:00Z", unit: "day" };
    const grab = { allowed: false, limit: 5, count: 5, resetsAtUtc: "2026-10-08T11:00:00Z", unit: "hour" };
    open({ editing: saved({ queryLimitStatus: status as never, grabLimitStatus: grab as never }) });
    expect(fact("Search limit")).toHaveTextContent(describeCallLimitStatus("Search", status)!);
    expect(fact("Grab limit")).toHaveTextContent(describeCallLimitStatus("Grab", grab)!);
  });

  it("says nothing of limits that have not been reached", () => {
    open({ editing: saved({ queryLimitStatus: { allowed: true, limit: 100, count: 3, resetsAtUtc: null, unit: "day" } as never }) });
    expect(screen.queryByText("Search limit", { selector: "dt" })).not.toBeInTheDocument();
    expect(screen.queryByText("Grab limit", { selector: "dt" })).not.toBeInTheDocument();
  });

  it("can be removed, and says what happens to the libraries routed to it", async () => {
    const user = userEvent.setup();
    const { onRemove } = open({ editing: saved() });
    expect(screen.getByText("Remove this indexer")).toBeInTheDocument();
    expect(screen.getByText("Libraries routed only to it will need another indexer.")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Remove…" }));
    expect(onRemove).toHaveBeenCalledTimes(1);
  });

  it("cannot be removed while something else is happening, and says what it is waiting for", () => {
    open({ editing: saved(), busy: "test:form" });
    expect(screen.getByRole("button", { name: "Remove…" })).toBeDisabled();
    expect(screen.getByText("Waiting for the connection test to finish")).toBeInTheDocument();
  });
});

describe("the VIP warning", () => {
  it("says when the subscription has run out, and lets the person acknowledge it", async () => {
    const user = userEvent.setup();
    const { onAcknowledgeVip } = open({ editing: saved({ vipExpiresUtc: new Date(Date.now() - 3 * DAY).toISOString() }) });
    const warning = screen.getByRole("button", { name: "Acknowledge" }).parentElement!;
    expect(within(warning).getByText("VIP expired")).toBeInTheDocument();
    expect(screen.queryByText("VIP expiring soon")).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Acknowledge" }));
    expect(onAcknowledgeVip).toHaveBeenCalledTimes(1);
  });

  it("shows an expired subscription in red", () => {
    open({ editing: saved({ vipExpiresUtc: new Date(Date.now() - 3 * DAY).toISOString() }) });
    expect(within(screen.getByRole("button", { name: "Acknowledge" }).parentElement!).getByText("VIP expired")).toHaveClass("text-destructive");
  });

  it("shows one about to run out in amber", () => {
    open({ editing: saved({ vipExpiresUtc: new Date(Date.now() + 3 * DAY).toISOString() }) });
    expect(within(screen.getByRole("button", { name: "Acknowledge" }).parentElement!).getByText("VIP expiring soon")).toHaveClass("text-warning");
  });

  it("counts a subscription that runs out this very moment as expired", () => {
    vi.useFakeTimers();
    try {
      const now = new Date("2026-10-08T12:00:00Z");
      vi.setSystemTime(now);
      open({ editing: saved({ vipExpiresUtc: now.toISOString() }) });
      const warning = screen.getByRole("button", { name: "Acknowledge" }).parentElement!;
      expect(within(warning).getByText("VIP expired")).toHaveClass("text-destructive");
    } finally {
      vi.useRealTimers();
    }
  });

  it("says when it is about to", () => {
    open({ editing: saved({ vipExpiresUtc: new Date(Date.now() + 3 * DAY).toISOString() }) });
    const warning = screen.getByRole("button", { name: "Acknowledge" }).parentElement!;
    expect(within(warning).getByText("VIP expiring soon")).toBeInTheDocument();
    expect(screen.queryByText("VIP expired")).not.toBeInTheDocument();
  });

  it("says nothing while the expiry is far off", () => {
    open({ editing: saved({ vipExpiresUtc: new Date(Date.now() + 60 * DAY).toISOString() }) });
    expect(screen.queryByText(/VIP expir/)).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Acknowledge" })).not.toBeInTheDocument();
  });

  it("says nothing once the person has acknowledged this expiry", () => {
    const expiry = new Date(Date.now() - 3 * DAY).toISOString();
    open({ editing: saved({ vipExpiresUtc: expiry, vipWarningAcknowledgedExpiryUtc: expiry }) });
    expect(screen.queryByText(/VIP expir/)).not.toBeInTheDocument();
  });

  it("holds Acknowledge while something else is under way, and shows it working when it is that", () => {
    open({ editing: saved({ id: "ix1", vipExpiresUtc: new Date(Date.now() - DAY).toISOString() }), busy: "acknowledge-vip:ix1" });
    const button = screen.getByRole("button", { name: "Acknowledge" });
    expect(button).toBeDisabled();
    expect(button.querySelector(".animate-spin")).not.toBeNull();
  });

  it("shows no spinner for another indexer's acknowledgement", () => {
    open({ editing: saved({ id: "ix1", vipExpiresUtc: new Date(Date.now() - DAY).toISOString() }), busy: "acknowledge-vip:other" });
    expect(screen.getByRole("button", { name: "Acknowledge" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Acknowledge" }).querySelector(".animate-spin")).toBeNull();
  });

  it("is ready to use when nothing else is happening", () => {
    open({ editing: saved({ vipExpiresUtc: new Date(Date.now() - DAY).toISOString() }) });
    expect(screen.getByRole("button", { name: "Acknowledge" })).toBeEnabled();
  });
});
