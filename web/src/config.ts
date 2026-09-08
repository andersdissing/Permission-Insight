/**
 * Runtime configuration, fetched before anything else starts.
 *
 * Nothing here is baked in at build time. The deployment writes config.json
 * from the Bicep outputs, which keeps tenant ids and client ids out of the
 * repository and means a second tenant needs a second file rather than a
 * second build.
 */
export interface AppConfig {
  tenantId: string;
  clientId: string;
  apiBaseUrl: string;
  apiScope: string;
  /** Named in the access denied screen, so the user knows what to ask for. */
  usersGroupName: string;
}

const required: (keyof AppConfig)[] = [
  'tenantId',
  'clientId',
  'apiBaseUrl',
  'apiScope',
  'usersGroupName',
];

export async function loadConfig(): Promise<AppConfig> {
  const response = await fetch('/config.json', { cache: 'no-store' });

  if (!response.ok) {
    throw new Error(
      'config.json is missing. The deployment writes it; for local development copy web/config.example.json to web/public/config.json.',
    );
  }

  const config = (await response.json()) as Partial<AppConfig>;

  const missing = required.filter((key) => !config[key]);
  if (missing.length > 0) {
    throw new Error(`config.json is missing: ${missing.join(', ')}.`);
  }

  return config as AppConfig;
}
