import { NavLink, Outlet } from "react-router-dom";

const navItems = [
  { to: "/", label: "Home", end: true },
  { to: "/movies", label: "Movies" },
  { to: "/series", label: "Series" },
  { to: "/activity", label: "Activity" },
  { to: "/settings", label: "Settings" }
];

export function RootLayout() {
  return (
    <div className="app-shell">
      <aside className="app-sidebar">
        <div className="brand-block">
          <span className="brand-mark">D</span>
          <div>
            <h1>Deluno</h1>
            <p>Unified media automation</p>
          </div>
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
              {item.label}
            </NavLink>
          ))}
        </nav>
      </aside>
      <main className="app-main">
        <Outlet />
      </main>
    </div>
  );
}

