"""Patch web_audit_agent.py pour ajouter --follow-psychologuenet."""
import re

PATH = "web_audit_agent.py"
src = open(PATH, encoding="utf-8").read()

# 1) Helpers a inserer juste avant la definition de audit_url
helper_block = '''
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
        m = re.match(r"^(?:https://www\\.psychologue\\.net)?(/cabinets/[a-z0-9\\-]+)$", href, re.I)
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
    print(f"{C.GREEN}  {len(sites)} site(s) externe(s) trouve(s).{C.RESET}\\n")
    return sites


'''

# Insertion juste avant "def audit_url("
marker = "\ndef audit_url("
if marker not in src:
    raise SystemExit("Marker introuvable")
src = src.replace(marker, helper_block + marker, 1)

# 2) Ajout du flag CLI juste apres --urls-file
cli_old = '    p.add_argument("--urls-file", dest="urls_file",\n                   help="Fichier texte contenant une URL par ligne")'
cli_new = cli_old + '''
    p.add_argument("--follow-psychologuenet", action="store_true",
                   help="Suit les URLs psychologue.net pour auditer les sites personnels des praticiens")
    p.add_argument("--max-profiles", type=int, default=30,
                   help="Nb max de profils a explorer en mode --follow-psychologuenet")'''
if cli_old not in src:
    raise SystemExit("Bloc CLI introuvable")
src = src.replace(cli_old, cli_new)

# 3) Apres le bloc de construction de `targets`, on ajoute l'expansion si flag actif
trigger = '''    else:
        targets = search_duckduckgo(args.query, limit=args.limit)
        if not targets:
            print(f"{C.RED}Aucun site trouve.{C.RESET}")
            return 1'''
addition = '''

    if args.follow_psychologuenet:
        print(f"{C.BLUE}Expansion psychologue.net en cours...{C.RESET}")
        targets = expand_psychologuenet_targets(targets, max_profiles=args.max_profiles)
        if not targets:
            print(f"{C.RED}Aucun site externe trouve sur les profils.{C.RESET}")
            return 1'''
if trigger not in src:
    raise SystemExit("Trigger targets introuvable")
src = src.replace(trigger, trigger + addition)

open(PATH, "w", encoding="utf-8").write(src)
print(f"Fichier reecrit: {len(src)} octets, {src.count(chr(10))+1} lignes")
