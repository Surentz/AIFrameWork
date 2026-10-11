import { useQueryClient } from '@tanstack/react-query';
import { useCallback, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { ErrorPanel } from '../../components/ErrorPanel';
import { useSession } from '../auth/queries';
import { Chat } from './Chat';
import { useChatSession } from './chatSession';
import { ConversationSidebar } from './ConversationSidebar';
import { fromSaved } from './conversation';
import { assistantKeys, useAssistantStatus, useSavedConversation } from './queries';
import './assistant.css';

/**
 * The assistant (ADRs 0033, 0034), for every signed-in user: the conversations on the left, the
 * open one in the middle. /assistant is a new chat; /assistant/:conversationId a saved one.
 */
export function AssistantPage(): React.JSX.Element {
  const status = useAssistantStatus();
  const { data: session } = useSession();
  const { conversationId } = useParams();
  const chat = useChatSession(conversationId);
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState('');

  const onSaved = useCallback(
    (id: string) => {
      void queryClient.invalidateQueries({ queryKey: assistantKeys.conversations(), exact: true });
      if (conversationId === undefined) {
        chat.adopt(id);
        void navigate(`/assistant/${id}`, { replace: true });
      }
    },
    [chat, conversationId, navigate, queryClient],
  );

  const newChat = useCallback(() => {
    chat.fresh();
    void navigate('/assistant');
  }, [chat, navigate]);

  if (status.isPending) {
    return <p role="status">Loading the assistant…</p>;
  }

  if (status.error) {
    return <ErrorPanel error={status.error} />;
  }

  if (!status.data.enabled) {
    return (
      <section className="assistant-off">
        <h1>Assistant</h1>
        <p>
          The assistant is switched off. An operator turns it on with <code>Assistant__Enabled</code>{' '}
          and an API key file; see <code>docs/local-development.md</code>.
        </p>
      </section>
    );
  }

  const operator = session?.role === 'Admin';

  return (
    <div className="assistant">
      <h1 className="visually-hidden">Assistant</h1>
      <ConversationSidebar activeId={conversationId} onNewChat={newChat} />
      {chat.load === undefined ? (
        <Chat key={chat.key} onSaved={onSaved} operator={operator} draft={draft} onDraftChange={setDraft} />
      ) : (
        <SavedChat key={chat.key} id={chat.load} onSaved={onSaved} operator={operator} draft={draft} onDraftChange={setDraft} />
      )}
    </div>
  );
}

interface SavedChatProps {
  readonly id: string;
  readonly onSaved: (conversationId: string) => void;
  readonly operator: boolean;
  readonly draft: string;
  readonly onDraftChange: (draft: string) => void;
}

/** Loads a saved conversation once, then hands it to the chat, which owns it from then on. */
function SavedChat({ id, ...chatProps }: SavedChatProps): React.JSX.Element {
  const saved = useSavedConversation(id);

  if (saved.isPending) {
    return (
      <p role="status" className="assistant-chat assistant-chat--placeholder">
        Loading the conversation…
      </p>
    );
  }

  if (saved.error) {
    return (
      <div className="assistant-chat assistant-chat--placeholder">
        <ErrorPanel error={saved.error} />
      </div>
    );
  }

  return <Chat initial={fromSaved(saved.data)} {...chatProps} />;
}
