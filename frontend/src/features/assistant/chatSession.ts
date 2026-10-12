import { useCallback, useState } from 'react';

interface SessionState {
  /** A new chat that was given this id by its first answer, and is still the chat on screen. */
  readonly adopted: string | undefined;
  /** Whether the address has reached `adopted` yet: the navigation lands after the adoption. */
  readonly arrived: boolean;
  /** Bumped for every fresh new chat, so its key changes even when the address does not. */
  readonly generation: number;
  /** The last header-icon token seen, so one click is acted on once. */
  readonly token: number | undefined;
}

export interface ChatSession {
  /** The key the chat renders under: a different key is a fresh chat instance. */
  readonly key: string;
  /** The saved conversation to load, or undefined for a new chat (adopted ones included). */
  readonly load: string | undefined;
  /** A new chat's first answer was saved as `id`: keep this instance at its new address. */
  readonly adopt: (id: string) => void;
  /** New chat: a fresh instance even when the address does not change. */
  readonly fresh: () => void;
}

/**
 * Which chat instance to show for an address. A new chat keeps its instance (and its stream) when
 * its first answer moves the address to /assistant/<id>; every other change of conversation — the
 * sidebar, New chat, the header icon — is a fresh instance.
 */
export function useChatSession(
  conversationId: string | undefined,
  freshToken?: number,
): ChatSession {
  const [session, setSession] = useState<SessionState>({
    adopted: undefined,
    arrived: false,
    generation: 0,
    token: undefined,
  });

  // Adjusting state during render, the pattern React's docs give for "adjusting state when a prop
  // changes". The adoption is recorded before the navigation lands, so the address is first
  // /assistant (still waiting), then /assistant/<id> (arrived). Only leaving it after that — or
  // going to some other conversation before it arrived — forgets the adopted chat and makes the
  // next new chat a new instance.
  // The header icon on a new chat changes no address, so it carries a token instead: a new token is
  // a request for a fresh chat, whatever the address.
  if (freshToken !== undefined && freshToken !== session.token) {
    setSession({
      adopted: undefined,
      arrived: false,
      generation: session.generation + 1,
      token: freshToken,
    });
  } else if (session.adopted !== undefined) {
    if (conversationId === session.adopted && !session.arrived) {
      setSession({ ...session, arrived: true });
    } else if (
      conversationId !== session.adopted &&
      (session.arrived || conversationId !== undefined)
    ) {
      setSession({ ...session, adopted: undefined, arrived: false, generation: session.generation + 1 });
    }
  }

  const live = conversationId === undefined || conversationId === session.adopted;

  const adopt = useCallback((id: string) => {
    setSession((current) => ({ ...current, adopted: id, arrived: false }));
  }, []);

  const fresh = useCallback(() => {
    setSession((current) => ({
      ...current,
      adopted: undefined,
      arrived: false,
      generation: current.generation + 1,
    }));
  }, []);

  return {
    key: live ? `new-${String(session.generation)}` : `saved-${conversationId}`,
    load: live ? undefined : conversationId,
    adopt,
    fresh,
  };
}
