import { Moon, Sun } from "lucide-react";
import { describe, expect, it, vi } from "vitest";
import { buildActionCommands, commandToShortcut, getGlobalShortcuts, navigationCommands, settingsCommands, type CommandItem } from "./command-registry";

const row = (c: CommandItem) => [c.id, c.label, c.to, c.shortcut?.join("+") ?? null, c.keywords?.join(",") ?? null, c.group];

describe("the navigation commands", () => {
  it("are these, with their words, places, shortcuts and search words", () => {
    expect(navigationCommands.map(row)).toEqual([
  ["nav.overview","Dashboard","/","g+o","dashboard,home,ops","navigation"],
  ["nav.movies","Movies","/movies","g+m","library,movies","navigation"],
  ["nav.collections","Collections","/collections",null,"library,collections,franchises,movies","navigation"],
  ["nav.tv","TV","/tv","g+t","shows,series","navigation"],
  ["nav.indexers","Find & Download","/indexers","g+i","providers,prowlarr,sources,indexers,download clients","navigation"],
  ["nav.automation","Automation & Recovery","/search-cycles","g+x","search,retries,upgrades,wanted,scheduling,recovery","navigation"],
  ["nav.queue","In progress","/queue","g+q","downloads,imports,clients,recovery","navigation"],
  ["nav.activity","Activity","/queue?view=history","g+a","queue,history,events,jobs,downloads","navigation"],
  ["nav.calendar","Schedule","/calendar","g+c","schedule,airing","navigation"],
  ["nav.discover","Discover","/discover","g+d","discover,recommendations,tmdb,lists,add","navigation"],
  ["nav.settings","Media Management","/settings","g+s","preferences,config","navigation"],
  ["nav.system","System & Settings","/system","g+y","logs,tasks,diagnostics","navigation"]
    ]);
  });

  it("each have an icon, and no two share an id or a shortcut", () => {
    expect(navigationCommands.every((c) => c.icon)).toBe(true);
    expect(new Set(navigationCommands.map((c) => c.id)).size).toBe(navigationCommands.length);
    const shortcuts = navigationCommands.filter((c) => c.shortcut).map((c) => c.shortcut!.join("+"));
    expect(new Set(shortcuts).size).toBe(shortcuts.length);
  });
});

