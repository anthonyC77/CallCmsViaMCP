/**
 * Configuration de production = déploiement GitHub Pages en mode démo.
 * `apiBaseUrl` vide + `demo: true` → services mockés avec les données du rapport,
 * aucune écriture réelle (cf. §8 du plan).
 */
export const environment = {
  production: true,
  demo: true,
  apiBaseUrl: '',
};
