#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Web Audit Agent
===============
Agent autonome qui parcourt le web et detecte des sites presentant des
soucis de Performance, SEO, Securite et Accessibilite.

Usage :
    python web_audit_agent.py "restaurants paris" --limit 10 --workers 5
    python web_audit_agent.py --urls https://a.com,https://b.fr --html r.html
    python web_audit_agent.py "agence web lyon" --limit 5 \\
        --csv r.csv --json r.json --html r.html

Dependances :
    pip install requests beautifulsoup4
"""
from __future__ import annotations
import argparse, csv, html, json, re, socket, ssl, sys, time
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import asdict, dataclass, field
from datetime import datetime
from typing import List, Optional, Tuple
from urllib.parse import parse_qs, urljoin, urlparse

try:
    import requests
    from bs4 import BeautifulSoup
except ImportError:
    print("Dependances manquantes. Installez : pip install requests beautifulsoup4")
    sys.exit(1)

USER_AGENT = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
              "(KHTML, like Gecko) Chrome/124.0 Safari/537.36 WebAuditAgent/1.1")
TIMEOUT = 15
LINK_CHECK_TIMEOUT = 8
MAX_PAGE_BYTES = 5 * 1024 * 1024
MAX_LINKS_TO_CHECK = 20
SLOW_RESPONSE_MS = 1500
LARGE_PAGE_KB = 2048
SECURITY_HEADERS = [
    "strict-transport-security", "content-security-policy", "x-frame-options",
    "x-content-type-options", "referrer-policy", "permissions-policy",
]

class C:
    RESET="\033[0m"; BOLD="\033[1m"; DIM="\033[2m"
    RED="\033[31m"; GREEN="\033[32m"; YELLOW="\033[33m"
    BLUE="\033[34m"; MAGENTA="\033[35m"; CYAN="\033[36m"
    @classmethod
    def disable(cls):
        for k, v in list(cls.__dict__.items()):
            if isinstance(v, str) and v.startswith("\033"):
                setattr(cls, k, "")

@dataclass
class AuditResult:
    url: str
    final_url: str = ""
    status_code: int = 0
    score: int = 100
    grade: str = ""
    issues: List[str] = field(default_factory=list)
    warnings: List[str] = field(default_factory=list)
    response_time_ms: int = 0
    page_size_kb: int = 0
    resource_count: int = 0
    images_count: int = 0
    images_without_alt: int = 0
    title: str = ""
    title_length: int = 0
    meta_description: str = ""
    meta_description_length: int = 0
    h1_count: int = 0
    has_canonical: bool = False
    has_lang: bool = False
    has_viewport: bool = False
    has_og_tags: bool = False
    robots_txt: bool = False
    sitemap_xml: bool = False
    https: bool = False
    redirects_to_https: bool = False
    ssl_valid: bool = False
    ssl_days_left: Optional[int] = None
    security_headers_present: List[str] = field(default_factory=list)
    security_headers_missing: List[str] = field(default_factory=list)
    a11y_issues: List[str] = field(default_factory=list)
    inputs_without_label: int = 0
    buttons_without_name: int = 0
    links_without_text: int = 0
    heading_skips: int = 0
    links_checked: int = 0
    broken_links: List[str] = field(default_factory=list)
    error: str = ""


def search_duckduckgo(query: str, limit: int = 10) -> List[str]:
    print(f"{C.CYAN}Recherche DuckDuckGo : '{query}'{C.RESET}")
    try:
        resp = requests.post("https://html.duckduckgo.com/html/",
            headers={"User-Agent": USER_AGENT}, data={"q": query}, timeout=TIMEOUT)
        resp.raise_for_status()
    except requests.RequestException as exc:
        print(f"{C.RED}  Erreur recherche : {exc}{C.RESET}")
        return []
    soup = BeautifulSoup(resp.text, "html.parser")
    seen: set = set(); results: List[str] = []
    blacklist = ("duckduckgo.com", "wikipedia.org", "facebook.com", "twitter.com",
        "x.com", "instagram.com", "youtube.com", "linkedin.com", "tiktok.com",
        "pinterest.", "amazon.", "ebay.")
    for link in soup.select("a.result__a, a.result__url"):
        href = (link.get("href") or "").strip()
        if not href: continue
        if "uddg=" in href:
            qs = parse_qs(urlparse(href).query)
            href = qs.get("uddg", [href])[0]
        if not href.startswith("http"):
            href = "https://" + href.lstrip("/")
        parsed = urlparse(href); domain = parsed.netloc.lower()
        if not domain or domain in seen or any(b in domain for b in blacklist):
            continue
        seen.add(domain)
        results.append(f"{parsed.scheme}://{domain}")
        if len(results) >= limit: break
    print(f"{C.GREEN}  {len(results)} site(s) trouve(s){C.RESET}\n")
    return results


def check_ssl_certificate(host: str) -> Tuple[bool, Optional[int]]:
    try:
        ctx = ssl.create_default_context()
        with socket.create_connection((host, 443), timeout=TIMEOUT) as sock:
            with ctx.wrap_socket(sock, server_hostname=host) as ssock:
                cert = ssock.getpeercert()
                expires = datetime.strptime(cert["notAfter"], "%b %d %H:%M:%S %Y %Z")
                return True, (expires - datetime.utcnow()).days
    except Exception:
        return False, None


def check_one_link(url: str) -> Tuple[str, int]:
    try:
        r = requests.head(url, headers={"User-Agent": USER_AGENT},
            timeout=LINK_CHECK_TIMEOUT, allow_redirects=True)
        if r.status_code in (405, 501):
            r = requests.get(url, headers={"User-Agent": USER_AGENT},
                timeout=LINK_CHECK_TIMEOUT, allow_redirects=True, stream=True)
            r.close()
        return url, r.status_code
    except requests.Timeout:
        return url, -1
    except requests.RequestException:
        return url, 0


def check_broken_links(base_url: str, soup: BeautifulSoup,
                       max_links: int = MAX_LINKS_TO_CHECK,
                       workers: int = 8) -> Tuple[int, List[str]]:
    base_host = urlparse(base_url).netloc
    candidates: List[str] = []; seen: set = set()
    for a in soup.find_all("a", href=True):
        href = a["href"].strip()
        if not href or href.startswith(("#", "mailto:", "tel:", "javascript:")):
            continue
        absolute = urljoin(base_url, href).split("#")[0]
        if urlparse(absolute).netloc != base_host: continue
        if absolute in seen: continue
        seen.add(absolute); candidates.append(absolute)
        if len(candidates) >= max_links: break
    broken: List[str] = []
    if not candidates: return 0, broken
    with ThreadPoolExecutor(max_workers=workers) as ex:
        for url, code in ex.map(check_one_link, candidates):
            if code == 0 or code == -1 or code >= 400:
                label = "timeout" if code == -1 else ("erreur" if code == 0 else str(code))
                broken.append(f"{url} ({label})")
    return len(candidates), broken


def check_accessibility(soup: BeautifulSoup, result: AuditResult) -> None:
    inputs_no_label = 0
    for inp in soup.find_all(["input", "select", "textarea"]):
        itype = (inp.get("type") or "").lower()
        if itype in ("hidden", "submit", "button", "reset", "image"): continue
        if inp.get("aria-label") or inp.get("aria-labelledby") or inp.get("title"):
            continue
        input_id = inp.get("id")
        if input_id and soup.find("label", attrs={"for": input_id}): continue
        if inp.find_parent("label"): continue
        inputs_no_label += 1
    result.inputs_without_label = inputs_no_label

    buttons_no_name = 0
    for b in soup.find_all("button"):
        if not (b.get_text() or "").strip() and not b.get("aria-label") and not b.get("title"):
            buttons_no_name += 1
    result.buttons_without_name = buttons_no_name

    links_no_text = 0
    for a in soup.find_all("a", href=True):
        if (a.get_text() or "").strip(): continue
        if a.get("aria-label") or a.get("title"): continue
        img = a.find("img")
        if img and (img.get("alt") or "").strip(): continue
        links_no_text += 1
    result.links_without_text = links_no_text

    levels = [int(h.name[1]) for h in soup.find_all(re.compile(r"^h[1-6]$"))]
    skips = 0; prev = 0
    for lvl in levels:
        if prev and lvl > prev + 1: skips += 1
        prev = lvl
    result.heading_skips = skips

    if inputs_no_label > 0:
        msg = f"{inputs_no_label} champ(s) de formulaire sans label associe"
        (result.issues if inputs_no_label > 3 else result.warnings).append(msg)
        result.a11y_issues.append(msg)
    if buttons_no_name > 0:
        msg = f"{buttons_no_name} bouton(s) sans nom accessible"
        (result.issues if buttons_no_name > 2 else result.warnings).append(msg)
        result.a11y_issues.append(msg)
    if links_no_text > 0:
        msg = f"{links_no_text} lien(s) sans texte accessible"
        (result.issues if links_no_text > 5 else result.warnings).append(msg)
        result.a11y_issues.append(msg)
    if skips > 0:
        msg = f"{skips} saut(s) dans la hierarchie des titres"
        result.warnings.append(msg); result.a11y_issues.append(msg)


# ---------------------------------------------------------------------------
# Expansion psychologue.net : listing -> profils -> sites externes
# ---------------------------------------------------------------------------

PSY_NET_HOST = "www.psychologue.net"


def _fetch_html(url: str) -> Optional[str]:
    try:
        r = requests.get(url, headers={"User-Agent": USER_AGENT},
                         timeout=TIMEOUT, allow_redirects=True)
        if r.status_code != 200:
            return None
        return r.text
    except requests.RequestException:
        return None


def expand_psychologuenet_listing(listing_url: str, max_profiles: int = 30) -> List[str]:
    """A partir d'une URL de listing psychologue.net, renvoie les URLs de profils."""
    html_text = _fetch_html(listing_url)
    if not html_text:
        return []
    soup = BeautifulSoup(html_text, "html.parser")
    profiles: List[str] = []
    seen: set = set()
    for a in soup.find_all("a", href=True):
        href = a["href"].strip()
        m = re.match(r"^(?:https://www\.psychologue\.net)?(/cabinets/[a-z0-9\-]+)$", href, re.I)
        if not m:
            continue
        path = m.group(1)
        # On exclut les categories generiques /cabinets/anxiete, /cabinets/toulouse, etc.
        # Heuristique : les profils contiennent au moins un tiret (prenom-nom)
        slug = path.rsplit("/", 1)[-1]
        if "-" not in slug:
            continue
        full = f"https://{PSY_NET_HOST}{path}"
        if full in seen:
            continue
        seen.add(full)
        profiles.append(full)
        if len(profiles) >= max_profiles:
            break
    return profiles


