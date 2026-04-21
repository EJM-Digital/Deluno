const connections = [
  {
    name: "Radarr Main",
    role: "Movies / Main",
    summary: "Bring in an existing main movie library without losing the setup people already trust."
  },
  {
    name: "Radarr 4K",
    role: "Movies / 4K",
    summary: "Support split 4K setups cleanly instead of pretending power users do not exist."
  },
  {
    name: "Sonarr Main",
    role: "TV Shows / Main",
    summary: "Map regular TV into a Deluno library and keep the migration path gentle."
  },
  {
    name: "Sonarr Anime",
    role: "TV Shows / Anime",
    summary: "Give anime and special-case TV setups a proper home without crowding the main library."
  }
];

export function ConnectionsPage() {
  return (
    <section className="page-stack">
      <header className="page-header">
        <p className="eyebrow">Connections</p>
        <h2>Bring your existing setup with you</h2>
        <p className="page-copy">
          Deluno should support clean migration and companion mode, so people can connect the Radarr and Sonarr libraries they already run while moving toward a better single-app experience.
        </p>
      </header>
      <div className="hero-grid hero-grid-tight">
        <article className="hero-card hero-card-feature">
          <p className="hero-kicker">Migration first</p>
          <h3>Users should not have to rebuild everything by hand.</h3>
          <p>
            Connections will eventually handle testing, importing, and mapping existing apps into Deluno libraries, with a clear picture of what is healthy, what is missing, and what still needs attention.
          </p>
        </article>
        <article className="hero-card">
          <p className="hero-kicker">What each connection should show</p>
          <div className="manifest-grid">
            <div className="manifest-row">
              <strong>Health</strong>
              <span>Can Deluno reach it right now and is the API working?</span>
            </div>
            <div className="manifest-row">
              <strong>Mapped library</strong>
              <span>Which Deluno library the connection belongs to.</span>
            </div>
            <div className="manifest-row">
              <strong>Import scope</strong>
              <span>Profiles, folders, and library items Deluno can bring across safely.</span>
            </div>
          </div>
        </article>
      </div>
      <article className="card">
        <div className="section-heading">
          <div>
            <h3>Planned connection types</h3>
            <p>Deluno should support more than one Radarr or Sonarr connection without the app becoming confusing.</p>
          </div>
        </div>
        <div className="collection-list">
          {connections.map((connection) => (
            <article key={connection.name} className="collection-item">
              <div className="item-heading">
                <strong>{connection.name}</strong>
                <span>{connection.role}</span>
              </div>
              <p>{connection.summary}</p>
            </article>
          ))}
        </div>
      </article>
    </section>
  );
}
