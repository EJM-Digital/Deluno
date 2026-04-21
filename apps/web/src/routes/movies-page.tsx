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
  type MovieListItem
} from "../lib/api";

interface MutationState {
  formError?: string;
  errors?: Record<string, string[]>;
}

export async function moviesLoader(): Promise<MovieListItem[]> {
  return fetchJson<MovieListItem[]>("/api/movies");
}

export async function moviesAction({ request }: ActionFunctionArgs) {
  const formData = await request.formData();
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
  const movies = useLoaderData() as MovieListItem[];
  const actionData = useActionData() as MutationState | undefined;
  const navigation = useNavigation();
  const isSubmitting = navigation.state === "submitting";

  return (
    <section className="page-stack">
      <header className="page-header">
        <p className="eyebrow">Movies</p>
        <h2>Movie engine workspace</h2>
        <p className="page-copy">
          This module owns its own schema, endpoints, and storage file. Nothing
          here shares operational state with series.
        </p>
      </header>
      <div className="workspace-grid">
        <article className="card">
          <h3>Add movie</h3>
          <Form method="post" className="entry-form">
            <div className="form-grid">
              <label className="field">
                <span>Title</span>
                <input name="title" type="text" placeholder="Arrival" required />
                {renderError(actionData?.errors?.title)}
              </label>
              <label className="field">
                <span>Release year</span>
                <input name="releaseYear" type="number" min="1888" max="2100" />
                {renderError(actionData?.errors?.releaseYear)}
              </label>
              <label className="field">
                <span>IMDb ID</span>
                <input name="imdbId" type="text" placeholder="tt2543164" />
              </label>
            </div>
            <label className="checkbox-field">
              <input name="monitored" type="checkbox" defaultChecked />
              <span>Monitor this movie after it is added</span>
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
              <h3>Tracked movies</h3>
              <p>{movies.length} item(s) stored in `movies.db`.</p>
            </div>
          </div>
          {movies.length === 0 ? (
            <div className="empty-state">
              <p>No movies added yet.</p>
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
                    <span>{movie.imdbId ?? "No IMDb ID yet"}</span>
                    <span>{movie.monitored ? "Monitored" : "Unmonitored"}</span>
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
