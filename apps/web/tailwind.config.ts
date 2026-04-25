import type { Config } from "tailwindcss";

const config: Config = {
  darkMode: ["class"],
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  theme: {
    screens: {
      xs: "360px",
      sm: "640px",
      md: "768px",
      lg: "1024px",
      xl: "1280px",
      "2xl": "1536px"
    },
    container: {
      center: true,
      padding: {
        DEFAULT: "1rem",
        md: "1.5rem",
        lg: "2rem"
      },
      screens: {
        "2xl": "1680px"
      }
    },
    extend: {
      fontFamily: {
        sans: [
          "Inter Variable",
          "Inter",
          "ui-sans-serif",
          "system-ui",
          "sans-serif"
        ],
        mono: [
          "JetBrains Mono Variable",
          "JetBrains Mono",
          "ui-monospace",
          "SFMono-Regular",
          "Menlo",
          "monospace"
        ]
      },
      borderRadius: {
        xl: "12px",
        "2xl": "16px"
      },
      maxWidth: {
        content: "1680px"
      },
      spacing: {
        topbar: "var(--topbar-height)",
        sidebar: "var(--sidebar-width)",
        "sidebar-rail": "var(--sidebar-rail)",
        "mobile-tabbar": "var(--mobile-tabbar-height)",
        "safe-bottom": "env(safe-area-inset-bottom)"
      },
      colors: {
        border: "hsl(var(--border))",
        input: "hsl(var(--input))",
        ring: "hsl(var(--ring))",
        background: "hsl(var(--background))",
        foreground: "hsl(var(--foreground))",
        primary: {
          DEFAULT: "hsl(var(--primary))",
          foreground: "hsl(var(--primary-foreground))",
          glow: "hsl(var(--primary-glow))",
          2: "hsl(var(--primary-2))",
          deep: "hsl(var(--primary-deep))"
        },
        secondary: {
          DEFAULT: "hsl(var(--secondary))",
          foreground: "hsl(var(--secondary-foreground))"
        },
        muted: {
          DEFAULT: "hsl(var(--muted))",
          foreground: "hsl(var(--muted-foreground))"
        },
        accent: {
          DEFAULT: "hsl(var(--accent))",
          foreground: "hsl(var(--accent-foreground))"
        },
        destructive: {
          DEFAULT: "hsl(var(--destructive))",
          foreground: "hsl(var(--destructive-foreground))"
        },
        success: {
          DEFAULT: "hsl(var(--success))",
          foreground: "hsl(var(--success-foreground))"
        },
        warning: {
          DEFAULT: "hsl(var(--warning))",
          foreground: "hsl(var(--warning-foreground))"
        },
        info: {
          DEFAULT: "hsl(var(--info))",
          foreground: "hsl(var(--info-foreground))"
        },
        card: {
          DEFAULT: "hsl(var(--card))",
          foreground: "hsl(var(--card-foreground))",
          elevated: "hsl(var(--card-elevated))"
        },
        popover: {
          DEFAULT: "hsl(var(--popover))",
          foreground: "hsl(var(--popover-foreground))"
        },
        surface: {
          1: "hsl(var(--surface-1))",
          2: "hsl(var(--surface-2))",
          3: "hsl(var(--surface-3))"
        },
        hairline: "hsl(var(--hairline))",
        sidebar: {
          DEFAULT: "hsl(var(--sidebar-background))",
          foreground: "hsl(var(--sidebar-foreground))",
          accent: "hsl(var(--sidebar-accent))",
          "accent-foreground": "hsl(var(--sidebar-accent-foreground))",
          border: "hsl(var(--sidebar-border))",
          ring: "hsl(var(--sidebar-ring))"
        },
        state: {
          ok: "hsl(var(--state-ok))",
          warn: "hsl(var(--state-warn))",
          danger: "hsl(var(--state-danger))",
          info: "hsl(var(--state-info))"
        }
      },
      boxShadow: {
        card: "var(--shadow-card)",
        md: "var(--shadow-md)",
        lg: "var(--shadow-lg)",
        glow: "var(--shadow-glow)",
        elevated: "var(--shadow-md)"
      },
      backgroundImage: {
        "gradient-accent": "var(--gradient-accent)",
        "gradient-surface": "var(--gradient-surface)"
      },
      keyframes: {
        shimmer: {
          "0%": { transform: "translateX(-120%)" },
          "100%": { transform: "translateX(120%)" }
        },
        "pulse-dot": {
          "0%, 100%": { opacity: "1", transform: "scale(1)" },
          "50%": { opacity: "0.55", transform: "scale(0.88)" }
        },
        "fade-in": {
          from: { opacity: "0", transform: "translateY(6px)" },
          to: { opacity: "1", transform: "translateY(0)" }
        },
        "slide-up": {
          from: { opacity: "0", transform: "translateY(12px)" },
          to: { opacity: "1", transform: "translateY(0)" }
        },
        "slide-down": {
          from: { opacity: "0", transform: "translateY(-8px)" },
          to: { opacity: "1", transform: "translateY(0)" }
        },
        "mobile-nav-sheet-in": {
          from: { opacity: "0", transform: "translate3d(-50%, 20px, 0) scale(0.97)" },
          to: { opacity: "1", transform: "translate3d(-50%, 0, 0) scale(1)" }
        },
        "mobile-nav-sheet-out": {
          from: { opacity: "1", transform: "translate3d(-50%, 0, 0) scale(1)" },
          to: { opacity: "0", transform: "translate3d(-50%, 16px, 0) scale(0.98)" }
        }
      },
      animation: {
        shimmer: "shimmer 1.9s linear infinite",
        "pulse-dot": "pulse-dot 1.8s ease-in-out infinite",
        "fade-in": "fade-in 160ms ease-out",
        "slide-up": "slide-up 180ms ease-out",
        "slide-down": "slide-down 160ms ease-out",
        "mobile-nav-sheet-in": "mobile-nav-sheet-in 0.32s cubic-bezier(0.2, 0.9, 0.22, 1) both",
        "mobile-nav-sheet-out": "mobile-nav-sheet-out 0.2s ease-in forwards"
      }
    }
  },
  plugins: []
};

export default config;