def extract_practitioner_site(profile_url: str) -> Optional[str]:
    """Recupere l'URL du site web personnel depuis un profil psychologue.net."""
    html_text = _fetch_html(profile_url)
    if not html_text:
        return None
    soup = BeautifulSoup(html_text, "html.parser")
    # 1) Lien avec title contenant 'Site web de'
    for a in soup.find_all("a", href=True):
        title = (a.get("title") or "")
        if "site web de" in title.lower():
            return a["href"].strip()
    # 2) Lien dont le texte est 'Visiter le site web'
    for a in soup.find_all("a", href=True):
        text = (a.get_text() or "").strip().lower()
        if "visiter le site web" in text:
            return a["href"].strip()
    return None


def expand_psychologuenet_targets(urls: List[str], max_profiles: int = 30,
                                   workers: int = 6) -> List[str]:
    """Transforme une liste d'URLs psychologue.net en liste de sites externes."""
    profile_urls: List[str] = []
    for u in urls:
        if "/search" in u or "/cabinets/" not in u:
            print(f"{C.CYAN}  Listing detecte : extraction des profils...{C.RESET}")
            profile_urls.extend(expand_psychologuenet_listing(u, max_profiles=max_profiles))
        else:
            profile_urls.append(u)
    # Dedupe en preservant l'ordre
    seen: set = set(); ordered: List[str] = []
    for u in profile_urls:
        if u not in seen:
            seen.add(u); ordered.append(u)
    print(f"{C.CYAN}  {len(ordered)} profil(s) a inspecter pour trouver leur site externe...{C.RESET}")
    sites: List[str] = []
    seen_sites: set = set()
    with ThreadPoolExecutor(max_workers=workers) as ex:
        for site in ex.map(extract_practitioner_site, ordered):
            if not site:
                continue
            # Nettoyage : on garde le domaine + chemin de base
            if not site.startswith("http"):
                site = "https://" + site.lstrip("/")
            host = urlparse(site).netloc.lower()
            # On garde uniquement les sites HORS psychologue.net (et autres pages tmh)
            if not host or "psychologue.net" in host or "mundopsicologos" in host:
                continue
            base = f"{urlparse(site).scheme}://{host}"
            if base in seen_sites:
                continue
            seen_sites.add(base); sites.append(base)
    print(f"{C.GREEN}  {len(sites)} site(s) externe(s) trouve(s).{C.RESET}\n")
    return sites



