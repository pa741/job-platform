import { useMemo } from 'react';
import { useMsal } from '@azure/msal-react';
import { InteractionRequiredAuthError } from '@azure/msal-browser';
import { JobPlatformApi } from '../api/client';
import { apiRequest, msalInstance } from './msalConfig';
import { config } from '../config';

/**
 * The API client, bound to the signed-in account.
 *
 * Tokens are acquired silently per request rather than cached in component state: MSAL
 * already caches and refreshes them, and a token held in a `useState` is one that expires
 * mid-session with no way to notice. When silent acquisition genuinely needs the user, it
 * falls back to a redirect rather than failing the call.
 */
export function useApi(): JobPlatformApi {
  const { instance, accounts } = useMsal();

  return useMemo(
    () =>
      new JobPlatformApi(config.apiBaseUrl, async () => {
        const account = accounts[0] ?? instance.getActiveAccount();
        if (!account) return null;

        try {
          const result = await instance.acquireTokenSilent({ ...apiRequest, account });
          return result.accessToken;
        } catch (error) {
          if (error instanceof InteractionRequiredAuthError) {
            await msalInstance.acquireTokenRedirect({ ...apiRequest, account });
          }
          return null;
        }
      }),
    [instance, accounts],
  );
}
