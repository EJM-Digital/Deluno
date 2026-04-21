import type { ActionFunctionArgs } from "react-router-dom";
import {
  Form,
  useActionData,
  useLoaderData,
  useNavigation
} from "react-router-dom";
import {
  fetchJson,
  readValidationProblem,
  type LibraryItem
} from "../lib/api";

interface MutationState {
  ok?: boolean;
  formError?: string;
  errors?: Record<string, string[]>;
}

export async function librariesLoader(): Promise<LibraryItem[]> {
  return fetchJson<LibraryItem[]>("/api/libraries");
}

export async function librariesAction({ request }: ActionFunctionArgs) {
  const formData = await request.formData();
  const intent = formData.get("intent");

  if (intent === "search-now") {
    const libraryId = String(formData.get("libraryId") ?? "");
    const response = await fetch(`/api/libraries/${libraryId}/search-now`, {
      method: "POST"
    });

    if (response.ok) {
      return { ok: true } satisfies MutationState;
    }

    return {
      formError: "Deluno could not start that library check right now."
    } satisfies MutationState;
  }

  const payload = {
    name: formData.get("name"),
    mediaType: formData.get("mediaType"),
    purpose: formData.get("purpose"),
    rootPath: formData.get("rootPath"),
    downloadsPath: formData.get("downloadsPath"),
    autoSearchEnabled: formData.get("autoSearchEnabled") === "on",
    missingSearchEnabled: formData.get("missingSearchEnabled") === "on",
    upgradeSearchEnabled: formData.get("upgradeSearchEnabled") === "on",
    searchIntervalHours: toNumberOrNull(formData.get("searchIntervalHours")),
    retryDelayHours: toNumberOrNull(formData.get("retryDelayHours")),
    maxItemsPerRun: toNumberOrNull(formData.get("maxItemsPerRun"))
  };

  const response = await fetch("/api/libraries", {
    method: "POST",
    headers: {
      "Content-Type": "application/json"
    },
    body: JSON.stringify(payload)
  });

  if (response.ok) {
    return { ok: true } satisfies MutationState;
  }

  const problem = await readValidationProblem(response);
  return {
    formError: problem?.title ?? "Unable to save the library right now.",
    errors: problem?.errors ?? {}
  } satisfies MutationState;
}

