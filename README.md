# SiteGuardian

Agents de surveillance et correction pour **example.com** (Kajabi).
Voir le cadrage complet dans [PLAN-PROJET-AgentKajabi.md](PLAN-PROJET-AgentKajabi.md).

## État : Phase 0 — Squelette ✅

Le squelette est opérationnel : solution .NET (API + tests), workspace Angular,
persistance SQLite, workflows CI/CD.

> Versions installées ici : **.NET 10** et **Angular 19** (le plan visait .NET 8 /
> Angular 18+ ; ces versions plus récentes sont rétrocompatibles avec l'intention).

## Structure

```
├── PLAN-PROJET-AgentKajabi.md
├── SiteGuardian.slnx                # solution .NET (format XML moderne)
├── src/SiteGuardian.Api/            # ASP.NET Core : agents, MCP, PDF, SignalR (à venir)
│   ├── Models/                      # AuditJob, Finding, CorrectionRecord
│   ├── Data/                        # SiteGuardianDbContext (EF Core + SQLite)
│   └── Hubs/                        # ProgressHub (SignalR)
├── src/SiteGuardian.Api.Tests/      # xUnit
├── frontend/                        # Angular (mode démo pour GitHub Pages)
└── .github/workflows/               # ci.yml, deploy-pages.yml
```

## Lancer en local

### Backend (API)

```bash
dotnet run --project src/SiteGuardian.Api
# → http://localhost:5215  (santé : GET /api/health)
```

Point d'entrée CLI (stub Phase 0, implémenté en Phase 1 pour le cron d'audit) :

```bash
dotnet run --project src/SiteGuardian.Api -- audit
```

### Frontend

```bash
cd frontend
npm install        # première fois
npx ng serve       # → http://localhost:4200
```

## Tests

```bash
dotnet test SiteGuardian.slnx
cd frontend && npx ng test --watch=false
```

## Configuration

- `src/SiteGuardian.Api/appsettings.json` : ConnectionStrings, Cors, Agents (modèles
  Claude), Budget (plafonds), Kajabi (URL MCP).
- Secrets en dev via `dotnet user-secrets` (jamais dans le repo) — cf. §12 du plan.
- Front : `frontend/src/environments/` — `environment.ts` (dev, API réelle) et
  `environment.production.ts` (démo GitHub Pages, `apiBaseUrl` vide).

## Prochaine étape

Phase 1 — Audit + PDF (crawler, contrôles déterministes, contrôles LLM, QuestPDF).
