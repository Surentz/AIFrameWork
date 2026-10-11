import { useState } from 'react';
import { ErrorPanel } from '../../components/ErrorPanel';
import { Chat } from './Chat';
import { useAssistantStatus } from './queries';
import './assistant.css';

/**
 * The assistant (ADRs 0033, 0034). Hidden entirely when the assistant is switched off. Task 5
 * replaces this with the sidebar and the routes.
 */
export function AssistantPage(): React.JSX.Element {
  const status = useAssistantStatus();
  const [draft, setDraft] = useState('');

  if (status.isPending) {
    return <p role="status">Loading the assistant…</p>;
  }

  if (status.error) {
    return <ErrorPanel error={status.error} />;
  }

  if (!status.data.enabled) {
    return (
      <section>
        <h1>Ask the assistant</h1>
        <p>
          The assistant is switched off. An operator turns it on with{' '}
          <code>Assistant__Enabled</code> and an API key file; see{' '}
          <code>docs/local-development.md</code>.
        </p>
      </section>
    );
  }

  return <Chat onSaved={() => undefined} operator={false} draft={draft} onDraftChange={setDraft} />;
}