export function LibrariesPage() {
  const libraries = useLoaderData() as LibraryItem[];
  const actionData = useActionData() as MutationState | undefined;
  const navigation = useNavigation();
  const isSubmitting = navigation.state === "submitting";

  return (
    <section className="page-stack">
      <header className="page-header">
        <p className="eyebrow">Libraries</p>
        <h2>Separate workflows, one polished home</h2>
        <p className="page-copy">
          Each library can keep its own folders, timing, and search behavior so Movies and TV Shows never feel tangled together.
        </p>
      </header>
      <div className="hero-grid">
        <article className="hero-card hero-card-feature">
          <p className="hero-kicker">Built for real setups</p>
          <h3>4K movies, everyday movies, kids TV, anime, and custom folders should all feel first-class.</h3>
          <p>
            Deluno lets each library keep its own rhythm instead of making you juggle separate installs just to keep workflows apart.
          </p>
        </article>
        <article className="hero-card">
          <p className="hero-kicker">What each library controls</p>
          <div className="manifest-grid">
            <div className="manifest-row">
              <strong>Folders</strong>
              <span>Choose where the library lives and where completed downloads should come from.</span>
            </div>
            <div className="manifest-row">
              <strong>Recurring checks</strong>
              <span>Let Deluno search for missing titles, better releases, or both on its own schedule.</span>
            </div>
            <div className="manifest-row">
              <strong>Work per pass</strong>
              <span>Control how much Deluno works through in a single run and how long it waits before trying again.</span>
            </div>
          </div>
        </article>
      </div>
      <div className="workspace-grid">
        <article className="card">
          <h3>Add a library</h3>
          <Form method="post" className="entry-form">
            <div className="form-grid">
              <label className="field">
                <span>Library name</span>
                <input name="name" type="text" placeholder="Movies / 4K" required />
                {renderError(actionData?.errors?.name)}
              </label>
              <label className="field">
                <span>Type</span>
                <select name="mediaType" defaultValue="movies">
                  <option value="movies">Movies</option>
                  <option value="tv">TV Shows</option>
                </select>
                {renderError(actionData?.errors?.mediaType)}
              </label>
              <label className="field">
                <span>Purpose</span>
                <input name="purpose" type="text" placeholder="Premium versions" />
              </label>
              <label className="field">
                <span>Library folder</span>
                <input name="rootPath" type="text" placeholder="D:\\Media\\Movies\\4K" required />
                {renderError(actionData?.errors?.rootPath)}
              </label>
              <label className="field">
                <span>Completed downloads folder</span>
                <input name="downloadsPath" type="text" placeholder="D:\\Downloads" />
              </label>
              <label className="field">
                <span>Check every (hours)</span>
                <input name="searchIntervalHours" type="number" min="1" defaultValue={6} />
              </label>
              <label className="field">
                <span>Try again after (hours)</span>
                <input name="retryDelayHours" type="number" min="1" defaultValue={24} />
              </label>
              <label className="field">
                <span>Work through up to</span>
                <input name="maxItemsPerRun" type="number" min="1" defaultValue={25} />
              </label>
            </div>
            <div className="checkbox-stack">
              <label className="checkbox-field">
                <input name="autoSearchEnabled" type="checkbox" defaultChecked />
                <span>Keep this library checked automatically</span>
              </label>
              <label className="checkbox-field">
                <input name="missingSearchEnabled" type="checkbox" defaultChecked />
                <span>Look for titles you do not have yet</span>
              </label>
              <label className="checkbox-field">
                <input name="upgradeSearchEnabled" type="checkbox" defaultChecked />
                <span>Look for better releases later</span>
              </label>
            </div>
            {actionData?.ok ? (
              <p className="feedback feedback-success">Saved. Deluno will pick this up on its next pass.</p>
            ) : null}
            {actionData?.formError ? (
              <p className="feedback feedback-error">{actionData.formError}</p>
            ) : null}
            <button className="primary-button" type="submit" disabled={isSubmitting}>
              {isSubmitting ? "Saving..." : "Add library"}
            </button>
          </Form>
        </article>
        <article className="card">
          <div className="section-heading">
            <div>
              <h3>Your libraries</h3>
              <p>{libraries.length} librar{libraries.length === 1 ? "y" : "ies"} ready for Deluno.</p>
            </div>
          </div>
          {libraries.length === 0 ? (
            <div className="empty-state">
              <p>No libraries yet. Add one and Deluno can start keeping its rules separate.</p>
            </div>
          ) : (
            <div className="collection-list">
              {libraries.map((library) => (
                <article key={library.id} className="collection-item">
                  <div className="item-heading">
                    <strong>{library.name}</strong>
                    <span className={`status-pill ${statusClassName(library.automationStatus)}`}>
                      {formatAutomationStatus(library)}
                    </span>
                  </div>
                  <div className="meta-row">
                    <span>{formatMediaType(library.mediaType)} · {library.purpose}</span>
                    <span>{library.autoSearchEnabled ? "Checked automatically" : "Manual only"}</span>
                  </div>
                  <div className="meta-row">
                    <span>{library.rootPath}</span>
                    <span>Every {library.searchIntervalHours}h · Retry {library.retryDelayHours}h · Up to {library.maxItemsPerRun}</span>
                  </div>
                  <div className="meta-row">
                    <span>{formatSearchModes(library)}</span>
                    <span>{formatTiming(library)}</span>
                  </div>
                  <Form method="post" className="inline-form">
                    <input type="hidden" name="intent" value="search-now" />
                    <input type="hidden" name="libraryId" value={library.id} />
                    <button className="secondary-button" type="submit" disabled={isSubmitting}>
                      Check now
                    </button>
                  </Form>
                </article>
              ))}
            </div>
          )}
        </article>
      </div>
    </section>
  );
}

function renderError(messages?: string[]) {
  if (!messages?.length) {
    return null;
  }

  return <span className="field-error">{messages[0]}</span>;
}

function toNumberOrNull(value: FormDataEntryValue | null) {
  if (typeof value !== "string" || value.trim().length === 0) {
    return null;
  }

  return Number(value);
}

function formatMediaType(value: string) {
  return value === "tv" ? "TV Shows" : "Movies";
}

function formatSearchModes(library: LibraryItem) {
  const modes: string[] = [];

  if (library.missingSearchEnabled) {
    modes.push("Missing");
  }

  if (library.upgradeSearchEnabled) {
    modes.push("Upgrades");
  }

  return modes.length > 0 ? `Checks: ${modes.join(" + ")}` : "Checks: none yet";
}

function formatTiming(library: LibraryItem) {
  if (library.searchRequested) {
    return "Manual check queued";
  }

  if (library.nextSearchUtc) {
    return `Next check ${formatDateTime(library.nextSearchUtc)}`;
  }

  if (library.lastSearchedUtc) {
    return `Last checked ${formatDateTime(library.lastSearchedUtc)}`;
  }

  return "Ready when you are";
}

function formatAutomationStatus(library: LibraryItem) {
  if (library.searchRequested) {
    return "Queued next";
  }

  switch (library.automationStatus) {
    case "queued":
      return "Queued";
    case "running":
      return "Checking now";
    case "ready":
      return "Healthy";
    case "paused":
      return "Paused";
    case "attention":
      return "Needs attention";
    default:
      return library.autoSearchEnabled ? "Standing by" : "Manual only";
  }
}

function statusClassName(status: string) {
  switch (status) {
    case "queued":
      return "status-queued";
    case "running":
      return "status-running";
    case "ready":
      return "status-completed";
    case "attention":
      return "status-failed";
    default:
      return "";
  }
}

function formatDateTime(value: string) {
  return new Date(value).toLocaleString([], {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  });
}
