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
  type PlatformSettingsSnapshot
} from "../lib/api";

interface MutationState {
  ok?: boolean;
  formError?: string;
  errors?: Record<string, string[]>;
}

export async function settingsLoader(): Promise<PlatformSettingsSnapshot> {
  return fetchJson<PlatformSettingsSnapshot>("/api/settings");
}

export async function settingsAction({ request }: ActionFunctionArgs) {
  const formData = await request.formData();
  const payload = {
    appInstanceName: formData.get("appInstanceName"),
    movieRootPath: formData.get("movieRootPath"),
    seriesRootPath: formData.get("seriesRootPath"),
    downloadsPath: formData.get("downloadsPath"),
    incompleteDownloadsPath: formData.get("incompleteDownloadsPath"),
    autoStartJobs: formData.get("autoStartJobs") === "on",
    enableNotifications: formData.get("enableNotifications") === "on"
  };

  const response = await fetch("/api/settings", {
    method: "PUT",
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
    formError: problem?.title ?? "Unable to save settings right now.",
    errors: problem?.errors ?? {}
  } satisfies MutationState;
}

export function SettingsPage() {
  const settings = useLoaderData() as PlatformSettingsSnapshot;
  const actionData = useActionData() as MutationState | undefined;
  const navigation = useNavigation();
  const isSubmitting = navigation.state === "submitting";

  return (
    <section className="page-stack">
      <header className="page-header">
        <p className="eyebrow">Settings</p>
        <h2>System configuration</h2>
        <p className="page-copy">
          Platform settings live in `platform.db` and control the roots and
          operational defaults shared by the movie and series engines.
        </p>
      </header>
      <article className="card">
        <Form method="post" className="entry-form">
          <div className="form-grid">
            <label className="field">
              <span>App name</span>
              <input
                name="appInstanceName"
                type="text"
                defaultValue={settings.appInstanceName}
                required
              />
              {renderError(actionData?.errors?.appInstanceName)}
            </label>
            <label className="field">
              <span>Movie root</span>
              <input
                name="movieRootPath"
                type="text"
                placeholder="D:\\Media\\Movies"
                defaultValue={settings.movieRootPath ?? ""}
              />
            </label>
            <label className="field">
              <span>Series root</span>
              <input
                name="seriesRootPath"
                type="text"
                placeholder="D:\\Media\\Series"
                defaultValue={settings.seriesRootPath ?? ""}
              />
            </label>
            <label className="field">
              <span>Downloads path</span>
              <input
                name="downloadsPath"
                type="text"
                placeholder="D:\\Downloads"
                defaultValue={settings.downloadsPath ?? ""}
              />
            </label>
            <label className="field">
              <span>Incomplete downloads path</span>
              <input
                name="incompleteDownloadsPath"
                type="text"
                placeholder="D:\\Downloads\\Incomplete"
                defaultValue={settings.incompleteDownloadsPath ?? ""}
              />
            </label>
          </div>
          <div className="checkbox-stack">
            <label className="checkbox-field">
              <input
                name="autoStartJobs"
                type="checkbox"
                defaultChecked={settings.autoStartJobs}
              />
              <span>Automatically process queued jobs</span>
            </label>
            <label className="checkbox-field">
              <input
                name="enableNotifications"
                type="checkbox"
                defaultChecked={settings.enableNotifications}
              />
              <span>Enable app notifications</span>
            </label>
          </div>
          {actionData?.ok ? (
            <p className="feedback feedback-success">Settings saved.</p>
          ) : null}
          {actionData?.formError ? (
            <p className="feedback feedback-error">{actionData.formError}</p>
          ) : null}
          <button className="primary-button" type="submit" disabled={isSubmitting}>
            {isSubmitting ? "Saving..." : "Save settings"}
          </button>
        </Form>
      </article>
    </section>
  );
}

function renderError(messages?: string[]) {
  if (!messages?.length) {
    return null;
  }

  return <span className="field-error">{messages[0]}</span>;
}
