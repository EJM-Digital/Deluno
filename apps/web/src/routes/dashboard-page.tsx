import { useLoaderData } from "react-router-dom";
import {
  fetchJson,
  type MovieListItem,
  type SeriesListItem,
  type SystemManifest
} from "../lib/api";

interface DashboardLoaderData {
  movieCount: number;
  tvShowCount: number;
}

export async function dashboardLoader(): Promise<DashboardLoaderData> {
  const [movies, series] = await Promise.all([
    fetchJson<MovieListItem[]>("/api/movies"),
    fetchJson<SeriesListItem[]>("/api/series")
  ]);

  return {
    movieCount: movies.length,
    tvShowCount: series.length
  };
}

export function DashboardPage() {
  const { movieCount, tvShowCount } =
    useLoaderData() as DashboardLoaderData;

  return (
    <section className="page-stack">
      <header className="page-header">
        <p className="eyebrow">Overview</p>
        <h2>Your library, all in one place</h2>
        <p className="page-copy">
          Deluno helps you keep movies and TV shows easy to find, easy to download, and easy to manage without jumping between separate apps.
        </p>
      </header>
      <div className="hero-grid">
        <article className="hero-card hero-card-feature">
          <p className="hero-kicker">Everything in one home</p>
          <h3>Find what you want. Grab what is missing. Keep everything in order.</h3>
          <p>
            Movies and TV shows each keep their own rules behind the scenes, but for the user it should feel like one clean, obvious library.
          </p>
        </article>
        <article className="hero-card">
          <p className="hero-kicker">What Deluno will help with</p>
          <div className="manifest-grid">
            <div className="manifest-row">
              <strong>Wanted searches</strong>
              <span>Keep looking for missing movies and TV shows without you needing to remember.</span>
            </div>
            <div className="manifest-row">
              <strong>Upgrades</strong>
              <span>Spot better releases later and bring them in automatically when they are worth replacing.</span>
            </div>
            <div className="manifest-row">
              <strong>Clean importing</strong>
              <span>Move finished downloads into the right library folder with the right name.</span>
            </div>
          </div>
        </article>
      </div>
      <div className="stat-grid">
        <article className="stat-card">
          <p className="stat-label">Movies</p>
          <strong>{movieCount}</strong>
        </article>
        <article className="stat-card">
          <p className="stat-label">TV Shows</p>
          <strong>{tvShowCount}</strong>
        </article>
        <article className="stat-card">
          <p className="stat-label">Status</p>
          <strong>Ready</strong>
        </article>
      </div>
      <div className="card-grid">
        <article className="card">
          <h3>Movies</h3>
          <p>
            Add movies you want, keep an eye out for missing ones, and upgrade them later when something better shows up.
          </p>
          <span className="inline-badge">Missing, upgrades, imports</span>
        </article>
        <article className="card">
          <h3>TV Shows</h3>
          <p>
            Track full shows, episode releases, packs, and all the awkward real-world TV cases that normal users should never have to think about.
          </p>
          <span className="inline-badge">Episodes, packs, daily, anime</span>
        </article>
        <article className="card">
          <h3>Activity</h3>
          <p>
            See what Deluno is checking, downloading, importing, or waiting on without digging through a wall of technical logs.
          </p>
          <span className="inline-badge">Simple status, clear actions</span>
        </article>
        <article className="card">
          <h3>Settings</h3>
          <p>
            Point Deluno at your folders, connect your apps, and choose how hands-off you want the automation to be.
          </p>
          <span className="inline-badge">Folders, apps, automation</span>
        </article>
        <article className="card card-wide">
          <h3>What comes next</h3>
          <div className="collection-list compact-list">
            <div className="collection-item compact-item">
              <strong>Multiple library setups</strong>
              <p>Support for separate 4K, HD, anime, or other split libraries without making the UI confusing.</p>
            </div>
            <div className="collection-item compact-item">
              <strong>Recurring wanted searches</strong>
              <p>Deluno-owned search scheduling for missing and upgrade checks instead of relying on manual searches.</p>
            </div>
            <div className="collection-item compact-item">
              <strong>Fetcher behavior built in</strong>
              <p>Bring over the useful parts of Fetcher so Deluno can proactively look after your library.</p>
            </div>
          </div>
        </article>
      </div>
    </section>
  );
}
