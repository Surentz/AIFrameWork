import { useState } from 'react';

const storageKey = 'aiframework.nav.open';

type FoldState = Readonly<Record<string, boolean>>;

function isFoldState(value: unknown): value is FoldState {
  return (
    typeof value === 'object' &&
    value !== null &&
    !Array.isArray(value) &&
    Object.values(value as Record<string, unknown>).every((entry) => typeof entry === 'boolean')
  );
}

function readStored(): FoldState {
  try {
    const raw = window.localStorage.getItem(storageKey);
    if (raw === null) {
      return {};
    }
    const parsed: unknown = JSON.parse(raw);
    return isFoldState(parsed) ? parsed : {};
  } catch {
    // Which groups are open is a convenience, not data. Storage can be blocked (private windows,
    // cleared site data) or hold something this code did not write; either way every group starts
    // closed, the current one still opens, and there is nothing the user could do about it.
    return {};
  }
}

function writeStored(state: FoldState): void {
  try {
    window.localStorage.setItem(storageKey, JSON.stringify(state));
  } catch {
    // Same reasoning as readStored: losing the memory costs the user a click, not their data.
  }
}

export interface NavFold {
  readonly isOpen: (groupId: string) => boolean;
  readonly toggle: (groupId: string) => void;
}

/**
 * Which sidebar groups are open. Closed by default; the group holding the current page opens on
 * first render and again whenever navigation moves into a different group, even if the user had
 * folded it. Staying on a page inside a group the user then folds leaves it folded. Only the
 * user's own folding and unfolding is stored.
 */
export function useNavFold(activeGroupId: string | undefined): NavFold {
  const [state, setState] = useState<FoldState>(() => ({
    ...readStored(),
    ...(activeGroupId === undefined ? {} : { [activeGroupId]: true }),
  }));
  const [seenGroupId, setSeenGroupId] = useState(activeGroupId);

  // Adjusted during render rather than in an effect: React's own recommendation for state that
  // follows a changing prop, and it leaves no frame in which the new group shows folded.
  if (seenGroupId !== activeGroupId) {
    setSeenGroupId(activeGroupId);
    if (activeGroupId !== undefined) {
      setState({ ...state, [activeGroupId]: true });
    }
  }

  return {
    isOpen: (groupId) => state[groupId] === true,
    toggle: (groupId) => {
      const open = state[groupId] !== true;
      setState((current) => ({ ...current, [groupId]: open }));
      // Only what the user chose is remembered. Groups that navigation opened around them are not,
      // or every group ever visited would stay open and "closed by default" would decay to "open".
      writeStored({ ...readStored(), [groupId]: open });
    },
  };
}
