# Projet : SiteGuardian — Agents de surveillance et correction pour site hébergé (Kajabi)

> Fichier de cadrage à donner à Claude Code. Objectif : une appli C# avec un front web
> qui permet (1) de lancer un audit du site et générer le rapport PDF d'erreurs,
> (2) d'envoyer une correction en langage naturel, prévisualiser le changement, puis
> valider avant application via le MCP Kajabi.

---

## 1. Contexte

- Site cible : hébergé sur **Kajabi** (thème + pages + blog).
- Un audit manuel a déjà produit un rapport (`RapportSite.pdf`)
- Kajabi expose un **serveur MCP officiel** : `https://mcp.kajabi.com/mcp` (OAuth 2.1,
  inclus dans tous les plans). Outils clés : `get_theme_content`, `update_theme_content`,
  `list_blog_posts`, `update_blog_post`, `update_landing_page`, `list_navbars`,
  `update_navbar_link`, `list_website_pages`, `get_website_page`…
- ⚠️ `update_theme_content` s'applique **immédiatement en production** (pas de brouillon)
  → d'où l'exigence de prévisualisation + validation humaine dans l'appli.

## 2. Vue d'ensemble

```
┌─────────────────────┐        ┌──────────────────────────────┐
│ Front Angular       │  HTTP  │ Backend ASP.NET Core (.NET 8) │
│ (déployable GitHub  │───────▶│                              │
│  Pages, mode démo)  │  API   │  ┌─ Agent Surveillance ────┐ │
└─────────────────────┘        │  │ crawl + analyse LLM     │ │──▶ example.com (HTTP GET)
                               │  │ → rapport PDF           │ │
                               │  └─────────────────────────┘ │
                               │  ┌─ Agent Correction ──────┐ │──▶ Claude API (Haiku/Sonnet)
                               │  │ NL → plan → préview     │ │
                               │  │ → validation → écriture │ │──▶ MCP Kajabi (OAuth)
                               │  └─────────────────────────┘ │
                               │  SQLite (jobs, historique)   │
                               └──────────────────────────────┘
```