def audit_url(url: str, check_links: bool = True, link_workers: int = 8) -> AuditResult:
    if not url.startswith(("http://", "https://")):
        url = "https://" + url
    result = AuditResult(url=url)
    t0 = time.time()
    try:
        resp = requests.get(url, headers={"User-Agent": USER_AGENT},
            timeout=TIMEOUT, allow_redirects=True, stream=True)
        content = b""
        for chunk in resp.iter_content(chunk_size=8192):
            content += chunk
            if len(content) >= MAX_PAGE_BYTES: break
        elapsed = (time.time() - t0) * 1000
    except requests.RequestException as exc:
        result.error = f"Requete impossible : {exc}"
        result.issues.append(result.error); result.score = 0; result.grade = "F"
        return result

    result.status_code = resp.status_code
    result.final_url = resp.url
    result.response_time_ms = int(elapsed)
    result.page_size_kb = int(len(content) / 1024)
    parsed_final = urlparse(resp.url); host = parsed_final.netloc

    result.https = parsed_final.scheme == "https"
    try:
        r2 = requests.get("http://" + host, headers={"User-Agent": USER_AGENT},
            timeout=TIMEOUT, allow_redirects=True)
        result.redirects_to_https = r2.url.startswith("https://")
    except requests.RequestException:
        result.redirects_to_https = result.https

    if result.https:
        valid, days = check_ssl_certificate(host)
        result.ssl_valid = valid; result.ssl_days_left = days
        if not valid:
            result.issues.append("Certificat SSL invalide ou injoignable")
        elif days is not None and days < 15:
            result.issues.append(f"Certificat SSL expire dans {days} jour(s)")
        elif days is not None and days < 30:
            result.warnings.append(f"Certificat SSL expire dans {days} jour(s)")
    else:
        result.issues.append("Site servi en HTTP (non chiffre)")

    headers_lower = {k.lower(): v for k, v in resp.headers.items()}
    for h in SECURITY_HEADERS:
        if h in headers_lower: result.security_headers_present.append(h)
        else: result.security_headers_missing.append(h)
    if result.https and "strict-transport-security" not in headers_lower:
        result.warnings.append("En-tete HSTS manquant")
    if "x-content-type-options" not in headers_lower:
        result.warnings.append("En-tete X-Content-Type-Options manquant")
    if "content-security-policy" not in headers_lower:
        result.warnings.append("En-tete Content-Security-Policy manquant")

    if elapsed > SLOW_RESPONSE_MS:
        result.issues.append(f"Reponse lente : {int(elapsed)} ms")
    elif elapsed > 800:
        result.warnings.append(f"Temps de reponse moyen : {int(elapsed)} ms")
    if result.page_size_kb > LARGE_PAGE_KB:
        result.issues.append(f"Page tres lourde : {result.page_size_kb} Ko")
    elif result.page_size_kb > 1024:
        result.warnings.append(f"Page lourde : {result.page_size_kb} Ko")

    if resp.status_code >= 400:
        result.issues.append(f"Code HTTP {resp.status_code}")
        return _finalize(result)

    try:
        soup = BeautifulSoup(content, "html.parser")
    except Exception as exc:
        result.error = f"HTML illisible : {exc}"
        result.issues.append(result.error)
        return _finalize(result)

    title_tag = soup.find("title")
    if title_tag and title_tag.text.strip():
        result.title = title_tag.text.strip()
        result.title_length = len(result.title)
        if result.title_length < 10:
            result.issues.append("Balise <title> trop courte")
        elif result.title_length > 65:
            result.warnings.append(f"<title> trop long ({result.title_length} car.)")
    else:
        result.issues.append("Balise <title> manquante")

    meta_desc = soup.find("meta", attrs={"name": re.compile(r"^description$", re.I)})
    if meta_desc and meta_desc.get("content", "").strip():
        result.meta_description = meta_desc["content"].strip()
        result.meta_description_length = len(result.meta_description)
        if result.meta_description_length < 50:
            result.warnings.append("Meta description trop courte")
        elif result.meta_description_length > 165:
            result.warnings.append(f"Meta description trop longue ({result.meta_description_length} car.)")
    else:
        result.issues.append("Meta description manquante")

    h1s = soup.find_all("h1"); result.h1_count = len(h1s)
    if result.h1_count == 0: result.issues.append("Aucune balise <h1>")
    elif result.h1_count > 1:
        result.warnings.append(f"{result.h1_count} balises <h1> (1 seule recommandee)")

    result.has_canonical = bool(soup.find("link", rel=lambda v: v and "canonical" in v))
    html_tag = soup.find("html")
    result.has_lang = bool(html_tag and html_tag.get("lang"))
    result.has_viewport = bool(soup.find("meta", attrs={"name": "viewport"}))
    result.has_og_tags = bool(soup.find("meta", attrs={"property": re.compile(r"^og:")}))
    if not result.has_canonical: result.warnings.append("Canonical manquant")
    if not result.has_lang: result.warnings.append("Attribut lang manquant sur <html>")
    if not result.has_viewport: result.issues.append("Meta viewport manquant (responsive)")
    if not result.has_og_tags: result.warnings.append("Aucune balise Open Graph")

    imgs = soup.find_all("img"); result.images_count = len(imgs)
    result.images_without_alt = sum(1 for i in imgs if not (i.get("alt") or "").strip())
    if result.images_without_alt > 0:
        msg = f"{result.images_without_alt} image(s) sans attribut alt"
        (result.issues if result.images_without_alt > 5 else result.warnings).append(msg)

    result.resource_count = (len(soup.find_all("script", src=True))
        + len(soup.find_all("link", rel="stylesheet")) + result.images_count)
    if result.resource_count > 100:
        result.warnings.append(f"Beaucoup de ressources ({result.resource_count})")

    base = f"{parsed_final.scheme}://{parsed_final.netloc}"
    try:
        r = requests.get(urljoin(base, "/robots.txt"),
            headers={"User-Agent": USER_AGENT}, timeout=TIMEOUT)
        result.robots_txt = r.status_code == 200 and "User-agent" in r.text
    except requests.RequestException: result.robots_txt = False
    try:
        r = requests.get(urljoin(base, "/sitemap.xml"),
            headers={"User-Agent": USER_AGENT}, timeout=TIMEOUT)
        result.sitemap_xml = r.status_code == 200 and "<urlset" in r.text[:5000]
    except requests.RequestException: result.sitemap_xml = False
    if not result.robots_txt: result.warnings.append("Pas de robots.txt")
    if not result.sitemap_xml: result.warnings.append("Pas de sitemap.xml")

    check_accessibility(soup, result)

    if check_links:
        checked, broken = check_broken_links(resp.url, soup, workers=link_workers)
        result.links_checked = checked
        result.broken_links = broken
        if broken:
            msg = f"{len(broken)} lien(s) casse(s) sur {checked} teste(s)"
            (result.issues if len(broken) > 3 else result.warnings).append(msg)

    return _finalize(result)


