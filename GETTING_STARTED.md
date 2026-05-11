# Deluno - Getting Started Guide

## Prerequisites

- **.NET 10 SDK** (or later)
- **Node.js 18+** (LTS recommended)
- **npm 9+** or **yarn**
- **Git** for version control
- **Visual Studio Code** or **Visual Studio 2022+** (optional but recommended)

## Quick Start

### 1. Environment Setup

```bash
# Navigate to the project root
cd /path/to/Deluno

# Verify .NET installation
dotnet --version  # Should be 10.0+

# Verify Node installation
node --version  # Should be 18+
npm --version   # Should be 9+
```

### 2. Install Dependencies

```bash
# Install backend dependencies (NuGet packages are restored automatically)
dotnet restore

# Install frontend dependencies
npm install

# Or if using specific workspace:
npm install --workspace=apps/web
```

### 3. Build the Application

```bash
# Build backend
dotnet build src/Deluno.Host/Deluno.Host.csproj

# Build frontend
npm run build --workspace=apps/web
```

### 4. Run Development Environment

#### Option A: Separate Terminal Windows

```bash
# Terminal 1: Backend
dotnet run --project src/Deluno.Host/Deluno.Host.csproj

# Terminal 2: Frontend (in apps/web directory)
npm run dev
```

#### Option B: Using npm scripts from root

```bash
# Start backend only
# (See scripts in root package.json for npm scripts)

# Then in another terminal, start frontend
npm run dev:web
```

### 5. Access the Application

- **Frontend:** http://localhost:5173
- **Backend API:** http://localhost:5099
- **Health Check:** http://localhost:5099/health

## Project Structure

```
Deluno/
├── src/
│   ├── Deluno.Host/              # Main ASP.NET Core application
│   ├── Deluno.Api/               # API endpoints
│   ├── Deluno.Movies/            # Movie operations & bulk endpoints
│   ├── Deluno.Series/            # Series operations & bulk endpoints
│   ├── Deluno.Jobs/              # Background job processing
│   ├── Deluno.Contracts/         # Shared data contracts
│   ├── Deluno.Infrastructure/    # Data access & persistence
│   ├── Deluno.Integrations/      # External service integrations
│   ├── Deluno.Platform/          # Core platform services
│   ├── Deluno.Realtime/          # SignalR configuration
│   └── Deluno.Filesystem/        # File system operations
│
├── apps/
│   └── web/
│       ├── src/
│       │   ├── routes/           # Page routes
│       │   ├── components/       # React components
│       │   │   ├── app/          # Application-specific components
│       │   │   │   └── library-view-with-bulk-ops.tsx (Phase 7)
│       │   │   ├── BulkOperationsPanel.tsx (Phase 6)
│       │   │   ├── BulkOperationsPanel.css (Phase 6)
│       │   │   └── shell/        # Layout & shell components
│       │   ├── lib/              # Utilities & types
│       │   ├── hooks/            # Custom React hooks
│       │   └── App.tsx           # Root component
│       ├── tests/
│       │   └── e2e/
│       │       ├── bulk-operations.spec.ts (Phase 6-7)
│       │       ├── movies-module.spec.ts
│       │       ├── tv-module.spec.ts
│       │       └── ... (other test files)
│       └── package.json
│
├── scripts/                      # Development scripts
├── .env.example                  # Environment variables template
└── IMPLEMENTATION_STATUS.md      # Progress tracking (Phase 7)
```

## Common Development Tasks

### Running Tests

```bash
# E2E tests (Playwright)
npm run test:smoke --workspace=apps/web

# Specific test file
npm run test:smoke --workspace=apps/web -- bulk-operations.spec.ts

# Watch mode (if supported)
npm run test:watch --workspace=apps/web
```

### Code Quality

```bash
# TypeScript type checking
npm run build --workspace=apps/web

# Linting (if configured)
npm run lint --workspace=apps/web

# Format code
npm run format --workspace=apps/web
```

### Database Management

```bash
# Migrations are automatically applied on startup
# To reset database:
# 1. Stop the application
# 2. Delete the data directory
# 3. Restart the application

# Manual database reset
rm -rf ~/.deluno/data  # On Linux/Mac
rmdir %APPDATA%\.deluno\data /s  # On Windows
```

### API Development

```bash
# Add a new endpoint in src/Deluno.{Module}/EndpointRouteBuilderExtensions.cs
# Example: Deluno.Movies/MoviesEndpointRouteBuilderExtensions.cs

// Add route:
app.MapPost("/api/movies/bulk", HandleBulkOperation)
   .WithName("BulkMovieOperation")
   .WithOpenApi();
```

### Frontend Development

```bash
# Add a new page in apps/web/src/routes/
# Add route to router configuration

// Import and add to router
import { NewPage, newPageLoader } from "./routes/new-page";

// In route definition
{
  path: "/new-page",
  element: <NewPage />,
  loader: newPageLoader
}
```

## Configuration

### Environment Variables

Create `.env` file in root directory:

