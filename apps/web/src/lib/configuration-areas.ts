/**
 * The eight configuration areas, and the explainer each one opens with.
 *
 * Both halves live here because they answer the same question. The matcher
 * decides which tabs the sidebar and toolbar show for a path; the explainer
 * says how those tabs fit together. Kept apart, they drifted — Find & Download
 * ended up with no explainer of its own and a routing one on a sub-tab, and
 * five of the seven only appeared on whichever tab you happened to land on.
 *
 * Rules the copy keeps, so eight of these do not read as eight different apps:
 *
 * - **Never restate the page title.** The toolbar already said where you are.
 * - **Order, not inventory.** Steps are a sequence — first this, then that —
 *   never a list of the tabs above, which would say the same thing twice.
 * - **Two to four steps, or none.** More than four is a manual. An area whose
 *   parts have no order gets a lead and stops there.
 * - **Account for every tab.** The panel sits above all of them, so it may not
 *   read as though one of them does not exist. A tab outside the sequence
 *   belongs in the lead, not in a step of its own.
 * - **Name the thing the user will see**, in the words the UI uses for it.
 */

export interface ConfigurationAreaExplainer {
  lead: string;
  steps: readonly { title: string; body: string }[];
}

export interface ConfigurationArea {
  id: string;
  match: (path: string) => boolean;
  explainer: ConfigurationAreaExplainer;
}

const MEDIA_MANAGEMENT_PATHS = [
  "/settings/libraries",
  "/settings/media-management",
  "/settings/import-policy",
  "/settings/processing",
  "/settings/destination-rules",
  "/settings/metadata",
  "/settings/tags"
];

const PREFERENCES_PATHS = ["/settings/general", "/settings/ui", "/settings/notifications", "/settings/migration"];