def _finalize(result: AuditResult) -> AuditResult:
    score = 100 - (len(result.issues) * 12) - (len(result.warnings) * 4)
    result.score = max(0, min(100, score))
    result.grade = grade_from_score(result.score)
    return result


def grade_from_score(s: int) -> str:
    if s >= 90: return "A"
    if s >= 75: return "B"
    if s >= 60: return "C"
    if s >= 40: return "D"
    return "F"


def color_for_score(s: int) -> str:
    if s >= 75: return C.GREEN
    if s >= 50: return C.YELLOW
    return C.RED


def print_result(r: AuditResult) -> None:
    bar = "-" * 72
    col = color_for_score(r.score)
    print(f"\n{C.BOLD}{bar}{C.RESET}")
    print(f"{C.BOLD}{r.url}{C.RESET}")
    if r.final_url and r.final_url != r.url:
        print(f"{C.DIM}  -> {r.final_url}{C.RESET}")
    print(f"  Score : {col}{r.score}/100  (note {r.grade}){C.RESET}"
          f"   HTTP {r.status_code}   {r.response_time_ms} ms   {r.page_size_kb} Ko")
    if r.error:
        print(f"  {C.RED}Erreur : {r.error}{C.RESET}")
        return
    if r.issues:
        print(f"  {C.RED}{C.BOLD}Problemes :{C.RESET}")
        for i in r.issues:
            print(f"    {C.RED}X{C.RESET} {i}")
    if r.warnings:
        print(f"  {C.YELLOW}{C.BOLD}Avertissements :{C.RESET}")
        for w in r.warnings:
            print(f"    {C.YELLOW}*{C.RESET} {w}")
    if r.broken_links:
        print(f"  {C.MAGENTA}{C.BOLD}Liens casses :{C.RESET}")
        for b in r.broken_links[:5]:
            print(f"    {C.MAGENTA}>{C.RESET} {b}")
        if len(r.broken_links) > 5:
            print(f"    {C.DIM}... +{len(r.broken_links)-5} autre(s){C.RESET}")
    if not r.issues and not r.warnings:
        print(f"  {C.GREEN}OK - Aucun probleme majeur detecte{C.RESET}")


