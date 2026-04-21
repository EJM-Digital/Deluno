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
  type ConnectionItem
} from "../lib/api";

interface MutationState {
  ok?: boolean;
  formError?: string;
  errors?: Record<string, string[]>;
}

export async function connectionsLoader(): Promise<ConnectionItem[]> {
  return fetchJson<ConnectionItem[]>("/api/connections");
}

export async function connectionsAction({ request }: ActionFunctionArgs) {
  const formData = await request.formData();
  const payload = {
    name: formData.get("name"),
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
    formError: problem?.title ?? "Unable to save the connection right now.",
    errors: problem?.errors ?? {}
  } satisfies MutationState;
}

export function ConnectionsPage() {
  const connections = useLoaderData() as ConnectionItem[];
  const actionData = useActionData() as MutationState | undefined;
  const navigation = useNavigation();
  const isSubmitting = navigation.state === "submitting";

  return (
    <section className="page-stack">
      <header className="page-header">
        <p className="eyebrow">Connections</p>
        <h2>Connect the services Deluno needs</h2>
        <p className="page-copy">
          Deluno runs the library itself. These connections are for the indexers, download clients, notifications, and related services it relies on.
        </p>
      </header>
      <div className="hero-grid hero-grid-tight">
        <article className="hero-card hero-card-feature">
          <p className="hero-kicker">Run Deluno directly</p>
          <h3>One app for your library, with clear service connections around it.</h3>
          <p>
            Keep indexers, download clients, and alerts easy to understand, easy to test, and easy to change without turning setup into a maze.
          </p>
        </article>
        <article className="hero-card">
          <p className="hero-kicker">Connection types</p>
          <div className="manifest-grid">
            <div className="manifest-row">
              <strong>Indexers</strong>
              <span>Where Deluno searches for release results.</span>
            </div>
            <div className="manifest-row">
              <strong>Download clients</strong>
              <span>Where Deluno sends grabs and tracks progress.</span>
            </div>
            <div className="manifest-row">
              <strong>Notifications and media servers</strong>
              <span>Optional alerts and library refresh targets for a polished setup.</span>
            </div>
          </div>
        </article>
      </div>
      <div className="workspace-grid">
        <article className="card">
          <h3>Add a connection</h3>
          <Form method="post" className="entry-form">
            <div className="form-grid">
              <label className="field">
                <span>Connection name</span>
                <input name="name" type="text" placeholder="Primary download client" required />
                {renderError(actionData?.errors?.name)}
              </label>
              <label className="field">
                <span>Connection type</span>
                <select name="connectionKind" defaultValue="indexer">
                  <option value="indexer">Indexer</option>
                  <option value="downloadClient">Download client</option>
                  <option value="notification">Notification</option>
                  <option value="mediaServer">Media server</option>
                </select>
                {renderError(actionData?.errors?.connectionKind)}
              </label>
              <label className="field">
                <span>Role</span>
                <input name="role" type="text" placeholder="Main movies and TV" />
              </label>
              <label className="field">
                <span>Address or endpoint</span>
                <input name="endpointUrl" type="text" placeholder="http://192.168.1.10:8080" />
              </label>
            </div>
            <label className="checkbox-field">
              <input name="isEnabled" type="checkbox" defaultChecked />
              <span>Enable this connection</span>
            </label>
            {actionData?.ok ? (
              <p className="feedback feedback-success">Connection saved.</p>
            ) : null}
            {actionData?.formError ? (
              <p className="feedback feedback-error">{actionData.formError}</p>
            ) : null}
            <button className="primary-button" type="submit" disabled={isSubmitting}>
              {isSubmitting ? "Saving..." : "Add connection"}
            </button>
          </Form>
        </article>
        <article className="card">
          <div className="section-heading">
            <div>
              <h3>Your connections</h3>
              <p>{connections.length} connection{connections.length === 1 ? "" : "s"} saved for Deluno.</p>
            </div>
          </div>
          {connections.length === 0 ? (
            <div className="empty-state">
              <p>No connections yet. Add the services Deluno should search, download through, or notify.</p>
            </div>
          ) : (
            <div className="collection-list">
              {connections.map((connection) => (
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
