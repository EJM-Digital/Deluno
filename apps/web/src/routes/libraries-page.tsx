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
  const payload = {
    name: formData.get("name"),
    mediaType: formData.get("mediaType"),
    purpose: formData.get("purpose"),
    rootPath: formData.get("rootPath"),
    downloadsPath: formData.get("downloadsPath"),
    autoSearchEnabled: formData.get("autoSearchEnabled") === "on",
    searchIntervalHours: toNumberOrNull(formData.get("searchIntervalHours")),
    retryDelayHours: toNumberOrNull(formData.get("retryDelayHours"))
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
        <h2>Separate libraries, one clean app</h2>
        <p className="page-copy">
          Give each part of your collection its own folders, rules, and search rhythm without running extra installs.
        </p>
      </header>
      <div className="hero-grid">
        <article className="hero-card hero-card-feature">
          <p className="hero-kicker">Built for real setups</p>
          <h3>HD, 4K, anime, kids, and custom libraries should feel normal.</h3>
          <p>
            Deluno libraries replace the need to split your life across separate apps just to keep different folders and rules apart.
          </p>
        </article>
        <article className="hero-card">
          <p className="hero-kicker">What each library controls</p>
          <div className="manifest-grid">
            <div className="manifest-row">
              <strong>Folders</strong>
              <span>Choose where this library lives and where finished downloads should come from.</span>
            </div>
            <div className="manifest-row">
              <strong>Automatic searches</strong>
              <span>Set how often Deluno should check for missing and better releases.</span>
            </div>
            <div className="manifest-row">
              <strong>Retry delay</strong>
              <span>Tell Deluno how long to wait before trying the same title again.</span>
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
                <span>Search every (hours)</span>
                <input name="searchIntervalHours" type="number" min="1" defaultValue={6} />
              </label>
              <label className="field">
                <span>Retry after (hours)</span>
                <input name="retryDelayHours" type="number" min="1" defaultValue={24} />
              </label>
            </div>
            <label className="checkbox-field">
              <input name="autoSearchEnabled" type="checkbox" defaultChecked />
              <span>Check this library automatically</span>
            </label>
            {actionData?.ok ? (
              <p className="feedback feedback-success">Library saved.</p>
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
                    <span>{formatMediaType(library.mediaType)}</span>
                  </div>
                  <div className="meta-row">
                    <span>{library.purpose}</span>
                    <span>{library.autoSearchEnabled ? "Auto search on" : "Auto search off"}</span>
                  </div>
                  <div className="meta-row">
                    <span>{library.rootPath}</span>
                    <span>Every {library.searchIntervalHours}h · Retry {library.retryDelayHours}h</span>
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

function formatMediaType(value: string) {
  return value === "tv" ? "TV Shows" : "Movies";
}