def print_summary(results: List[AuditResult]) -> None:
    if not results: return
    bar = "=" * 72
    print(f"\n{C.BOLD}{bar}{C.RESET}")
    print(f"{C.BOLD}  RESUME ({len(results)} site(s) audite(s)){C.RESET}")
    print(f"{C.BOLD}{bar}{C.RESET}")
    for r in sorted(results, key=lambda x: x.score):
        col = color_for_score(r.score)
        domain = urlparse(r.url).netloc or r.url
        print(f"  {col}{r.grade}  {r.score:3d}/100{C.RESET}  "
              f"{domain:<40}  {C.RED}{len(r.issues)} pb{C.RESET}  "
              f"{C.YELLOW}{len(r.warnings)} avert.{C.RESET}")


def export_csv(results: List[AuditResult], path: str) -> None:
    if not results: return
    fields = list(asdict(results[0]).keys())
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=fields)
        w.writeheader()
        for r in results:
            row = asdict(r)
            for k, v in row.items():
                if isinstance(v, list):
                    row[k] = " | ".join(map(str, v))
            w.writerow(row)
    print(f"\n{C.GREEN}CSV exporte : {path}{C.RESET}")


def export_json(results: List[AuditResult], path: str) -> None:
    with open(path, "w", encoding="utf-8") as f:
        json.dump([asdict(r) for r in results], f, ensure_ascii=False, indent=2)
    print(f"{C.GREEN}JSON exporte : {path}{C.RESET}")


