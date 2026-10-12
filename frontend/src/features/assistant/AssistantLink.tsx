import { NavLink, useNavigate } from 'react-router-dom';
import { useAssistantStatus } from './queries';

/**
 * The header's way in, beside the notification bell. Hidden while the assistant is switched off,
 * and on a failed status too: an optional feature never puts an error panel in the shell (the page
 * itself shows the error).
 */
export function AssistantLink(): React.JSX.Element | null {
  const status = useAssistantStatus();
  const navigate = useNavigate();
  if (status.data?.enabled !== true) {
    return null;
  }

  return (
    <NavLink
      to="/assistant"
      // Even on /assistant it starts a fresh chat: the token is what tells the page apart.
      onClick={(event) => {
        event.preventDefault();
        void navigate('/assistant', { state: { fresh: Date.now() } });
      }}
      className={({ isActive }) => (isActive ? 'shell__icon shell__icon--active' : 'shell__icon')}
      aria-label="Assistant"
      title="Assistant"
    >
      <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor"
        strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
        <path d="M21 12a8 8 0 0 1-11.6 7.1L4 20l1-4.6A8 8 0 1 1 21 12Z" />
      </svg>
    </NavLink>
  );
}
