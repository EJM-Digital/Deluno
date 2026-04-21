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
        <h2>Live operations surface</h2>
        <p className="page-copy">
          Jobs are durable in `jobs.db` and the worker now records queue, start,
          and completion events here.
        </p>
      </header>
      <div className="hero-grid hero-grid-tight">
        <article className="hero-card hero-card-feature">
          <p className="hero-kicker">Operations ledger</p>
          <h3>{jobs.length} recent jobs tracked.</h3>
          <p>
            This page revalidates automatically every few seconds so the queue
            feels alive while Deluno works in the background.
          </p>
        </article>
        <article className="hero-card">
          <p className="hero-kicker">Activity feed</p>
          <div className="manifest-row">
            <strong>{activity.length} events</strong>
            <span>Queue, start, completion, and failure records from `jobs.db`.</span>
          </div>
        </article>
      </div>
      <div className="workspace-grid">
        <article className="card">
          <div className="section-heading">
            <div>
              <h3>Job queue</h3>
              <p>{jobs.length} recent jobs from `jobs.db`.</p>
            </div>
          </div>
          {jobs.length === 0 ? (
            <div className="empty-state">
              <p>No jobs have been queued yet.</p>
            </div>
          ) : (
            <div className="collection-list">
              {jobs.map((job) => (
                <article key={job.id} className="collection-item">
                  <div className="item-heading">
                    <strong>{job.jobType}</strong>
                    <span className={`status-pill status-${job.status}`}>
                      {job.status}
                    </span>
                  </div>
                  <div className="meta-row">
                    <span>{job.source}</span>
                    <span>{job.attempts} attempt(s)</span>
                  </div>
                  <div className="meta-row">
                    <span>{job.relatedEntityType ?? "system"}</span>
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
              <h3>Activity feed</h3>
              <p>{activity.length} recent events.</p>
            </div>
          </div>
          {activity.length === 0 ? (
            <div className="empty-state">
              <p>No activity events yet.</p>
            </div>
          ) : (
            <div className="collection-list">
              {activity.map((event) => (
                <article key={event.id} className="collection-item">
                  <div className="item-heading">
                    <strong>{event.category}</strong>
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