HTML_CSS = (
"*{box-sizing:border-box}"
"body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;"
"background:#0f172a;color:#e2e8f0;margin:0;padding:2rem}"
"h1{margin:0 0 .5rem;font-size:1.8rem}"
".sub{color:#94a3b8;margin-bottom:2rem}"
".summary{background:#1e293b;border-radius:12px;padding:1.5rem;margin-bottom:2rem;"
"display:grid;grid-template-columns:repeat(auto-fit,minmax(140px,1fr));gap:1rem}"
".stat{text-align:center}.stat .v{font-size:2rem;font-weight:700}"
".stat .l{color:#94a3b8;font-size:.85rem;text-transform:uppercase;letter-spacing:.05em}"
".grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(380px,1fr));gap:1.25rem}"
".card{background:#1e293b;border-radius:12px;padding:1.25rem;"
"border-left:6px solid #475569;position:relative}"
".card.A{border-left-color:#10b981}.card.B{border-left-color:#84cc16}"
".card.C{border-left-color:#eab308}.card.D{border-left-color:#f97316}"
".card.F{border-left-color:#ef4444}"
".card h2{margin:0 0 .25rem;font-size:1.1rem;word-break:break-all}"
".card h2 a{color:#e2e8f0;text-decoration:none}"
".meta{color:#94a3b8;font-size:.85rem;margin-bottom:.75rem}"
".score{position:absolute;top:1rem;right:1.25rem;font-size:2rem;font-weight:700;"
"background:#0f172a;padding:.25rem .75rem;border-radius:8px}"
".score.A{color:#10b981}.score.B{color:#84cc16}.score.C{color:#eab308}"
".score.D{color:#f97316}.score.F{color:#ef4444}"
".tags{display:flex;flex-wrap:wrap;gap:.35rem;margin-bottom:.75rem}"
".tag{background:#334155;color:#cbd5e1;font-size:.75rem;"
"padding:.15rem .55rem;border-radius:999px}"
".tag.ok{background:#064e3b;color:#6ee7b7}"
".tag.bad{background:#7f1d1d;color:#fecaca}"
".section{margin-top:.75rem}"
".section h3{font-size:.8rem;text-transform:uppercase;letter-spacing:.05em;"
"color:#94a3b8;margin:.5rem 0 .35rem}"
".section ul{margin:0;padding-left:1.2rem;font-size:.9rem}"
".issues li{color:#fca5a5}.warns li{color:#fde68a}"
".broken li{color:#f0abfc;word-break:break-all;font-size:.8rem}"
"footer{margin-top:2rem;text-align:center;color:#64748b;font-size:.85rem}"
)


