import { NavLink, Outlet, useLocation } from "react-router-dom";

const navItems = [
  {
    to: "/",
    label: "Home",
    caption: "Control room",
    end: true
  },
  {
    to: "/movies",
    label: "Movies",
    caption: "Film catalog"
  },
  {
    to: "/series",
    label: "Series",
    caption: "Episodic engine"
  },
  {
    to: "/activity",
    label: "Activity",
    caption: "Runtime ledger"
  },
  {
    to: "/settings",
    label: "Settings",
    caption: "Platform rules"
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
            <p>Media operations suite</p>
          </div>
        </div>
        <div className="sidebar-note">
          One host. Separate engines. Premium control over what enters your library.
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
          <p className="sidebar-footer-label">Architecture</p>
          <p className="sidebar-footer-copy">SQLite-first modular host</p>
          <p className="sidebar-footer-meta">Windows-ready and Docker-ready by design</p>
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
            <span className="shell-pill shell-pill-muted">single app</span>
            <span className="shell-pill shell-pill-muted">separate domains</span>
          </div>
        </div>
        <div className="shell-stage">
          <Outlet />
        </div>
      </main>
    </div>
  );
}
