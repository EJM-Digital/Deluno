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
          <div className="manifest-row">
            <strong>Keep checking automatically</strong>
            <span>After you add a movie, Deluno can keep looking for the first good release and future upgrades.</span>
          </div>
        </article>
      </div>
      <div className="workspace-grid">
        <article className="card">
          <h3>Add a movie</h3>
          <Form method="post" className="entry-form">
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
