const libraries = [
  {
    name: "Movies / Main",
    type: "Movies",
    purpose: "Everyday library",
    summary: "Use this for the main movie library most people watch every day.",
    status: "Planned"
  },
  {
    name: "Movies / 4K",
    type: "Movies",
    purpose: "Premium versions",
    summary: "Keep 4K rules, folders, and upgrades separate without needing a second app.",
    status: "Planned"
  },
  {
    name: "TV Shows / Main",
    type: "TV Shows",
    purpose: "General shows",
    summary: "Track normal TV releases with their own folders, schedules, and release rules.",
    status: "Planned"
  },
  {
    name: "TV Shows / Anime",
    type: "TV Shows",
    purpose: "Anime and specials",
    summary: "Handle anime, specials, and harder TV matching separately when the rules need to differ.",
    status: "Planned"
  }
];

export function LibrariesPage() {
  return (
    <section className="page-stack">
      <header className="page-header">
        <p className="eyebrow">Libraries</p>
        <h2>Separate libraries, one clean app</h2>
        <p className="page-copy">
          This is where Deluno will replace the mess of running extra Radarr and Sonarr instances just to keep different libraries apart.
        </p>
      </header>
      <div className="hero-grid">
        <article className="hero-card hero-card-feature">
          <p className="hero-kicker">Why this matters</p>
          <h3>HD, 4K, anime, kids, and custom setups should feel normal.</h3>
          <p>
            Deluno should let each library have its own folders, release rules, schedules, and download behavior without turning the whole app into an expert-only dashboard.
          </p>
        </article>
        <article className="hero-card">
          <p className="hero-kicker">What each library will own</p>
          <div className="manifest-grid">
            <div className="manifest-row">
              <strong>Folders</strong>
              <span>Each library gets its own root path and import destination.</span>
            </div>
            <div className="manifest-row">
              <strong>Release rules</strong>
              <span>Quality targets, upgrade goals, and size or language preferences stay separate.</span>
            </div>
            <div className="manifest-row">
              <strong>Schedules</strong>
              <span>Missing and upgrade searches can run on different timings for each library.</span>
            </div>
          </div>
        </article>
      </div>
      <div className="card-grid">
        {libraries.map((library) => (
          <article key={library.name} className="card">
            <h3>{library.name}</h3>
            <p>{library.summary}</p>
            <div className="manifest-grid">
              <div className="manifest-row">
                <strong>{library.type}</strong>
                <span>{library.purpose}</span>
              </div>
            </div>
            <span className="inline-badge">{library.status}</span>
          </article>
        ))}
      </div>
    </section>
  );
}