def _html_card(r: AuditResult) -> str:
    safe_url = html.escape(r.final_url or r.url)
    safe_short = html.escape(urlparse(r.url).netloc or r.url)
    safe_title = html.escape(r.title or "(sans titre)")
    tags = []
    tags.append('<span class="tag ok">HTTPS</span>' if r.https
                else '<span class="tag bad">HTTP</span>')
    if r.https:
        if r.ssl_valid and r.ssl_days_left is not None:
            cls = "ok" if r.ssl_days_left > 30 else "bad"
            tags.append(f'<span class="tag {cls}">SSL {r.ssl_days_left}j</span>')
        elif not r.ssl_valid:
            tags.append('<span class="tag bad">SSL invalide</span>')
    tags.append(f'<span class="tag">{r.response_time_ms} ms</span>')
    tags.append(f'<span class="tag">{r.page_size_kb} Ko</span>')
    if r.robots_txt: tags.append('<span class="tag ok">robots.txt</span>')
    if r.sitemap_xml: tags.append('<span class="tag ok">sitemap</span>')
    sections = []
    if r.issues:
        items = "".join(f"<li>{html.escape(i)}</li>" for i in r.issues)
        sections.append(f'<div class="section issues"><h3>Problemes ({len(r.issues)})</h3><ul>{items}</ul></div>')
    if r.warnings:
        items = "".join(f"<li>{html.escape(w)}</li>" for w in r.warnings)
        sections.append(f'<div class="section warns"><h3>Avertissements ({len(r.warnings)})</h3><ul>{items}</ul></div>')
    if r.broken_links:
        items = "".join(f"<li>{html.escape(b)}</li>" for b in r.broken_links[:10])
        more = f"<li>... +{len(r.broken_links)-10} autre(s)</li>" if len(r.broken_links) > 10 else ""
        sections.append(f'<div class="section broken"><h3>Liens casses ({len(r.broken_links)})</h3><ul>{items}{more}</ul></div>')
    if not sections:
        sections.append('<div class="section"><h3>OK - Aucun probleme majeur</h3></div>')
    return (f'<div class="card {r.grade}">'
            f'<div class="score {r.grade}">{r.score}</div>'
            f'<h2><a href="{safe_url}" target="_blank" rel="noopener">{safe_short}</a></h2>'
            f'<div class="meta">{safe_title}</div>'
            f'<div class="tags">{"".join(tags)}</div>'
            f'{"".join(sections)}</div>')


def export_html(results: List[AuditResult], path: str) -> None:
    ordered = sorted(results, key=lambda x: x.score)
    cards = "\n".join(_html_card(r) for r in ordered)
    counts = {g: sum(1 for r in results if r.grade == g) for g in "ABCDF"}
    date = datetime.now().strftime("%Y-%m-%d %H:%M")
    ti = sum(len(r.issues) for r in results)
    tw = sum(len(r.warnings) for r in results)
    tb = sum(len(r.broken_links) for r in results)
    out = (f'<!DOCTYPE html><html lang="fr"><head><meta charset="utf-8">'
           f'<title>Rapport audit web - {date}</title><style>{HTML_CSS}</style>'
           f'</head><body><h1>Rapport audit web</h1>'
           f'<div class="sub">Genere le {date} - {len(results)} site(s) analyse(s)</div>'
           f'<div class="summary">'
           f'<div class="stat"><div class="v" style="color:#10b981">{counts["A"]}</div><div class="l">Note A</div></div>'
           f'<div class="stat"><div class="v" style="color:#84cc16">{counts["B"]}</div><div class="l">Note B</div></div>'
           f'<div class="stat"><div class="v" style="color:#eab308">{counts["C"]}</div><div class="l">Note C</div></div>'
           f'<div class="stat"><div class="v" style="color:#f97316">{counts["D"]}</div><div class="l">Note D</div></div>'
           f'<div class="stat"><div class="v" style="color:#ef4444">{counts["F"]}</div><div class="l">Note F</div></div>'
           f'<div class="stat"><div class="v">{ti}</div><div class="l">Problemes</div></div>'
           f'<div class="stat"><div class="v">{tw}</div><div class="l">Avertissements</div></div>'
           f'<div class="stat"><div class="v">{tb}</div><div class="l">Liens casses</div></div>'
           f'</div><div class="grid">{cards}</div>'
           f'<footer>Web Audit Agent v1.1</footer></body></html>')
    with open(path, "w", encoding="utf-8") as f:
        f.write(out)
    print(f"{C.GREEN}HTML exporte : {path}{C.RESET}")


