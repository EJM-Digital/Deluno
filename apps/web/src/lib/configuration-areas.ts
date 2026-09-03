/**
 * The seven configuration areas, and the explainer each one opens with.
 *
 * Both halves live here because they answer the same question. The matcher
 * decides which tabs the sidebar and toolbar show for a path; the explainer
 * says how those tabs fit together. Kept apart, they drifted — Find & Download
 * ended up with no explainer of its own and a routing one on a sub-tab, and
 * five of the seven only appeared on whichever tab you happened to land on.
 *
 * Rules the copy keeps, so seven of these do not read as seven different apps:
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
  "/settings/subtitles",
  "/settings/tags"
];

const PREFERENCES_PATHS = ["/settings/general", "/settings/ui", "/settings/notifications", "/settings/migration"];

export const CONFIGURATION_AREAS: readonly ConfigurationArea[] = [
  {
    id: "media-management",
    match: (path) => path === "/settings" || MEDIA_MANAGEMENT_PATHS.some((item) => path.startsWith(item)),
    explainer: {
      lead: "A library is the thing everything else here hangs off. It says what kind of media it holds, which folder the finished files live in, and what happens to a download on its way there. The tabs above follow that journey in the order a file travels it, bar two that arrive once a title is home: the artwork and extra files Deluno keeps beside it, and the subtitle languages that shelf wants.",
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
      path.startsWith("/settings/custom-formats") ||
      path.startsWith("/settings/release-rules"),
    explainer: {
      lead: "Deluno compares every release it finds against what you have said you want, and takes the best one that passes. What you want is seven questions — how good, how big, the picture, the sound, who from, which language, and what you never want — and you answer them once.",
      steps: [
        { title: "Answer the seven questions", body: "Each one already has an answer, so going straight through gives you something that works. Open the ones you want to change, and watch a real release be judged as you change them." },
        { title: "Come back and change one", body: "The same seven questions are a checklist of your answers. Nothing has to be walked again to alter one of them." },
        { title: "Point a library at it", body: "Movies, TV Shows, or any shelf you have made — so anime and films need not want the same things." }
      ]
    }
  },
  {
    id: "find-and-download",
    match: (path) => path.startsWith("/indexers"),
    explainer: {
      lead: "Deluno needs two things before it can fetch a release: somewhere to search, and something to do the downloading. Neither belongs to a library on its own — the library is what says which of them to use. Subtitles come from their own sources, which need no download client because the file arrives in the answer.",
      steps: [
        { title: "Add somewhere to search", body: "An indexer is a search source. Deluno asks every one a library is linked to, then compares what comes back before it picks a release." },
        { title: "Add something to download with", body: "qBittorrent, SABnzbd or another client. Deluno hands the release over and watches it until the file is finished." },
        { title: "Tell each library which to use", body: "A library can search everywhere and download anywhere, or be pinned to its own source and client. Nothing runs for a library until it has both." },
        { title: "Add somewhere to fetch subtitles", body: "Two of the six need no account at all. Each says what it covers and what signing up buys you, and a test tells you whether it is answering before you rely on it." }
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
