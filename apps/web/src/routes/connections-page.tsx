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
  type ConnectionItem,
  type IndexerItem
} from "../lib/api";

interface IndexersLoaderData {
  indexers: IndexerItem[];
  connections: ConnectionItem[];
}

interface MutationState {
  ok?: boolean;
  formError?: string;
  errors?: Record<string, string[]>;
}

export async function connectionsLoader(): Promise<IndexersLoaderData> {
  const [indexers, connections] = await Promise.all([
    fetchJson<IndexerItem[]>("/api/indexers"),
    fetchJson<ConnectionItem[]>("/api/connections")
  ]);

  return { indexers, connections };
}

export async function connectionsAction({ request }: ActionFunctionArgs) {
  const formData = await request.formData();
  const intent = String(formData.get("intent") ?? "create-indexer");

  if (intent === "test-indexer") {
    const id = String(formData.get("id") ?? "");
    const response = await fetch(`/api/indexers/${id}/test`, {
      method: "POST"
    });

    if (response.ok) {
      return { ok: true } satisfies MutationState;
    }

    return {
      formError: "Deluno could not test that indexer right now."
    } satisfies MutationState;
  }

  if (intent === "create-service") {
    const payload = {
      name: formData.get("serviceName"),
      connectionKind: formData.get("connectionKind"),
      role: formData.get("role"),
      endpointUrl: formData.get("endpointUrl"),
      isEnabled: formData.get("isEnabled") === "on"
    };

    const response = await fetch("/api/connections", {
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
      formError: problem?.title ?? "Unable to save the service right now.",
      errors: problem?.errors ?? {}
    } satisfies MutationState;
  }

  const payload = {
    name: formData.get("name"),
    protocol: formData.get("protocol"),
    privacy: formData.get("privacy"),
    baseUrl: formData.get("baseUrl"),
    priority: toNumberOrNull(formData.get("priority")),
    categories: formData.get("categories"),
    tags: formData.get("tags"),
    isEnabled: formData.get("indexerEnabled") === "on"
  };

  const response = await fetch("/api/indexers", {
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
    formError: problem?.title ?? "Unable to save the indexer right now.",
    errors: problem?.errors ?? {}
  } satisfies MutationState;
}

export function ConnectionsPage() {
  const { indexers, connections } = useLoaderData() as IndexersLoaderData;
  const actionData = useActionData() as MutationState | undefined;
  const navigation = useNavigation();
  const isSubmitting = navigation.state === "submitting";

  return (
    <section className="page-stack">
      <header className="page-header">
        <p className="eyebrow">Indexers</p>
        <h2>Search sources, download routing, and service health in one place</h2>
        <p className="page-copy">
          Deluno should make source setup feel native. Add indexers, map the services Deluno uses, and keep Movies and TV Shows pointed at the right places without a second app in the middle.
        </p>
      </header>
      <div className="hero-grid">
        <article className="hero-card hero-card-feature">
          <p className="hero-kicker">What this replaces</p>
          <h3>Indexer management, routing, health, and search visibility should all live inside Deluno.</h3>
          <p>
            No sync maze, no duplicate setup, and no guessing which service is responsible for what. Deluno owns the media workflows and the source layer together.
          </p>
        </article>
        <article className="hero-card">
          <p className="hero-kicker">What belongs here</p>
          <div className="manifest-grid">
            <div className="manifest-row">
              <strong>Indexers</strong>
              <span>Search sources for torrent and usenet releases, including categories, priority, and tags.</span>
            </div>
            <div className="manifest-row">
              <strong>Download routing</strong>
              <span>Clients and services Deluno uses to hand off grabs, track progress, and notify the rest of your setup.</span>
            </div>
            <div className="manifest-row">
              <strong>Health</strong>
              <span>Clear status and future testing surfaces so users know what is ready, paused, or needs attention.</span>
            </div>
          </div>
        </article>
      </div>
      <div className="card-grid">
        <article className="card">
          <h3>Add an indexer</h3>
          <Form method="post" className="entry-form">
            <input type="hidden" name="intent" value="create-indexer" />
            <div className="form-grid">
              <label className="field">
                <span>Name</span>
                <input name="name" type="text" placeholder="Nyaa" required />
                {renderError(actionData?.errors?.name)}
              </label>
              <label className="field">
                <span>Protocol</span>
                <select name="protocol" defaultValue="torrent">
                  <option value="torrent">Torrent</option>
                  <option value="usenet">Usenet</option>
                </select>
              </label>
              <label className="field">
                <span>Privacy</span>
                <select name="privacy" defaultValue="public">
                  <option value="public">Public</option>
                  <option value="private">Private</option>
                </select>
              </label>
              <label className="field">
                <span>Base address</span>
                <input name="baseUrl" type="text" placeholder="https://indexer.example" required />
                {renderError(actionData?.errors?.baseUrl)}
              </label>
              <label className="field">
                <span>Priority</span>
                <input name="priority" type="number" min="1" defaultValue={100} />
              </label>
              <label className="field">
                <span>Categories</span>
                <input name="categories" type="text" placeholder="movies, tv, anime" />
              </label>
              <label className="field">
                <span>Tags</span>
                <input name="tags" type="text" placeholder="4k, anime, kids" />
              </label>
            </div>
            <label className="checkbox-field">
              <input name="indexerEnabled" type="checkbox" defaultChecked />
              <span>Enable this indexer right away</span>
            </label>
            {actionData?.ok ? (
              <p className="feedback feedback-success">Saved. Deluno can start using it right away.</p>
            ) : null}
            {actionData?.formError ? (
              <p className="feedback feedback-error">{actionData.formError}</p>
            ) : null}
            <button className="primary-button" type="submit" disabled={isSubmitting}>
              {isSubmitting ? "Saving..." : "Add indexer"}
            </button>
          </Form>
        </article>
        <article className="card">
          <div className="section-heading">
            <div>
              <h3>Your indexers</h3>
              <p>{indexers.length} source{indexers.length === 1 ? "" : "s"} ready for Deluno.</p>
            </div>
          </div>
          {indexers.length === 0 ? (
            <div className="empty-state">
              <p>No indexers yet. Add the sources Deluno should search across first.</p>
            </div>
          ) : (
            <div className="collection-list">
              {indexers.map((indexer) => (
                <article key={indexer.id} className="collection-item">
                  <div className="item-heading">
                    <strong>{indexer.name}</strong>
                    <span className={`status-pill ${statusClassName(indexer.healthStatus)}`}>
                      {formatHealthStatus(indexer)}
                    </span>
                  </div>
                  <div className="meta-row">
                    <span>{capitalize(indexer.protocol)} · {capitalize(indexer.privacy)} · Priority {indexer.priority}</span>
                    <span>{indexer.isEnabled ? "Enabled" : "Paused"}</span>
                  </div>
                  <div className="meta-row">
                    <span>{indexer.baseUrl}</span>
                    <span>{indexer.categories || "No categories yet"}</span>
                  </div>
                  {indexer.tags ? (
                    <div className="meta-row">
                      <span>Tags: {indexer.tags}</span>
                      <span>{indexer.lastHealthMessage ?? "Ready to use."}</span>
                    </div>
                  ) : (
                    <div className="meta-row">
                      <span>No tags yet</span>
                      <span>{indexer.lastHealthMessage ?? "Ready to use."}</span>
                    </div>
                  )}
                  <Form method="post" className="inline-form">
                    <input type="hidden" name="intent" value="test-indexer" />
                    <input type="hidden" name="id" value={indexer.id} />
                    <button className="secondary-button" type="submit" disabled={isSubmitting}>
                      Test source
                    </button>
                  </Form>
                </article>
              ))}
            </div>
          )}
        </article>
        <article className="card card-wide">
          <div className="section-heading">
            <div>
              <h3>Download routing and services</h3>
              <p>{connections.length} service{connections.length === 1 ? "" : "s"} linked to Deluno.</p>
            </div>
          </div>
          <div className="workspace-grid">
            <Form method="post" className="entry-form">
              <input type="hidden" name="intent" value="create-service" />
              <div className="form-grid">
                <label className="field">
                  <span>Name</span>
                  <input name="serviceName" type="text" placeholder="Primary qBittorrent" required />
                  {renderError(actionData?.errors?.serviceName)}
                </label>
                <label className="field">
                  <span>Service type</span>
                  <select name="connectionKind" defaultValue="downloadClient">
                    <option value="downloadClient">Download client</option>
                    <option value="notification">Notification</option>
                    <option value="mediaServer">Media server</option>
                  </select>
                </label>
                <label className="field">
                  <span>Role</span>
                  <input name="role" type="text" placeholder="Movies / Main and TV Shows / Main" />
                </label>
                <label className="field">
                  <span>Address or endpoint</span>
                  <input name="endpointUrl" type="text" placeholder="http://192.168.1.10:8080" />
                </label>
              </div>
              <label className="checkbox-field">
                <input name="isEnabled" type="checkbox" defaultChecked />
                <span>Enable this service right away</span>
              </label>
              <button className="secondary-button" type="submit" disabled={isSubmitting}>
                {isSubmitting ? "Saving..." : "Add service"}
              </button>
            </Form>
            <div className="collection-list">
              {connections.length === 0 ? (
                <div className="empty-state">
                  <p>No services yet. Add a download client so Deluno knows where to send grabs.</p>
                </div>
              ) : (
                connections.map((connection) => (
                  <article key={connection.id} className="collection-item">
                    <div className="item-heading">
                      <strong>{connection.name}</strong>
                      <span>{formatConnectionKind(connection.connectionKind)}</span>
                    </div>
                    <div className="meta-row">
                      <span>{connection.role}</span>
                      <span>{connection.isEnabled ? "Enabled" : "Paused"}</span>
                    </div>
                    <div className="meta-row">
                      <span>{connection.endpointUrl ?? "No address saved yet"}</span>
                    </div>
                  </article>
                ))
              )}
            </div>
          </div>
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

function formatConnectionKind(value: string) {
  switch (value) {
    case "downloadClient":
      return "Download client";
    case "mediaServer":
      return "Media server";
    default:
      return value.charAt(0).toUpperCase() + value.slice(1);
  }
}

function capitalize(value: string) {
  return value.charAt(0).toUpperCase() + value.slice(1);
}

function formatHealthStatus(indexer: IndexerItem) {
  switch (indexer.healthStatus) {
    case "ready":
      return "Ready";
    case "paused":
      return "Paused";
    case "attention":
      return "Needs attention";
    default:
      return "Not tested";
  }
}

function statusClassName(status: string) {
  switch (status) {
    case "ready":
      return "status-completed";
    case "attention":
      return "status-failed";
    default:
      return "";
  }
}