def main() -> int:
    p = argparse.ArgumentParser(
        description="Agent qui parcourt le web et detecte les soucis (Perf/SEO/Securite/A11y).",
        formatter_class=argparse.RawDescriptionHelpFormatter, epilog=__doc__)
    p.add_argument("query", nargs="?", help="Thematique a rechercher")
    p.add_argument("--urls", help="Liste d'URLs separees par des virgules")
    p.add_argument("--urls-file", dest="urls_file",
                   help="Fichier texte contenant une URL par ligne")
    p.add_argument("--follow-psychologuenet", action="store_true",
                   help="Suit les URLs psychologue.net pour auditer les sites personnels des praticiens")
    p.add_argument("--max-profiles", type=int, default=30,
                   help="Nb max de profils a explorer en mode --follow-psychologuenet")
    p.add_argument("--limit", type=int, default=10, help="Nb max de sites (defaut 10)")
    p.add_argument("--workers", type=int, default=4, help="Audits paralleles (defaut 4)")
    p.add_argument("--no-check-links", action="store_true",
                   help="Desactive la detection de liens casses")
    p.add_argument("--csv", help="Export CSV")
    p.add_argument("--json", dest="json_path", help="Export JSON")
    p.add_argument("--html", dest="html_path", help="Export HTML (rapport visuel)")
    p.add_argument("--no-color", action="store_true", help="Desactive les couleurs")
    args = p.parse_args()

    if args.no_color: C.disable()
    if not args.query and not args.urls and not args.urls_file:
        p.error("Indiquez une thematique OU --urls OU --urls-file")

    targets: List[str] = []
    if args.urls_file:
        try:
            with open(args.urls_file, "r", encoding="utf-8") as f:
                targets = [ln.strip() for ln in f
                           if ln.strip() and not ln.strip().startswith("#")]
        except OSError as exc:
            print(f"{C.RED}Impossible de lire {args.urls_file} : {exc}{C.RESET}")
            return 1
        print(f"{C.CYAN}{len(targets)} URL(s) lue(s) depuis {args.urls_file}{C.RESET}\n")
    elif args.urls:
        targets = [u.strip() for u in args.urls.split(",") if u.strip()]
        print(f"{C.CYAN}{len(targets)} URL(s) a auditer{C.RESET}\n")
    else:
        targets = search_duckduckgo(args.query, limit=args.limit)
        if not targets:
            print(f"{C.RED}Aucun site trouve.{C.RESET}")
            return 1

    if args.follow_psychologuenet:
        print(f"{C.BLUE}Expansion psychologue.net en cours...{C.RESET}")
        targets = expand_psychologuenet_targets(targets, max_profiles=args.max_profiles)
        if not targets:
            print(f"{C.RED}Aucun site externe trouve sur les profils.{C.RESET}")
            return 1

    check_links = not args.no_check_links
    results: List[AuditResult] = []
    print(f"{C.BLUE}Audit de {len(targets)} site(s) avec {args.workers} worker(s)...{C.RESET}")
    with ThreadPoolExecutor(max_workers=args.workers) as ex:
        future_to_url = {ex.submit(audit_url, url, check_links): url for url in targets}
        done = 0
        for fut in as_completed(future_to_url):
            url = future_to_url[fut]; done += 1
            try:
                r = fut.result()
            except Exception as exc:
                r = AuditResult(url=url, error=str(exc), score=0, grade="F",
                                issues=[f"Exception: {exc}"])
            results.append(r)
            print(f"{C.DIM}[{done}/{len(targets)}] {urlparse(url).netloc} "
                  f"-> {r.grade} {r.score}/100{C.RESET}")


    order = {u: i for i, u in enumerate(targets)}
    results.sort(key=lambda r: order.get(r.url, 999))

    for r in results:
        print_result(r)
    print_summary(results)

    if args.csv: export_csv(results, args.csv)
    if args.json_path: export_json(results, args.json_path)
    if args.html_path: export_html(results, args.html_path)

    total_issues = sum(len(r.issues) for r in results)
    print(f"\n{C.BOLD}Total : {total_issues} probleme(s) critique(s) sur "
          f"{len(results)} site(s).{C.RESET}\n")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print(f"\n{C.YELLOW}Interrompu par l'utilisateur.{C.RESET}")
        sys.exit(130)
