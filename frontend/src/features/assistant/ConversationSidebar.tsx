import { useState } from 'react';
import type { KeyboardEvent, SubmitEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { ErrorPanel } from '../../components/ErrorPanel';
import { groupByAge } from './history';
import { useConversations, useDeleteConversation, useRenameConversation } from './queries';
import type { ConversationSummary } from './types';

const MaxTitleLength = 60;

interface ConversationSidebarProps {
  readonly activeId: string | undefined;
  readonly onNewChat: () => void;
}

/** The caller's conversations, newest first, grouped by age; rename and delete per row. */
export function ConversationSidebar({
  activeId,
  onNewChat,
}: ConversationSidebarProps): React.JSX.Element {
  const conversations = useConversations();

  return (
    <nav className="assistant-sidebar" aria-label="Conversations">
      <button type="button" className="assistant-sidebar__new" onClick={onNewChat}>
        <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor"
          strokeWidth="2" strokeLinecap="round" aria-hidden="true">
          <path d="M12 5v14M5 12h14" />
        </svg>
        New chat
      </button>

      {conversations.isPending && (
        <p role="status" className="muted">
          Loading conversations…
        </p>
      )}
      {conversations.error && <ErrorPanel error={conversations.error} />}

      {conversations.data &&
        groupByAge(conversations.data, new Date()).map((group) => (
          <section key={group.label} className="assistant-sidebar__group">
            <h2 className="assistant-sidebar__label">{group.label}</h2>
            <ul className="assistant-sidebar__list">
              {group.items.map((conversation) => (
                <ConversationRow
                  key={conversation.id}
                  conversation={conversation}
                  active={conversation.id === activeId}
                />
              ))}
            </ul>
          </section>
        ))}
    </nav>
  );
}

interface ConversationRowProps {
  readonly conversation: ConversationSummary;
  readonly active: boolean;
}

type RowMode = 'view' | 'menu' | 'rename' | 'confirm';

function ConversationRow({ conversation, active }: ConversationRowProps): React.JSX.Element {
  const [mode, setMode] = useState<RowMode>('view');
  const rename = useRenameConversation();
  const remove = useDeleteConversation();
  const navigate = useNavigate();

  function submitRename(event: SubmitEvent<HTMLFormElement>): void {
    event.preventDefault();
    const title = new FormData(event.currentTarget).get('title');
    const trimmed = typeof title === 'string' ? title.trim() : '';
    if (trimmed === '' || trimmed === conversation.title) {
      setMode('view');
      return;
    }

    rename.mutate({ id: conversation.id, title: trimmed }, { onSuccess: () => { setMode('view'); } });
  }

  function cancelOnEscape(event: KeyboardEvent<HTMLInputElement>): void {
    if (event.key === 'Escape') {
      setMode('view');
    }
  }

  function confirmDelete(): void {
    remove.mutate(conversation.id, {
      onSuccess: () => {
        if (active) {
          void navigate('/assistant');
        }
      },
    });
  }

  return (
    <li className={active ? 'assistant-row assistant-row--active' : 'assistant-row'}>
      {mode === 'rename' ? (
        <form onSubmit={submitRename}>
          <input
            name="title"
            className="assistant-row__input"
            aria-label="Conversation title"
            defaultValue={conversation.title}
            maxLength={MaxTitleLength}
            // The user just chose Rename: the input is the one thing they act on next.
            // eslint-disable-next-line jsx-a11y/no-autofocus
            autoFocus
            onKeyDown={cancelOnEscape}
          />
        </form>
      ) : (
        <div className="assistant-row__line">
          <Link
            className="assistant-row__link"
            to={`/assistant/${conversation.id}`}
            aria-current={active ? 'page' : undefined}
          >
            {conversation.title}
          </Link>
          <button
            type="button"
            className="assistant-row__more"
            aria-label={`Options for ${conversation.title}`}
            aria-expanded={mode !== 'view'}
            onClick={() => { setMode(mode === 'view' ? 'menu' : 'view'); }}
          >
            ⋯
          </button>
        </div>
      )}

      {mode === 'menu' && (
        <div className="assistant-row__menu">
          <button type="button" onClick={() => { setMode('rename'); }}>
            Rename
          </button>
          <button type="button" onClick={() => { setMode('confirm'); }}>
            Delete
          </button>
        </div>
      )}

      {mode === 'confirm' && (
        <div className="assistant-row__menu" role="group" aria-label="Confirm delete">
          <span>Delete this conversation?</span>
          <button type="button" className="assistant-row__danger" onClick={confirmDelete}>
            Delete conversation
          </button>
          <button type="button" onClick={() => { setMode('view'); }}>
            Cancel
          </button>
        </div>
      )}

      {rename.error && <ErrorPanel error={rename.error} />}
      {remove.error && <ErrorPanel error={remove.error} />}
    </li>
  );
}
