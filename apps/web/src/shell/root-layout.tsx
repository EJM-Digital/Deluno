import { NavLink, Outlet, useLocation } from "react-router-dom";

const navItems = [
  {
    to: "/",
    label: "Overview",
    caption: "Your library",
    end: true
  },
  {
    to: "/movies",
    label: "Movies",
    caption: "Movies"
  },
  {
    to: "/series",
    label: "TV Shows",
    caption: "TV shows"
  },
  {
    to: "/activity",
    label: "Activity",
    caption: "What Deluno is doing"
  },
  {
    to: "/libraries",
    label: "Libraries",
    caption: "Separate setups"
  },
  {
    to: "/indexers",
    label: "Indexers",
    caption: "Sources and routing"
  },
  {
    to: "/settings",
    label: "Settings",
    caption: "Folders and apps"
  }
];

export function RootLayout() {
  const location = useLocation();
  const activeItem =
    navItems.find((item) =>
      item.end ? location.pathname === item.to : location.pathname.startsWith(item.to)
    ) ?? navItems[0];

  return (
    <div className="app-shell">
      <aside className="app-sidebar">
        <div className="brand-block">
          <span className="brand-mark" aria-hidden="true">
            <span className="brand-mark-orbit" />
            <span className="brand-mark-core">D</span>
          </span>
          <div>
            <h1>Deluno</h1>
            <p>Your media library, handled beautifully</p>
          </div>
        </div>
        <div className="sidebar-note">
          Manage movies and TV shows, keep searching for missing or better releases, and route everything through Deluno in one place.
        </div>
        <nav className="app-nav">
          {navItems.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.end}
              className={({ isActive }) =>
                isActive ? "nav-link nav-link-active" : "nav-link"
              }
            >
              <span className="nav-link-label">{item.label}</span>
              <span className="nav-link-caption">{item.caption}</span>
            </NavLink>
          ))}
        </nav>
        <div className="sidebar-footer">
          <p className="sidebar-footer-label">Designed For Home Users</p>
          <p className="sidebar-footer-copy">One app for movies and TV shows</p>
          <p className="sidebar-footer-meta">Simple to run on Windows, easy to ship in Docker, and built to replace split-tool setups</p>
        </div>
      </aside>
      <main className="app-main">
        <div className="shell-topbar">
          <div className="shell-topbar-copy">
            <p className="shell-kicker">{activeItem.caption}</p>
            <h2 className="shell-heading">{activeItem.label}</h2>
          </div>
          <div className="shell-topbar-pills" aria-hidden="true">
            <span className="shell-pill">Deluno</span>
            <span className="shell-pill shell-pill-muted">movies</span>
            <span className="shell-pill shell-pill-muted">tv shows</span>
          </div>
        </div>
        <div className="shell-stage">
          <Outlet />
        </div>
      </main>
    </div>
  );
}
