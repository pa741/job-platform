import { PublicClientApplication, type Configuration } from '@azure/msal-browser';
import { config } from '../config';

/**
 * MSAL configuration.
 *
 * Nothing here is a secret. A SPA client id and authority are public by construction - the
 * browser has to send them - and a SPA cannot hold a client secret at all. Authorisation is
 * the API's job: it validates the token's signature, issuer, audience and scope.
 */
const msalConfiguration: Configuration = {
  auth: {
    clientId: config.clientId,
    authority: `https://login.microsoftonline.com/${config.tenantId}`,
    redirectUri: window.location.origin,
    postLogoutRedirectUri: window.location.origin,
  },
  cache: {
    // sessionStorage rather than localStorage: the token dies with the tab, which is the
    // right default for a dashboard on a shared machine.
    cacheLocation: 'sessionStorage',
  },
};

export const msalInstance = new PublicClientApplication(msalConfiguration);

/** The scope the API requires. Requested at sign-in and on every silent refresh. */
export const apiRequest = { scopes: [config.apiScope] };
