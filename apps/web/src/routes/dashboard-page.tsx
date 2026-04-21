import { useLoaderData } from "react-router-dom";
import {
  fetchJson,
  type MovieListItem,
  type SeriesListItem,
  type SystemManifest
} from "../lib/api";

interface DashboardLoaderData {
  manifest: SystemManifest;
  movieCount: number;
  seriesCount: number;
}

export async function dashboardLoader(): Promise<DashboardLoaderData> {
  const [manifest, movies, series] = await Promise.all([
    fetchJson<SystemManifest>("/api/manifest"),
    fetchJson<MovieListItem[]>("/api/movies"),
    fetchJson<SeriesListItem[]>("/api/series")
  ]);

  return {
    manifest,
    movieCount: movies.length,
    seriesCount: series.length
  };
}

export function DashboardPage() {
  const { manifest, movieCount, seriesCount } =
    useLoaderData() as DashboardLoaderData;

  return (
    <section className="page-stack">
      <header className="page-header">
        <p className="eyebrow">Overview</p>
        <h2>One app, separate engines</h2>
        <p className="page-copy">
          Deluno now boots a single host with separate movie and series
          databases, real endpoints, and durable workers behind the shell.
        </p>
      </header>
      <div className="stat-grid">
        <article className="stat-card">
          <p className="stat-label">Movies tracked</p>
          <strong>{movieCount}</strong>
        </article>
        <article className="stat-card">
          <p className="stat-label">Series tracked</p>
          <strong>{seriesCount}</strong>
        </article>
        <article className="stat-card">
          <p className="stat-label">SQLite files</p>
          <strong>{manifest.databases.length}</strong>
        </article>
      </div>
      <div className="card-grid">
        <article className="card">
          <h3>Movies</h3>
          <p>
            Movie search, grab, import, profiles, and history stay isolated inside
            the movies module.
          </p>
          <span className="inline-badge">Database: movies.db</span>
        </article>
        <article className="card">
          <h3>Series</h3>
          <p>
            Series workflows keep their own state, queues, matching rules, and
            import logic.
          </p>
          <span className="inline-badge">Database: series.db</span>
        </article>
        <article className="card">
          <h3>Jobs</h3>
          <p>
            Durable background workers track search, polling, imports, and cleanup
            through the jobs database.
          </p>
          <span className="inline-badge">Database: jobs.db</span>
        </article>
        <article className="card">
          <h3>Storage root</h3>
          <p>{manifest.storageRoot}</p>
        </article>
        <article className="card card-wide">
          <h3>Registered modules</h3>
          <div className="collection-list compact-list">
            {manifest.modules.map((module) => (
              <div key={module.name} className="collection-item compact-item">
                <strong>{module.name}</strong>
                <p>{module.purpose}</p>
              </div>
            ))}
          </div>
        </article>
      </div>
    </section>
  );
}
