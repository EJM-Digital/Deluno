import { useEffect } from "react";
import { useLoaderData, useRevalidator } from "react-router-dom";
import {
  fetchJson,
  type ActivityEventItem,
  type JobQueueItem
} from "../lib/api";

interface ActivityLoaderData {
  jobs: JobQueueItem[];
  activity: ActivityEventItem[];
}

export async function activityLoader(): Promise<ActivityLoaderData> {
  const [jobs, activity] = await Promise.all([
    fetchJson<JobQueueItem[]>("/api/jobs?take=20"),
    fetchJson<ActivityEventItem[]>("/api/activity?take=40")
  ]);

  return { jobs, activity };
}

export function ActivityPage() {
  const { jobs, activity } = useLoaderData() as ActivityLoaderData;
  const revalidator = useRevalidator();

  useEffect(() => {
    const timer = window.setInterval(() => {
      revalidator.revalidate();
    }, 5000);

    return () => window.clearInterval(timer);
  }, [revalidator]);

  return (
    <section className="page-stack">
      <header className="page-header">
        <p className="eyebrow">Activity</p>
        <h2>What Deluno is doing</h2>
        <p className="page-copy">
          See what Deluno is checking, waiting on, downloading, or finishing up in the background.
        </p>
      </header>
      <div className="hero-grid hero-grid-tight">
        <article className="hero-card hero-card-feature">
          <p className="hero-kicker">Background work</p>
          <h3>{jobs.length} recent task{jobs.length === 1 ? "" : "s"} tracked.</h3>
          <p>
            This page refreshes itself every few seconds so you can see Deluno working without needing to reload.
          </p>
        </article>
        <article className="hero-card">
          <p className="hero-kicker">Recent updates</p>
          <div className="manifest-row">
            <strong>{activity.length} events</strong>
            <span>Simple updates about what Deluno has queued, started, finished, or needs attention on.</span>
          </div>
        </article>
      </div>
      <div className="workspace-grid">
        <article className="card">
          <div className="section-heading">
            <div>
              <h3>Task list</h3>
              <p>{jobs.length} recent background task{jobs.length === 1 ? "" : "s"}.</p>
            </div>
          </div>
          {jobs.length === 0 ? (
            <div className="empty-state">
              <p>Nothing queued right now. Once Deluno starts searching, importing, or upgrading, it will show up here.</p>
            </div>
          ) : (
            <div className="collection-list">
              {jobs.map((job) => (
                <article key={job.id} className="collection-item">
                  <div className="item-heading">
                    <strong>{formatJobType(job.jobType)}</strong>
                    <span className={`status-pill status-${job.status}`}>
                      {formatJobStatus(job.status)}
                    </span>
                  </div>
                  <div className="meta-row">
                    <span>{formatJobSource(job.source)}</span>
                    <span>{job.attempts} attempt{job.attempts === 1 ? "" : "s"}</span>
                  </div>
                  <div className="meta-row">
                    <span>{formatEntityType(job.relatedEntityType)}</span>
                    <span>{formatTimestamp(job.createdUtc)}</span>
                  </div>
                </article>
              ))}
            </div>
          )}
        </article>
        <article className="card">
          <div className="section-heading">
            <div>
              <h3>Recent updates</h3>
              <p>{activity.length} recent update{activity.length === 1 ? "" : "s"}.</p>
            </div>
          </div>
          {activity.length === 0 ? (
            <div className="empty-state">
              <p>No updates yet. Once Deluno does something useful, you will see it here.</p>
            </div>
          ) : (
            <div className="collection-list">
              {activity.map((event) => (
                <article key={event.id} className="collection-item">
                  <div className="item-heading">
                    <strong>{formatEventCategory(event.category)}</strong>
                    <span>{formatTimestamp(event.createdUtc)}</span>
                  </div>
                  <p>{event.message}</p>
                </article>
              ))}
            </div>
          )}
        </article>
      </div>
    </section>
  );
}

function formatTimestamp(value: string) {
  return new Date(value).toLocaleString();
}

function formatJobType(value: string) {
  switch (value) {
    case "movies.catalog.refresh":
      return "Check movie releases";
    case "series.catalog.refresh":
      return "Check TV show releases";
    default:
      return value;
  }
}

function formatJobStatus(value: string) {
  switch (value) {
    case "queued":
      return "Waiting";
    case "running":
      return "Working";
    case "completed":
      return "Done";
    case "failed":
      return "Needs attention";
    default:
      return value;
  }
}

function formatJobSource(value: string) {
  switch (value) {
    case "movies":
      return "Movies";
    case "series":
      return "TV Shows";
    default:
      return value;
  }
}

function formatEntityType(value: string | null) {
  switch (value) {
    case "movie":
      return "Movie";
    case "series":
      return "TV show";
    default:
      return "Library";
  }
}

function formatEventCategory(value: string) {
  switch (value) {
    case "job.queued":
      return "Added to the queue";
    case "job.started":
      return "Started";
    case "job.completed":
      return "Finished";
    case "job.failed":
      return "Needs attention";
    default:
      return value;
  }
}