```env
# Backend
ASPNETCORE_ENVIRONMENT=Development
ASPNETCORE_URLS=http://localhost:5099

# Frontend
VITE_API_URL=http://localhost:5099

# Database
Storage__DataRoot=~/.deluno
```

### Application Settings

Backend settings in `appsettings.Development.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

## Troubleshooting

### Port Already in Use

```bash
# Find process using port 5173 (frontend)
lsof -i :5173  # macOS/Linux
netstat -ano | findstr :5173  # Windows

# Kill process
kill -9 <PID>  # macOS/Linux
taskkill /PID <PID> /F  # Windows
```

### Database Locked

```bash
# SQLite sometimes locks during development
# Solution: Restart the application
# Or check for zombie processes

ps aux | grep dotnet  # macOS/Linux
tasklist | findstr dotnet  # Windows
```

### Build Failures

```bash
# Clean and rebuild
dotnet clean src/Deluno.Host/Deluno.Host.csproj
dotnet build src/Deluno.Host/Deluno.Host.csproj

# Frontend
rm -rf apps/web/node_modules apps/web/.vite
npm install --workspace=apps/web
npm run build --workspace=apps/web
```

### Test Failures

```bash
# Check test configuration
cat apps/web/playwright.config.ts

# Run tests with debug output
npm run test:smoke --workspace=apps/web -- --debug

# Update snapshots if needed
npm run test:smoke --workspace=apps/web -- --update-snapshots
```

## Feature Status by Phase

| Phase | Feature | Status | Notes |
|-------|---------|--------|-------|
| 1 | Backend + Frontend | ✅ Complete | Full stack operational |
| 2 | Tech Debt | ✅ Complete | Code quality improved |
| 3 | Real-Time Events | ✅ Complete | SignalR integrated |
| 4 | Retry Service | ✅ Complete | Exponential backoff |
| 5 | Error Handling | ✅ Complete | ErrorAlert component |
| 6 | Bulk Operations | ✅ Complete | POST /api/*/bulk endpoints |
| 7 | UI Integration | ✅ Complete | LibraryViewWithBulkOps |
| 8 | UI Backlog | 🔄 Next | Enhanced filtering, sorting |
| 9+ | Extended Features | ⏳ Planned | Advanced search, automation |

## Performance Tips

### Frontend
1. Use React DevTools to check re-render performance
2. Enable browser throttling in DevTools for mobile testing
3. Clear browser cache when testing CSS changes
4. Use `npm run build` to see optimized bundle size

### Backend
1. Monitor SQL queries with logging
2. Check database file size: `du -h ~/.deluno/data`
3. Use dotnet built-in profiling for performance analysis
4. Monitor memory usage during bulk operations

## Next Steps

1. **Review Documentation**
   - Read IMPLEMENTATION_STATUS.md for current progress
   - Check ARCHITECTURE_OVERVIEW.md for system design
   - See PHASE_8_GUIDELINES.md for upcoming work

2. **Run Tests**
   - Execute `npm run test:smoke` to verify setup
   - Check test results for any failures

3. **Explore Codebase**
   - Review Phase 6 bulk operations in:
     - Backend: `src/Deluno.Movies/MoviesEndpointRouteBuilderExtensions.cs`
     - Frontend: `apps/web/src/components/BulkOperationsPanel.tsx`
   - Check Phase 7 integration in:
     - `apps/web/src/components/app/library-view-with-bulk-ops.tsx`
     - `apps/web/src/routes/library-page.tsx`

4. **Begin Phase 8**
   - Start implementing UI backlog features
   - Follow guidelines in PHASE_8_GUIDELINES.md
   - Reference ARCHITECTURE_OVERVIEW.md for patterns

## Support & Debugging

### Enable Verbose Logging

```bash
# Backend verbose logging
export LOGLEVEL=Debug
dotnet run --project src/Deluno.Host/Deluno.Host.csproj

# Frontend debug mode
VITE_DEBUG=true npm run dev --workspace=apps/web
```

### Browser Developer Tools

1. **React DevTools**: Inspect component hierarchy
2. **Network Tab**: Monitor API calls
3. **Application Tab**: Check localStorage/IndexedDB
4. **Console**: Check for JavaScript errors

### Backend Debugging

```bash
# Visual Studio Code launch configuration (.vscode/launch.json)
{
  "name": ".NET Core Launch",
  "type": "coreclr",
  "request": "launch",
  "program": "${workspaceFolder}/src/Deluno.Host/bin/Debug/net10.0/Deluno.Host.dll",
  "args": [],
  "cwd": "${workspaceFolder}",
  "stopAtEntry": false,
  "console": "internalConsole"
}
```

## Additional Resources

- [.NET 10 Documentation](https://learn.microsoft.com/dotnet/)
- [React Documentation](https://react.dev)
- [TypeScript Handbook](https://www.typescriptlang.org/docs/)
- [Playwright Testing](https://playwright.dev/)
- [Tailwind CSS](https://tailwindcss.com/)

---

**Last Updated:** May 10, 2026  
**Maintained By:** Claude Agent  
**Status:** Production Ready for Phases 1-7