export const CONFIGURATION_AREAS: readonly ConfigurationArea[] = [
  {
    id: "media-management",
    match: (path) => path === "/settings" || MEDIA_MANAGEMENT_PATHS.some((item) => path.startsWith(item)),
    explainer: {
      lead: "A library is the thing everything else here hangs off. It says what kind of media it holds, which folder the finished files live in, and what happens to a download on its way there. The tabs above follow that journey in the order a file travels it, bar one: the artwork and extra files Deluno keeps beside each title, which it collects once the title is home.",
      steps: [
        { title: "Make a library and give it a folder", body: "Movies, TV shows, or one of your own. Imported files end up in the folder you name here, so pick one Deluno can write to." },
        { title: "Say what happens when a download finishes", body: "Import it straight away, or hold it until an external processor has produced a cleaned copy — and choose whether the original is kept afterwards." },
        { title: "Say how the file should be named and where it lands", body: "Naming rules turn a release name into a tidy filename. A final destination can send particular titles to a different folder from the rest of the library." },
        { title: "Label the titles you want treated differently", body: "A label you make once and reuse, in Library Routing or a final destination. It only works when both sides carry it: the rule asks for the tag, and the title has it." }
      ]
    }
  },
  {
    id: "quality-and-release",
    match: (path) =>
      path.startsWith("/settings/policy-sets") ||
      path.startsWith("/settings/profiles") ||
      path.startsWith("/settings/quality") ||
      path.startsWith("/settings/custom-formats"),
    explainer: {
      lead: "Deluno scores every release it finds and takes the best one that passes. Four things shape that score, and they stack: the qualities you accept, how big a file of that quality should be, the words you want or refuse in a release name, and — last — the Library Profile that points one of your libraries at a particular set of those choices.",
      steps: [
        { title: "Say which qualities you accept", body: "A Quality Profile lists the qualities you will take, best first, and the one good enough to stop upgrading at." },
        { title: "Add the details that decide close calls", body: "Size Rules rule out files too small or too large to be what they claim. Release Preferences add or subtract points for things like a preferred group or an unwanted codec." },
        { title: "Point a library at it", body: "A Library Profile attaches those choices to Movies, TV Shows, or any library you have made — so different libraries can want different things." }
      ]
    }
  },
  {
    id: "find-and-download",
    match: (path) => path.startsWith("/indexers"),
    explainer: {
      lead: "Deluno needs two things before it can fetch anything: somewhere to search, and something to do the downloading. Neither belongs to a library on its own — the library is what says which of them to use.",
      steps: [
        { title: "Add somewhere to search", body: "An indexer is a search source. Deluno asks every one a library is linked to, then compares what comes back before it picks a release." },
        { title: "Add something to download with", body: "qBittorrent, SABnzbd or another client. Deluno hands the release over and watches it until the file is finished." },
        { title: "Tell each library which to use", body: "A library can search everywhere and download anywhere, or be pinned to its own source and client. Nothing runs for a library until it has both." }
      ]
    }
  },
  {
    id: "subtitles",
    match: (path) => path.startsWith("/subtitles"),
    explainer: {
      lead: "Subtitles are the one thing a library asks for that Deluno cannot produce itself, so this is in two halves: which languages each shelf wants, and where they come from. Deluno reads what your files already have before it fetches anything, so a library that has been through Bazarr starts mostly green.",
      steps: [
        { title: "Say which languages each library wants", body: "Per shelf, so English on everything and Japanese on anime is one setting each rather than a compromise. A cutoff says how many of them a file needs before Deluno stops looking." },
        { title: "Add somewhere to fetch them from", body: "Two of the sources need no account at all. Each one says what it covers and what signing up buys you, and a test tells you whether it is answering before you rely on it." },
        { title: "Leave the rest to the library cycle", body: "Nothing is queued when you save. The same schedule that searches for releases reads your files, then fetches what is missing, in the same window and at the same pace." }
      ]
    }
  },
  {
    id: "automation-and-recovery",
    match: (path) => path.startsWith("/search-cycles") || path.startsWith("/settings/automation"),
    explainer: {
      lead: "Deluno can keep looking on its own — for the titles you do not have yet, and for better copies of the ones you do. This is where you say how often it looks, how much it takes on at a time, and what it should do when a download goes wrong.",
      steps: [
        { title: "Turn it on, one library at a time", body: "Each library runs to its own schedule, so a large catalogue and a small one need not search at the same pace." },
        { title: "Choose what it looks for", body: "Titles with no file at all, better copies of what you already have, or both. Each runs as its own cycle with its own limit." },
        { title: "Decide how failures end", body: "How long to wait before trying again, when to stop retrying a release, and what happens to a download that never finishes." }
      ]
    }
  },
  {
    id: "discover-media",
    match: (path) => path.startsWith("/settings/lists"),
    explainer: {
      lead: "An import list is a source of titles, not a source of files — a public list, a watchlist, a collection — that Deluno keeps your library in step with. It only decides what to look for; finding and downloading is still the rest of the app's job.",
      steps: [
        { title: "Point Deluno at a list", body: "Give it the list's address and say which library the titles belong in." },
        { title: "Preview before you commit", body: "See exactly what would be added, and approve titles one at a time if you would rather not take all of them." },
        { title: "Let it keep up", body: "Each sync adds what is new. Taking a title off the list never deletes anything already in your library." }
      ]
    }
  },
  {
    id: "preferences",
    match: (path) => PREFERENCES_PATHS.some((item) => path.startsWith(item)),
    explainer: {
      // No steps: these tabs are four separate preferences, not a sequence.
      lead: "How this installation behaves, rather than what it manages. Nothing on these tabs changes a library or a title — they change Deluno itself: what it calls itself and where it listens, how it looks, who it tells when something happens, and how to bring settings across from the app you used before.",
      steps: []
    }
  },
  {
    id: "system",
    // Deliberately not `/setup-guide`, which the sidebar counts as System so the
    // area stays lit, but which is its own screen with its own explaining to do.
    match: (path) => path.startsWith("/system"),
    explainer: {
      lead: "The state of the installation rather than its contents: whether Deluno is healthy and what it is waiting on, a record of what changed and who changed it, backups to take before you touch something, updates, the API keys anything outside Deluno needs in order to talk to it, and the guides for anything you would rather read about first.",
      steps: []
    }
  }
];

export function findConfigurationArea(pathname: string) {
  return CONFIGURATION_AREAS.find((area) => area.match(pathname));
}