describe("the settings commands", () => {
  it("are these, with their words, places and search words", () => {
    expect(settingsCommands.map(row)).toEqual([
  ["settings.general","Maintain Deluno · General","/settings/general",null,"system,startup,url,branding,app,instance","navigation"],
  ["settings.ui","Maintain Deluno · Interface","/settings/ui",null,"system,appearance,theme,display,ui,interface","navigation"],
  ["settings.media-management","Media Management · Media Naming","/settings/media-management",null,"naming,import,organise,hardlinks,cleanup","navigation"],
  ["settings.import-policy","Media Management · Import Policy","/settings/import-policy",null,"import,downloads,hardlinks,cleanup,cutoff,upgrade","navigation"],
  ["settings.processing","Media Management · Processing Workflow","/settings/processing",null,"processor,fileflows,weir,handoff,completed files,import","navigation"],
  ["settings.libraries","Media Management · Library & Storage","/settings/libraries",null,"library,folders,paths,root,movies,tv","navigation"],
  ["settings.destination-rules","Media Management · Final Destinations","/settings/destination-rules",null,"library,routing,rules,root folders,genre,tag,language","navigation"],
  ["settings.metadata","Media Management · Metadata","/settings/metadata",null,"library,tmdb,language,ratings,certification,metadata","navigation"],
  ["settings.tags","Media Management · Tags","/settings/tags",null,"library,tags,labels,groups,routing","navigation"],
  ["settings.auto-tagging","Media Management · Auto-tagging","/settings/auto-tagging",null,"library,tags,rules,automatic,genre,anime,label","navigation"],
  ["settings.automation","Automation & Recovery","/search-cycles",null,"search,retries,failed downloads,upgrades,scheduling,recovery","navigation"],
  ["settings.policy-sets","Library Profiles","/settings/policy-sets",null,"quality,profile,library profile,behaviour,policy,library behaviour,library rules,defaults,custom rules,multi version","navigation"],
  ["settings.profiles","Library Profiles · Quality Profiles","/settings/profiles",null,"quality,profiles,policy,upgrades","navigation"],
  ["settings.quality","Library Profiles · Size Rules","/settings/quality",null,"quality,resolution,bitrate,size limits,sizes","navigation"],
  ["settings.custom-formats","Library Profiles · Release Preferences","/settings/custom-formats",null,"quality,scoring,release,format,rules","navigation"],
  ["settings.release-rules","Quality & Release · Acquisition Rules","/settings/release-rules",null,"quality,acquisition,delay,hold,release,terms,indexer","navigation"],
  ["settings.lists","Discover Media · Import Lists","/settings/lists",null,"automation,imdb,trakt,intake,source,auto import,lists","navigation"],
  ["settings.notifications","System & Settings · Notifications","/settings/notifications",null,"webhook,alerts,events,notifications","navigation"],
  ["settings.migration","System & Settings · Migration","/settings/migration",null,"radarr,sonarr,import,migration,configuration","navigation"],
  ["settings.account","System & Settings · Account","/settings/account",null,"recovery,codes,password,username,sign in,account,forgot","navigation"],
  ["settings.guided-setup","System & Settings · Guided Setup","/setup-guide",null,"setup,wizard,first time,configuration","navigation"]
    ]);
  });

  it("each have an icon and a unique id", () => {
    expect(settingsCommands.every((c) => c.icon)).toBe(true);
    expect(new Set(settingsCommands.map((c) => c.id)).size).toBe(settingsCommands.length);
  });
});

describe("the action commands", () => {
  it("offers nothing when nothing can be done", () => {
    expect(buildActionCommands({})).toEqual([]);
  });

  it("offers each action only when it can be done, in order, with its words and search words", () => {
    const [movie, series, indexer, refresh] = [vi.fn(), vi.fn(), vi.fn(), vi.fn()];
    const items = buildActionCommands({ onAddMovie: movie, onAddSeries: series, onAddIndexer: indexer, onRefresh: refresh });
    expect(items.map((c) => [c.id, c.label, c.group, c.keywords])).toEqual([
      ["action.add-movie", "Add movie", "actions", ["new", "import"]],
      ["action.add-series", "Add TV show", "actions", ["new", "import", "show"]],
      ["action.add-indexer", "Add indexer", "actions", ["provider", "new"]],
      ["action.refresh", "Refresh data", "actions", ["reload", "revalidate"]]
    ]);
    expect(items.every((c) => c.icon)).toBe(true);
    items.forEach((c) => c.perform!());
    expect([movie, series, indexer, refresh].map((f) => f.mock.calls.length)).toEqual([1, 1, 1, 1]);
    expect(items.map((c) => c.perform)).toEqual([movie, series, indexer, refresh]);
  });

  it("offers just the actions that were given", () => {
    expect(buildActionCommands({ onRefresh: vi.fn() }).map((c) => c.id)).toEqual(["action.refresh"]);
    expect(buildActionCommands({ onAddIndexer: vi.fn() }).map((c) => c.id)).toEqual(["action.add-indexer"]);
    expect(buildActionCommands({ onAddSeries: vi.fn() }).map((c) => c.id)).toEqual(["action.add-series"]);
    expect(buildActionCommands({ onAddMovie: vi.fn() }).map((c) => c.id)).toEqual(["action.add-movie"]);
  });

  it("offers the other colour of the page, as a preference", () => {
    const toggle = vi.fn();
    const [dark] = buildActionCommands({ onToggleTheme: toggle, theme: "dark" });
    expect(dark).toMatchObject({ id: "action.toggle-theme", label: "Switch to light mode", group: "preferences", keywords: ["dark", "light", "theme"], perform: toggle });
    const [light] = buildActionCommands({ onToggleTheme: toggle, theme: "light" });
    expect(light.label).toBe("Switch to dark mode");
    expect(buildActionCommands({ onToggleTheme: toggle })[0].label).toBe("Switch to dark mode");
    expect(dark.icon).toBe(Sun);
    expect(light.icon).toBe(Moon);
  });
});

