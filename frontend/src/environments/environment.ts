/**
 * Configuration par défaut (dev local). L'usage réel pointe vers l'API locale.
 * `demo: false` → les services appellent réellement le backend.
 */
export const environment = {
  production: false,
  demo: false,
  apiBaseUrl: 'http://localhost:5215',
};
