import type { components } from '../../api/schema';

export type AssistantStatus = components['schemas']['AssistantStatusResponse'];

export type AskAssistantRequest = components['schemas']['AskAssistantRequest'];

/** One server-sent event's data: `type` is text, tool, done or error. */
export type AssistantEvent = components['schemas']['AssistantEventResponse'];

// The generator puts null inside the enum because the property is nullable; the outcome itself is three values.
export type AssistantOutcome = NonNullable<components['schemas']['AssistantOutcome']>;
