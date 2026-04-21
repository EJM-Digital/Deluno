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
  type MovieImportRecoverySummary,
  type MovieListItem,
  type MovieWantedSummary
} from "../lib/api";

interface MutationState {
  formError?: string;
  errors?: Record<string, string[]>;
}

interface MoviesLoaderData {
  movies: MovieListItem[];
  importRecovery: MovieImportRecoverySummary;
  wanted: MovieWantedSummary;
}

export async function moviesLoader(): Promise<MoviesLoaderData> {
  const [movies, importRecovery, wanted] = await Promise.all([
    fetchJson<MovieListItem[]>("/api/movies"),
    fetchJson<MovieImportRecoverySummary>("/api/movies/import-recovery"),
    fetchJson<MovieWantedSummary>("/api/movies/wanted")
  ]);

  return { movies, importRecovery, wanted };
}

export async function moviesAction({ request }: ActionFunctionArgs) {
  const formData = await request.formData();
  const intent = String(formData.get("intent") ?? "create-movie");

  if (intent === "add-import-issue") {
    const payload = {
      title: formData.get("issueTitle"),
      failureKind: formData.get("failureKind"),
      summary: formData.get("issueSummary"),
      recommendedAction: formData.get("recommendedAction")
    };

    const response = await fetch("/api/movies/import-recovery", {
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
      formError: problem?.title ?? "Unable to save the import issue right now.",
      errors: problem?.errors ?? {}
    } satisfies MutationState;
  }

  const payload = {
    title: formData.get("title"),
    releaseYear: toNumberOrNull(formData.get("releaseYear")),
    imdbId: formData.get("imdbId"),
    monitored: formData.get("monitored") === "on"
  };

  const response = await fetch("/api/movies", {
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
    formError: problem?.title ?? "Unable to save the movie right now.",
    errors: problem?.errors ?? {}
  } satisfies MutationState;
}

export function MoviesPage() {
  const { movies, importRecovery, wanted } = useLoaderData() as MoviesLoaderData;
  const actionData = useActionData() as MutationState | undefined;
  const navigation = useNavigation();
  const isSubmitting = navigation.state === "submitting";

  return (
    <section className="page-stack">
      <header className="page-header">
        <p className="eyebrow">Movies</p>
        <h2>Movies</h2>
        <p className="page-copy">
          Add the movies you care about and let Deluno keep checking for missing releases and better versions over time.
        </p>
      </header>
      <div className="hero-grid hero-grid-tight">
        <article className="hero-card hero-card-feature">
          <p className="hero-kicker">Movie library</p>
          <h3>{movies.length} movie{movies.length === 1 ? "" : "s"} on your list.</h3>
          <p>
            This is where Deluno keeps track of what you want, what is missing, and what could be upgraded later.
          </p>
        </article>
        <article className="hero-card">
          <p className="hero-kicker">What happens next</p>
          <div className="manifest-grid">
            <div className="manifest-row">
              <strong>{wanted.missingCount}</strong>
              <span>movie{wanted.missingCount === 1 ? "" : "s"} still missing</span>
            </div>
            <div className="manifest-row">
              <strong>{wanted.upgradeCount}</strong>
              <span>ready for a better upgrade</span>
            </div>
            <div className="manifest-row">
              <strong>{wanted.waitingCount}</strong>
              <span>waiting before Deluno checks again</span>
            </div>
          </div>
        </article>
      </div>
      <div className="workspace-grid">
        <article className="card">
          <h3>Add a movie</h3>
          <Form method="post" className="entry-form">
            <input type="hidden" name="intent" value="create-movie" />
            <div className="form-grid">
              <label className="field">
                <span>Movie title</span>
                <input name="title" type="text" placeholder="Arrival" required />
                {renderError(actionData?.errors?.title)}
              </label>
              <label className="field">
                <span>Release year</span>
                <input name="releaseYear" type="number" min="1888" max="2100" />
                {renderError(actionData?.errors?.releaseYear)}
              </label>
              <label className="field">
                <span>IMDb ID or link (optional)</span>
                <input name="imdbId" type="text" placeholder="tt2543164" />
              </label>
            </div>
            <label className="checkbox-field">
              <input name="monitored" type="checkbox" defaultChecked />
              <span>Keep looking for this movie automatically</span>
            </label>
            {actionData?.formError ? (
              <p className="feedback feedback-error">{actionData.formError}</p>
            ) : null}
            <button className="primary-button" type="submit" disabled={isSubmitting}>
              {isSubmitting ? "Saving..." : "Add movie"}
            </button>
          </Form>
        </article>
        <article className="card">
          <div className="section-heading">
            <div>
              <h3>Your movies</h3>
              <p>{movies.length} movie{movies.length === 1 ? "" : "s"} on your wanted list.</p>
            </div>
          </div>
          {movies.length === 0 ? (
            <div className="empty-state">
              <p>No movies yet. Start with one title you want Deluno to keep an eye on.</p>
            </div>
          ) : (
            <div className="collection-list">
              {movies.map((movie) => (
                <article key={movie.id} className="collection-item">
                  <div className="item-heading">
                    <strong>{movie.title}</strong>
                    {movie.releaseYear ? <span>{movie.releaseYear}</span> : null}
                  </div>
                  <div className="meta-row">
                    <span>{movie.imdbId ?? "No IMDb link added"}</span>
                    <span className={movie.monitored ? "status-tag status-tag-armed" : "status-tag"}>
                      {movie.monitored ? "Auto search on" : "Auto search off"}
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
          <h3>Wanted right now</h3>
          {wanted.recentItems.length === 0 ? (
            <div className="empty-state">
              <p>Nothing is waiting for Deluno right now.</p>
            </div>
          ) : (
            <div className="collection-list">
              {wanted.recentItems.map((item) => (
                <article key={item.movieId} className="collection-item">
                  <div className="item-heading">
                    <strong>{item.title}</strong>
                    <span>{formatWantedStatus(item.wantedStatus)}</span>
                  </div>
                  <div className="meta-row">
                    <span>{item.wantedReason}</span>
                  </div>
                  <div className="meta-row">
                    <span>{formatQualityLine(item.currentQuality, item.targetQuality, item.qualityCutoffMet)}</span>
                  </div>
                  <div className="meta-row">
                    <span>{item.lastSearchResult ?? "Deluno has not checked this one yet."}</span>
                    <span>{item.nextEligibleSearchUtc ? `Next check ${formatDateTime(item.nextEligibleSearchUtc)}` : "Ready now"}</span>
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
            When Deluno sees import trouble, this is where Movies will explain what went wrong and what it recommends next.
          </p>
          <Form method="post" className="entry-form">
            <input type="hidden" name="intent" value="add-import-issue" />
            <div className="form-grid">
              <label className="field">
                <span>Movie title</span>
                <input name="issueTitle" type="text" placeholder="Arrival" />
              </label>
              <label className="field">
                <span>Issue type</span>
                <select name="failureKind" defaultValue="unmatched">
                  <option value="unmatched">Needs matching</option>
                  <option value="quality">Quality rejected</option>
                  <option value="corrupt">Corrupt</option>
                  <option value="downloadFailed">Download failed</option>
                  <option value="importFailed">Import failed</option>
                </select>
              </label>
              <label className="field">
                <span>What happened</span>
                <input name="issueSummary" type="text" placeholder="Manual import could not match the movie folder." />
              </label>
              <label className="field">
                <span>Recommended next step</span>
                <input name="recommendedAction" type="text" placeholder="Review the folder name and retry the match." />
              </label>
            </div>
            <button className="secondary-button" type="submit" disabled={isSubmitting}>
              {isSubmitting ? "Saving..." : "Add import issue"}
            </button>
          </Form>
          <div className="manifest-grid">
            <div className="manifest-row">
              <strong>{importRecovery.openCount}</strong>
              <span>movie import issue{importRecovery.openCount === 1 ? "" : "s"} currently open</span>
            </div>
            <div className="manifest-row">
              <strong>{importRecovery.qualityCount}</strong>
              <span>quality or upgrade rejections</span>
            </div>
            <div className="manifest-row">
              <strong>{importRecovery.unmatchedCount}</strong>
              <span>titles that need matching or review</span>
            </div>
          </div>
        </article>
        <article className="card">
          <h3>Recent import issues</h3>
          {importRecovery.recentCases.length === 0 ? (
            <div className="empty-state">
              <p>No movie import issues right now. Deluno is ready to surface quality rejects, unmatched items, corrupt downloads, and failed imports here.</p>
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

function formatWantedStatus(value: string) {
  switch (value) {
    case "upgrade":
      return "Upgrade";
    case "waiting":
      return "Waiting";
    default:
      return "Missing";
  }
}

function formatQualityLine(currentQuality: string | null, targetQuality: string | null, cutoffMet: boolean) {
  if (currentQuality && targetQuality) {
    return cutoffMet
      ? `Current quality ${currentQuality} meets the ${targetQuality} target`
      : `Current quality ${currentQuality} · Target ${targetQuality}`;
  }

  if (targetQuality) {
    return `Target quality ${targetQuality}`;
  }

  return "Quality target still needs to be set";
}

function formatDateTime(value: string) {
  return new Date(value).toLocaleString([], {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
  });
}
