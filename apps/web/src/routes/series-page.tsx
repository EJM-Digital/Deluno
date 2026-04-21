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
  type SeriesImportRecoverySummary,
  type SeriesListItem
} from "../lib/api";

interface MutationState {
  formError?: string;
  errors?: Record<string, string[]>;
}

interface SeriesLoaderData {
  items: SeriesListItem[];
  importRecovery: SeriesImportRecoverySummary;
}

export async function seriesLoader(): Promise<SeriesLoaderData> {
  const [items, importRecovery] = await Promise.all([
    fetchJson<SeriesListItem[]>("/api/series"),
    fetchJson<SeriesImportRecoverySummary>("/api/series/import-recovery")
  ]);

  return { items, importRecovery };
}

export async function seriesAction({ request }: ActionFunctionArgs) {
  const formData = await request.formData();
  const payload = {
    title: formData.get("title"),
    startYear: toNumberOrNull(formData.get("startYear")),
    imdbId: formData.get("imdbId"),
    monitored: formData.get("monitored") === "on"
  };

  const response = await fetch("/api/series", {
    method: "POST",
    headers: {
      "Content-Type": "application/json"
    },
    body: JSON.stringify(payload)
  });

  if (response.ok) {
    return { ok: true };
  }

  const problem = await readValidationProblem(response);
  return {
    formError: problem?.title ?? "Unable to save the series right now.",
    errors: problem?.errors ?? {}
  } satisfies MutationState;
}

export function SeriesPage() {
  const { items, importRecovery } = useLoaderData() as SeriesLoaderData;
  const actionData = useActionData() as MutationState | undefined;
  const navigation = useNavigation();
  const isSubmitting = navigation.state === "submitting";

  return (
    <section className="page-stack">
      <header className="page-header">
        <p className="eyebrow">TV Shows</p>
        <h2>TV Shows</h2>
        <p className="page-copy">
          Add TV shows you want and let Deluno keep up with missing episodes, better releases, and the awkward bits that come with TV.
        </p>
      </header>
      <div className="hero-grid hero-grid-tight">
        <article className="hero-card hero-card-feature">
          <p className="hero-kicker">TV library</p>
          <h3>{items.length} TV show{items.length === 1 ? "" : "s"} on your list.</h3>
          <p>
            Deluno needs to handle normal shows, daily releases, anime, specials, and packs without making any of that feel complicated.
          </p>
        </article>
        <article className="hero-card">
          <p className="hero-kicker">What happens next</p>
          <div className="manifest-row">
            <strong>Keep checking automatically</strong>
            <span>After you add a show, Deluno can keep looking for missing episodes and better replacements later.</span>
          </div>
        </article>
      </div>
      <div className="workspace-grid">
        <article className="card">
          <h3>Add a TV show</h3>
          <Form method="post" className="entry-form">
            <div className="form-grid">
              <label className="field">
                <span>Show title</span>
                <input name="title" type="text" placeholder="Severance" required />
                {renderError(actionData?.errors?.title)}
              </label>
              <label className="field">
                <span>First aired year</span>
                <input name="startYear" type="number" min="1888" max="2100" />
                {renderError(actionData?.errors?.startYear)}
              </label>
              <label className="field">
                <span>IMDb ID or link (optional)</span>
                <input name="imdbId" type="text" placeholder="tt11280740" />
              </label>
            </div>
            <label className="checkbox-field">
              <input name="monitored" type="checkbox" defaultChecked />
              <span>Keep looking for this show automatically</span>
            </label>
            {actionData?.formError ? (
              <p className="feedback feedback-error">{actionData.formError}</p>
            ) : null}
            <button className="primary-button" type="submit" disabled={isSubmitting}>
              {isSubmitting ? "Saving..." : "Add TV show"}
            </button>
          </Form>
        </article>
        <article className="card">
          <div className="section-heading">
            <div>
              <h3>Your TV shows</h3>
              <p>{items.length} show{items.length === 1 ? "" : "s"} on your wanted list.</p>
            </div>
          </div>
          {items.length === 0 ? (
            <div className="empty-state">
              <p>No TV shows yet. Add one and Deluno can start keeping up with it.</p>
            </div>
          ) : (
            <div className="collection-list">
              {items.map((item) => (
                <article key={item.id} className="collection-item">
                  <div className="item-heading">
                    <strong>{item.title}</strong>
                    {item.startYear ? <span>{item.startYear}</span> : null}
                  </div>
                  <div className="meta-row">
                    <span>{item.imdbId ?? "No IMDb link added"}</span>
                    <span className={item.monitored ? "status-tag status-tag-armed" : "status-tag"}>
                      {item.monitored ? "Auto search on" : "Auto search off"}
                    </span>
                  </div>
                </article>
              ))}
            </div>
          )}
        </article>
      </div>
      <div className="card-grid">
        <article className="card">
          <h3>Import recovery</h3>
          <p>
            TV imports are messy in their own special ways. Deluno will keep track of failed episode imports, unmatched files, corrupt downloads, and quality rejects here.
          </p>
          <div className="manifest-grid">
            <div className="manifest-row">
              <strong>{importRecovery.openCount}</strong>
              <span>TV import issue{importRecovery.openCount === 1 ? "" : "s"} currently open</span>
            </div>
            <div className="manifest-row">
              <strong>{importRecovery.unmatchedCount}</strong>
              <span>files that need matching or review</span>
            </div>
            <div className="manifest-row">
              <strong>{importRecovery.importFailedCount}</strong>
              <span>items that reached a hard import failure</span>
            </div>
          </div>
        </article>
        <article className="card">
          <h3>Recent import issues</h3>
          {importRecovery.recentCases.length === 0 ? (
            <div className="empty-state">
              <p>No TV import issues right now. Deluno is ready to surface quality rejects, unmatched episodes, corrupt downloads, and failed imports here.</p>
            </div>
          ) : (
            <div className="collection-list">
              {importRecovery.recentCases.map((item) => (
                <article key={item.id} className="collection-item">
                  <div className="item-heading">
                    <strong>{item.title}</strong>
                    <span>{formatFailureKind(item.failureKind)}</span>
                  </div>
                  <div className="meta-row">
                    <span>{item.summary}</span>
                  </div>
                  <div className="meta-row">
                    <span>{item.recommendedAction}</span>
                    <span>{formatDateTime(item.detectedUtc)}</span>
                  </div>
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

function formatFailureKind(value: string) {
  switch (value) {
    case "quality":
      return "Quality rejected";
    case "unmatched":
      return "Needs matching";
    case "corrupt":
      return "Corrupt";
    case "downloadFailed":
      return "Download failed";
    case "importFailed":
      return "Import failed";
    default:
      return "Needs review";
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