Principe : le **backend est le client MCP** (SDK officiel C#) et orchestre le LLM.
Le front ne contient aucun secret.

## 3. Stack technique

| Couche | Choix | Notes |
|---|---|---|
| Backend | ASP.NET Core 8 Web API (C#) | Minimal APIs ou Controllers |
| Client MCP | NuGet `ModelContextProtocol` | SDK officiel C# (Anthropic + Microsoft), transport Streamable HTTP |
| LLM | Claude API — NuGet `Anthropic.SDK` (communautaire) ou appels REST `api.anthropic.com` | Modèles : voir §5 |
| Crawl/parse | `AngleSharp` (ou HtmlAgilityPack) | parse HTML, liens, meta, lang, alt |
| PDF | `QuestPDF` | licence Community OK pour ce projet |
| Temps réel | SignalR | progression audit/correction en live |
| Persistance | SQLite + EF Core | jobs, historique corrections, tokens chiffrés |
| Front | Angular 18+ (standalone components) | `environment.apiBaseUrl` configurable |
| CI/démo | GitHub Actions → GitHub Pages | build Angular, mode démo avec données mockées |

## 4. Les deux agents

### 4.1 Agent Surveillance (audit)

- **Rôle** : crawler le site public (sitemap.xml puis fallback crawl des liens internes,
  limite ~60 pages), exécuter les contrôles, produire un JSON de findings puis le PDF.
- **Contrôles déterministes (sans LLM, en C#)** :
  - liens internes/externes cassés (404), liens non-HTTPS
  - `<html lang>` incorrect, title/h1 manquants ou multiples, titles trop courts
  - meta description absente/dupliquée, og:tags absents
  - images sans `alt`
  - texte par défaut Kajabi (« © Kajabi », « Join Our Free Trial », etc. — liste de patterns)
- **Contrôles LLM (Sonnet)** : fautes d'orthographe/typo en français, incohérences de ton,
  textes anglais résiduels. Envoyer le texte extrait par lot de pages, demander une
  sortie JSON structurée `{page, extrait, probleme, correction_proposee, severite}`.
- **Sortie** : `AuditReport` (JSON) + PDF généré avec QuestPDF, structure calquée sur
  RapportSite.pdf : Urgent / Important / À planifier, chaque finding avec
  « C'est quoi le problème / Pourquoi c'est important / Comment corriger ».
- **Dédoublonnage** : un même texte de thème (bandeau, footer) apparaît sur N pages
  → regrouper en un seul finding avec compteur de pages.

### 4.2 Agent Correction

- **Rôle** : transformer une instruction en langage naturel
  (« corrige accompagnement partout ») en un **plan d'actions MCP**, montrer une
  **prévisualisation avant/après**, attendre la **validation**, puis exécuter.
- **Boucle agentique** (tool-use standard) :
  1. Le backend liste les tools MCP Kajabi et les expose au modèle comme tools Claude.
  2. Phase **lecture seule** : le modèle n'a accès qu'aux tools `list_*`/`get_*`/`search_*`
     pour localiser le texte (theme content, blog posts, navbars, pages).
  3. Le modèle produit un `CorrectionPlan` JSON :
     ```json
     {
       "actions": [{
         "tool": "update_theme_content",
         "cible": "Announcement Bar — site theme",
         "avant": "…acccompagnement…",
         "apres": "…accompagnement…",
         "args": { }
       }],
       "resume": "1 remplacement dans le bandeau, visible sur 43 pages"
     }
     ```
  4. Le front affiche le diff avant/après → l'utilisateur **valide ou refuse**.
  5. Sur validation, le backend exécute les tools d'écriture **lui-même** (direct tool
     calling, sans repasser par le LLM) avec exactement les args validés.
  6. Vérification post-écriture : relire via `get_*` + re-crawler la/les pages publiques.
- **Garde-fous** :
  - jamais d'écriture MCP déclenchée directement par le LLM — l'écriture ne part que
    du plan validé par l'humain
  - whitelist de tools d'écriture autorisés (`update_theme_content`, `update_blog_post`,
    `update_landing_page`, `update_navbar_link`) — le reste est bloqué côté backend
  - journal complet en base (qui, quoi, avant/après, quand) → rollback manuel possible
    en réappliquant la valeur « avant »

## 5. Choix des modèles Claude

| Usage | Modèle | Justification |
|---|---|---|
| Agent Correction (localisation + plan) | `claude-haiku-4-5-20251001` | tool-use simple et ciblé ; rapide et peu cher — **à tester en premier** |
| Fallback correction si Haiku se trompe | `claude-sonnet-5` | escalade automatique si le plan échoue à la validation ou si >N tool calls |
| Agent Surveillance (analyse typos/ton) | `claude-sonnet-5` | qualité de détection en français plus importante que le coût (1 audit/semaine) |

Rendre le mapping configurable dans `appsettings.json` :
```json
"Agents": {
  "Correction": { "Model": "claude-haiku-4-5-20251001", "FallbackModel": "claude-sonnet-5", "MaxToolCalls": 15 },
  "Surveillance": { "Model": "claude-sonnet-5", "BatchPages": 5 }
}
```
Phase ultérieure : option LLM local (Ollama + modèle avec function calling) derrière la
même interface `ILlmProvider` — prévoir l'abstraction dès le départ.

## 6. Intégration MCP Kajabi

- URL : `https://mcp.kajabi.com/mcp` (Streamable HTTP).
- **OAuth 2.1** : flux d'autorisation initié depuis l'appli ; c'est le **gestionnaire du
  site** qui se connecte à son compte Kajabi et choisit les permissions
  (lecture + « Create and edit blog posts/pages » + « Edit theme content »).
  Stocker access/refresh tokens chiffrés (DataProtection API) côté backend.
- Les connexions inutilisées expirent → gérer le refresh et un état « reconnexion requise »
  visible dans le front.
- Premier appel de toute session : `list_sites` pour récupérer le `site_id`.
- Mapping corrections du rapport → tools :

| Correction du rapport | Tool MCP |
|---|---|
| « acccompagnement » (bandeau) | `get_theme_content` / `update_theme_content` |
| « © Kajabi » → « © Example » (footer) | `update_theme_content` |
| Lien YouTube cassé (footer) | `update_theme_content` ou `update_navbar_link` |
| Catégorie blog « finitiude », SEO articles | `update_blog_post` |
| Meta descriptions landing pages | `update_landing_page` |
| Pages website (SEO) | lecture seule pour l'instant (`get_website_page`) → signaler « correction manuelle » dans le rapport |

## 7. API Backend (endpoints)

```
POST   /api/audit                    → lance un audit (job id), progression via SignalR
GET    /api/audit/{id}               → statut + findings JSON
GET    /api/audit/{id}/pdf           → télécharge le rapport PDF
POST   /api/corrections              → { instruction: "corrige accompagnement partout" } → CorrectionPlan (préview)
POST   /api/corrections/{id}/apply   → exécute le plan validé
POST   /api/corrections/{id}/reject  → journalise le refus
GET    /api/corrections              → historique
GET    /api/kajabi/status            → connecté ? site_id ? permissions ?
GET    /api/kajabi/connect           → démarre l'OAuth Kajabi
```

## 8. Front Angular (3 écrans)

1. **Tableau de bord** : état connexion Kajabi, dernier audit (compteurs urgent/important),
   bouton « Lancer un audit », lien vers le PDF.
2. **Corrections** : champ texte libre (« Corrige-moi accompagnement partout »),
   liste des findings de l'audit avec bouton « Corriger » qui pré-remplit l'instruction.
   Après soumission : carte de **préview** avec diff avant/après (rouge/vert) et boutons
   **Valider** / **Refuser**. Après application : statut de vérification post-écriture.
3. **Historique** : corrections passées, valeurs avant/après, bouton « Restaurer avant ».

**Publication GitHub** : repo public ; workflow GitHub Actions qui build le front et le
déploie sur **GitHub Pages en mode démo** (`apiBaseUrl` vide → services mockés avec les
données du rapport, aucune écriture réelle). L'usage réel reste en local
(`dotnet run` sert l'API sur localhost, secrets dans user-secrets, jamais dans le repo).

## 9. Phases de développement

- **Phase 0 — Squelette** : solution `.sln` (Api + Tests), Angular workspace, SQLite,
  CI GitHub Actions (build + tests). Critère : `dotnet run` + `ng serve` fonctionnent.
- **Phase 1 — Audit + PDF** : crawler, contrôles déterministes, contrôles LLM (Sonnet),
  génération QuestPDF. Critère : le PDF reproduit ≥ 80 % des findings de RapportSite.pdf.
- **Phase 2 — Connexion MCP** : OAuth Kajabi, `list_sites`, `get_theme_content` en lecture.
  Critère : le bandeau contenant « acccompagnement » est localisé via MCP.
- **Phase 3 — Correction + préview** : boucle agentique Haiku, CorrectionPlan, diff,
  validation, écriture directe, vérification post-écriture. Critère : le scénario
  « corrige accompagnement partout » passe de bout en bout avec validation humaine.
- **Phase 4 — Front complet + GitHub Pages** : 3 écrans, SignalR, mode démo déployé.
- **Phase 5 (plus tard) — LLM local** : implémentation `ILlmProvider` Ollama ;
  audit planifié (cron hebdo) avec envoi du PDF par email.

## 10. Tests d'acceptation (issus du rapport réel)

1. « Corrige acccompagnement partout » → 1 action `update_theme_content`, diff correct, 43 pages impactées.
2. « Remplace © Kajabi par © Example dans le footer » → theme content, diff correct.
3. « Répare le lien YouTube du footer » → URL complète `https://www.youtube.com/@example-channel`.
4. « Corrige la catégorie finitiude » → `update_blog_post` sur les articles concernés.
5. Instruction ambiguë (« améliore le SEO ») → l'agent doit demander une précision, pas écrire.
6. Tool d'écriture hors whitelist demandé par le LLM → refus backend + log.

## 11. Stratégie coûts (exigences d'implémentation)

Objectif : < 1 €/mois d'API pour 4 audits hebdo + ~20 corrections. Pas d'abonnement :
clé API en crédits prépayés, avec plafond de dépense mensuel configuré dans la console.

### Règle d'or : le LLM en dernier recours

Ordre d'attaque pour chaque tâche, du gratuit au payant :

1. **Déterministe en C# (0 €)** : crawl, liens 404, meta manquantes, `lang`, alt,
   titles, patterns Kajabi par défaut. Doit couvrir ~70 % des findings sans LLM.
2. **Correcteur orthographique local (0 €)** : Hunspell FR (NuGet `WeCantSpell.Hunspell`)
   pré-filtre les fautes candidates. Le LLM ne valide que les candidats douteux,
   il ne relit jamais les pages entières.
3. **Audit incrémental** : hash SHA-256 du texte extrait de chaque page, stocké en base.
   D'une semaine sur l'autre, seules les pages dont le hash a changé repassent à
   l'analyse LLM. Premier audit = complet ; suivants = delta uniquement.
4. **LLM optimisé** quand il est vraiment nécessaire :
   - Haiku par défaut, Sonnet uniquement en escalade (cf. §5)
   - **Batch API** (−50 %) pour l'audit hebdo — pas de contrainte de latence
   - **prompt caching** sur le prompt système et les définitions de tools MCP (−90 %
     sur les tokens répétés entre appels)
5. **Exécution des corrections sans LLM (0 €)** : après validation humaine, le backend
   appelle les tools MCP directement avec les args validés (déjà spécifié §4.2).
6. **Vérification pré-prod sans LLM (0 €)** : relecture `get_*` + re-crawl de la page
   publique + comparaison de chaînes avec le résultat attendu. Pas de modèle.

### Garde-fous budgétaires dans le code

```json
"Budget": {
  "MaxTokensParCorrection": 50000,
  "MaxToolCallsParCorrection": 15,
  "MaxCoutEstimeParAuditEUR": 0.50,
  "AlerteSiCoutMensuelDepasseEUR": 3.00
}
```
- Compteur de tokens/coût par job, stocké en base, affiché dans le front (Historique).
- Si un plafond est atteint : arrêt propre du job + finding « analyse incomplète »,
  jamais de dépassement silencieux.

### Ordres de grandeur attendus (tarifs Haiku ≈ 1 $/M tokens entrée)

| Opération | Coût cible |
|---|---|
| Premier audit complet (49 pages) | 0,10 – 0,30 € |
| Audit hebdo incrémental (2-3 pages modifiées) | 0,02 – 0,05 € |
| Une correction (localisation + plan) | 0,01 – 0,03 € |
| Exécution + vérification | 0 € |
| **Total mensuel type** | **< 1 €** |

### Planification gratuite via GitHub Actions

L'audit hebdo tourne en **cron GitHub Actions** (gratuit, secrets chiffrés dans le repo) :
le workflow exécute l'audit en mode CLI (`dotnet run --project src/SiteGuardian.Api -- audit`),
génère le PDF, l'attache en artifact et l'envoie par email. Aucune machine locale à laisser
allumée ; le gestionnaire n'ouvre l'appli que pour valider des corrections.
→ Prévoir un point d'entrée CLI dans l'Api (audit sans serveur web) dès la Phase 1.

## 12. Sécurité / config

- Secrets : `dotnet user-secrets` en dev (`Anthropic:ApiKey`, tokens Kajabi chiffrés en base). Rien dans le repo.
- CORS restreint au front local + GitHub Pages (démo lecture seule).
- Budget : voir §11 (plafonds obligatoires, dépassement = arrêt propre du job).
- Le PDF et les logs ne contiennent aucune donnée de contacts Kajabi (ne pas accorder la
  permission contacts dans l'OAuth).

## 13. Démarrage dans Claude Code

```bash
claude
# puis :
# "Lis PLAN-PROJET-AgentKajabi.md et implémente la Phase 0, puis attends ma validation."
```

Brancher aussi le MCP Kajabi dans Claude Code pour explorer les tools pendant le dev :
```bash
claude mcp add kajabi --transport http https://mcp.kajabi.com/mcp
```

Structure de repo cible :
```
site-guardian/
├── PLAN-PROJET-AgentKajabi.md
├── src/SiteGuardian.Api/          # ASP.NET Core : agents, MCP client, PDF, SignalR
├── src/SiteGuardian.Api.Tests/
├── frontend/                      # Angular
└── .github/workflows/             # ci.yml, deploy-pages.yml
```
