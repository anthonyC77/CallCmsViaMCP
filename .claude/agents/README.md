# Site Audit Swarm

A swarm of 12 Claude Code subagents that audit a website across every major technical dimension — links, content, performance, mobile, accessibility, SEO, AI-crawler discoverability, security, code standards, internationalization, and analytics — and produce a single prioritized report.

## Architecture

```
               [liste de sites]          [site unique]
                      |                       |
             prospect-auditor          audit-orchestrator
                      |                       |
           rapport de prospection   +----------+----------+
           (score + top 3 issues)   |          |          |
                                  links    content    perf
                                    |          |          |
                                  mobile     a11y      seo
                                    |          |          |
                                ai-crawler security   code
                                    |          |
                                  i18n     analytics
```

The **orchestrator** has two modes:
- **Prospecting mode** — delegates to `prospect-auditor` when the user provides a list of sites. Returns a scored ranking of prospects with their top issues.
- **Full audit mode** — dispatches the 11 specialists in parallel, collects their structured findings, deduplicates overlap (e.g., a missing `alt` is both A11y and SEO), then ranks every finding by severity and effort to produce a phased remediation roadmap.

## The agents

| Agent | Mode | Domain | Primary tool |
|---|---|---|---|
| `audit-orchestrator` | Both | Coordination + routing | `Task` (dispatch) |
| `prospect-auditor` | **Prospecting** | Surface scan of a site list — scores prospects by improvement potential | `curl` via Bash |
| `links-navigation-auditor` | Full audit | 404s, redirect chains, broken anchors, expired externals | `curl` via Bash |
| `content-quality-auditor` | Full audit | Spelling, French typography, duplicates, Lorem ipsum | LanguageTool API |
| `performance-auditor` | Full audit | Core Web Vitals, image/JS/CSS optimization, cache, compression | PageSpeed Insights API |
| `mobile-responsive-auditor` | Full audit | Viewport, touch targets, overflow, mobile forms, `srcset` | Static HTML + PSI |
| `accessibility-auditor` | Full audit | WCAG 2.1/2.2 AA — alt, contrast, headings, labels, focus | PageSpeed Insights (a11y) |
| `seo-auditor` | Full audit | Titles, meta, H1, canonicals, sitemap, OG, schema.org, hreflang | PageSpeed Insights (SEO) |
| `ai-crawler-auditor` | Full audit | `llms.txt`, AI-bot policy, SSR coverage, semantic HTML | Direct fetch |
| `security-auditor` | Full audit | HTTPS/TLS, security headers, deps, CSRF, exposed secrets | `curl -I`, `openssl` |
| `code-standards-auditor` | Full audit | W3C HTML, deprecated tags, console errors, mixed content, PWA | W3C Nu validator API |
| `i18n-auditor` | Full audit | `lang`, charset, localized dates/currencies, untranslated leaks | Static HTML inspection |
| `analytics-monitoring-auditor` | Full audit | Trackers, conversions, consent gating, error tracking | Static HTML inspection |

## Installing the swarm

The agents in this folder are written in the **Claude Code subagent format** (Markdown with YAML frontmatter). To make them callable, copy or move them into one of:

- `.claude/agents/` at the project root (project-scoped, shared via git)
- `~/.claude/agents/` (user-scoped, available across all projects)

```bash
# Project-scoped install
mkdir -p .claude/agents
cp agents/*.md .claude/agents/

# OR user-scoped install
mkdir -p ~/.claude/agents
cp agents/*.md ~/.claude/agents/
```

Once installed, each agent can be invoked by name via the `Task` tool, and the orchestrator will pick them up automatically.

## Running an audit

### Full audit (single site)

In a Claude Code session (or Cowork chat), simply ask:

> Run a full audit on https://example.com

The orchestrator will:

1. Confirm scope (single page vs crawl, languages, stack, constraints).
2. Dispatch all 11 specialists in parallel.
3. Wait for findings, deduplicate, prioritize.
4. Write `audit-report-<domain>-<YYYY-MM-DD>.md` to the workspace.

You can also call any specialist directly. For example:

> Use the `performance-auditor` on https://example.com/pricing

### Prospecting (list of sites)

To identify which sites in a list would most benefit from a full audit report:

> Prospecte cette liste de sites : site1.fr, site2.com, site3.fr

Or with a file:

> Prospecte les sites dans ma liste `prospects.csv`

The orchestrator will delegate to `prospect-auditor`, which will:

1. Scan each site's homepage (fast surface checks, no deep crawl).
2. Score each site from 0 to 100 by improvement potential.
3. Produce a ranked prospecting report with the top 3 issues per site.
4. Write `prospect-report-<YYYY-MM-DD>.md` to the workspace.

## Output schema

Every specialist returns findings in a common JSON shape so the orchestrator can merge them mechanically:

```json
{
  "agent": "performance-auditor",
  "target": "https://example.com",
  "summary": "Mobile LCP is 5.1s, dominated by an unoptimized hero image.",
  "score": { "performance_mobile": 38, "performance_desktop": 71 },
  "findings": [
    {
      "id": "perf-lcp-hero-image",
      "title": "Hero image is 1.8 MB JPEG, no width/height, no AVIF/WebP",
      "severity": "critical",
      "category": "images",
      "evidence": {
        "metric": "LCP",
        "value": "5100ms",
        "threshold": "2500ms",
        "resource_url": "/static/hero.jpg",
        "savings_kb": 1450
      },
      "impact": "Single largest contributor to mobile LCP failing CWV threshold.",
      "fix": "Serve AVIF/WebP via <picture>, set explicit width/height, add fetchpriority=\"high\".",
      "effort": "low"
    }
  ]
}
```

## Optional configuration

A few agents can be enhanced with API keys, but all work without:

- `PAGESPEED_API_KEY` — raises PSI rate limits (Performance / Accessibility / SEO agents).
- `LANGUAGETOOL_API_KEY` — raises LanguageTool quota (Content quality agent).

If you have these, expose them as environment variables in the session before running an audit.

## Extending the swarm

To add a new specialist:

1. Drop a new `.md` file in `.claude/agents/` with the standard frontmatter.
2. Add the agent name to `audit-orchestrator.md`'s dispatch list.
3. Make sure your agent returns the same findings JSON schema so the orchestrator can merge it.

To narrow the swarm for quick checks, ask the orchestrator to run only a subset, e.g. "run perf + a11y only".
