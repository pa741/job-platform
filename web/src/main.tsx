import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { MsalProvider } from '@azure/msal-react';
import { EventType } from '@azure/msal-browser';
import { msalInstance } from './auth/msalConfig';
import { App } from './App';

// MSAL v5 must be initialised before use, and the redirect promise handled before render -
// otherwise the token in the URL fragment is dropped and the app bounces back to sign-in.
await msalInstance.initialize();
await msalInstance.handleRedirectPromise();

const account = msalInstance.getAllAccounts()[0];
if (account) msalInstance.setActiveAccount(account);

msalInstance.addEventCallback((event) => {
  if (event.eventType === EventType.LOGIN_SUCCESS && event.payload && 'account' in event.payload) {
    // The payload's account is optional in the union, so it is narrowed rather than asserted.
    const loggedIn = event.payload.account;
    if (loggedIn) msalInstance.setActiveAccount(loggedIn);
  }
});

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <MsalProvider instance={msalInstance}>
      <App />
    </MsalProvider>
  </StrictMode>,
);
