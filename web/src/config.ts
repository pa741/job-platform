/**
 * Build-time configuration, from Vite environment variables.
 *
 * Vite inlines these at build time, so they are visible in the bundle - which is why only
 * public identifiers live here. Anything secret would have to be held by the API instead.
 */
function required(name: string, value: string | undefined): string {
  if (!value) {
    throw new Error(
      `${name} is not set. Copy .env.example to .env.local and fill it in, ` +
        'or set it in the Static Web Apps build configuration.',
    );
  }
  return value;
}

export const config = {
  apiBaseUrl: required('VITE_API_BASE_URL', import.meta.env.VITE_API_BASE_URL).replace(/\/$/, ''),
  tenantId: required('VITE_ENTRA_TENANT_ID', import.meta.env.VITE_ENTRA_TENANT_ID),
  clientId: required('VITE_ENTRA_CLIENT_ID', import.meta.env.VITE_ENTRA_CLIENT_ID),
  apiScope: required('VITE_API_SCOPE', import.meta.env.VITE_API_SCOPE),
};
