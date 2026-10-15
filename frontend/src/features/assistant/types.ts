import type { components } from '../../api/schema';

export type AssistantStatus = components['schemas']['AssistantStatusResponse'];

export type AskAssistantRequest = components['schemas']['AskAssistantRequest'];

/** One server-sent event's data: `type` is text, tool, done or error. */
export type AssistantEvent = components['schemas']['AssistantEventResponse'];

// The generator puts null inside the enum because the property is nullable; the outcome itself is three values.
export type AssistantOutcome = NonNullable<components['schemas']['AssistantOutcome']>;

export type ConversationSummary = components['schemas']['AssistantConversationSummaryResponse'];

export type SavedConversation = components['schemas']['AssistantConversationResponse'];

export type SavedMessage = components['schemas']['AssistantMessageResponse'];

/** An instant or help answer: already formatted, rendered as a card, never as Markdown. */
export type AssistantAnswer = components['schemas']['AssistantAnswerResponse'];

export type AssistantSuggestions = components['schemas']['AssistantSuggestionsResponse'];

/** Which path answers: the local router (quick) or the AI. */
export type AskMode = 'quick' | 'ai';

/** An answer's rating. Sent as null to clear it. */
export type AnswerRating = NonNullable<components['schemas']['AnswerRating']>;
