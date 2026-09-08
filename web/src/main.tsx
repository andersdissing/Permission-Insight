import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import './styles.css';

/**
 * MSAL renews tokens silently in a hidden iframe pointed at the redirect URI,
 * which is this page. Booting the application inside that frame starts a
 * second sign-in, which MSAL refuses to perform in an iframe, so the frame
 * never reaches the state the parent is waiting for and renewal fails with
 * `timed_out` about an hour after signing in.
 *
 * MSAL reads the response straight off the frame's URL and needs no code
 * running inside it, so rendering nothing here is both correct and what makes
 * silent renewal work at all.
 */
if (window.self === window.top) {
  const container = document.getElementById('root');

  if (!container) {
    throw new Error('The page is missing its root element.');
  }

  createRoot(container).render(
    <StrictMode>
      <App />
    </StrictMode>,
  );
}