describe("the shortcuts listed for the keyboard", () => {
  it("lists every navigation command's own key, so none can be left out (#1465)", () => {
    const listed = getGlobalShortcuts("Win32").filter((s) => s.group === "Navigation");
    const withKeys = navigationCommands.filter((command) => command.shortcut);
    expect(withKeys.length).toBeGreaterThan(0);
    expect(listed.map((s) => [s.keys, s.label])).toEqual(withKeys.map((command) => [command.shortcut, `Go to ${command.label}`]));
  });


  it("lists them by group, in order", () => {
    const shortcuts = getGlobalShortcuts("Win32");
    expect(shortcuts.map((s) => [s.keys.join("+"), s.label, s.group])).toEqual([
      [expect.any(String), "Open search & navigate", "Global"],
      ["/", "Focus search", "Global"],
      ["?", "Show keyboard shortcuts", "Global"],
      ["Esc", "Close overlay or clear selection", "Global"],
      ["g+o", "Go to Dashboard", "Navigation"],
      ["g+m", "Go to Movies", "Navigation"],
      ["g+t", "Go to TV", "Navigation"],
      ["g+i", "Go to Find & Download", "Navigation"],
      ["g+x", "Go to Automation & Recovery", "Navigation"],
      ["g+q", "Go to In progress", "Navigation"],
      ["g+a", "Go to Activity", "Navigation"],
      ["g+c", "Go to Schedule", "Navigation"],
      ["g+d", "Go to Discover", "Navigation"],
      ["g+s", "Go to Media Management", "Navigation"],
      ["g+y", "Go to System & Settings", "Navigation"],
      ["j", "Focus next row", "Table"],
      ["k", "Focus previous row", "Table"],
      ["x", "Select focused row", "Table"],
      ["m", "Toggle monitored", "Row"],
      [".", "Open row actions", "Row"]
    ]);
  });

  it("opens the palette with the key the platform uses", () => {
    expect(getGlobalShortcuts("MacIntel")[0].keys).not.toEqual(getGlobalShortcuts("Win32")[0].keys);
    expect(getGlobalShortcuts("Win32")[0].keys).toEqual(["Ctrl", "K"]);
    expect(getGlobalShortcuts("MacIntel")[0].keys).toEqual(["⌘", "K"]);
  });

  it("lists only go-to keys that a navigation command really has", () => {
    const real = new Set(navigationCommands.filter((c) => c.shortcut).map((c) => c.shortcut!.join("+")));
    for (const listed of getGlobalShortcuts("Win32").filter((s) => s.group === "Navigation")) expect(real.has(listed.keys.join("+"))).toBe(true);
  });
});

describe("a command as a listed shortcut", () => {
  it("is nothing without a shortcut", () => {
    expect(commandToShortcut({ id: "a", label: "A", group: "actions" })).toBeNull();
  });

  it("is listed under Navigation for a navigation command and Actions for anything else", () => {
    expect(commandToShortcut({ id: "a", label: "A", group: "navigation", shortcut: ["g", "a"] })).toEqual({ keys: ["g", "a"], label: "A", group: "Navigation" });
    expect(commandToShortcut({ id: "a", label: "A", group: "actions", shortcut: ["x"] })).toEqual({ keys: ["x"], label: "A", group: "Actions" });
    expect(commandToShortcut({ id: "a", label: "A", group: "preferences", shortcut: ["x"] })?.group).toBe("Actions");
  });
});
