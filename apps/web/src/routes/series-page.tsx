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
  type SeriesListItem
} from "../lib/api";

interface MutationState {
  formError?: string;
  errors?: Record<string, string[]>;
}

export async function seriesLoader(): Promise<SeriesListItem[]> {
  return fetchJson<SeriesListItem[]>("/api/series");
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
  const items = useLoaderData() as SeriesListItem[];
  const actionData = useActionData() as MutationState | undefined;
  const navigation = useNavigation();
  const isSubmitting = navigation.state === "submitting";

  return (
    <section className="page-stack">
      <header className="page-header">
        <p className="eyebrow">Series</p>
        <h2>Series engine workspace</h2>
        <p className="page-copy">
          Series data stays inside its own database file and API surface, ready
          for season, episode, and pack logic without stepping on the movies
          engine.
        </p>
      </header>
      <div className="hero-grid hero-grid-tight">
        <article className="hero-card hero-card-feature">
          <p className="hero-kicker">Series engine</p>
          <h3>{items.length} shows under management.</h3>
          <p>
            This workspace is reserved for episodic flows, alternate orderings,
            packs, and the messy realities of television metadata.
          </p>
        </article>
        <article className="hero-card">
          <p className="hero-kicker">Storage boundary</p>
          <div className="manifest-row">
            <strong>series.db</strong>
            <span>Shows, seasons, episodes, and monitoring state stay isolated here.</span>
          </div>
        </article>
      </div>
      <div className="workspace-grid">
        <article className="card">
          <h3>Add series</h3>
          <Form method="post" className="entry-form">
            <div className="form-grid">
              <label className="field">
                <span>Title</span>
                <input name="title" type="text" placeholder="Severance" required />
                {renderError(actionData?.errors?.title)}
              </label>
              <label className="field">
                <span>Start year</span>
                <input name="startYear" type="number" min="1888" max="2100" />
                {renderError(actionData?.errors?.startYear)}
              </label>
              <label className="field">
                <span>IMDb ID</span>
                <input name="imdbId" type="text" placeholder="tt11280740" />
              </label>
            </div>
            <label className="checkbox-field">
              <input name="monitored" type="checkbox" defaultChecked />
              <span>Monitor this series after it is added</span>
            </label>
            {actionData?.formError ? (
              <p className="feedback feedback-error">{actionData.formError}</p>
            ) : null}
            <button className="primary-button" type="submit" disabled={isSubmitting}>
              {isSubmitting ? "Saving..." : "Add series"}
            </button>
          </Form>
        </article>
        <article className="card">
          <div className="section-heading">
            <div>
              <h3>Tracked series</h3>
              <p>{items.length} item(s) stored in `series.db`.</p>
            </div>
          </div>
          {items.length === 0 ? (
            <div className="empty-state">
              <p>No series added yet.</p>
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
                    <span>{item.imdbId ?? "No IMDb ID yet"}</span>
                    <span className={item.monitored ? "status-tag status-tag-armed" : "status-tag"}>
                      {item.monitored ? "Monitored" : "Unmonitored"}
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
