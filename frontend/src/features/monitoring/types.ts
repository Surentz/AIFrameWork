import type { components } from '../../api/schema';

export type MonitoringAccess = components['schemas']['MonitoringAccessResponse'];

export type JobHealth = components['schemas']['JobHealthResponse'];

export type JobRun = components['schemas']['JobRunResponse'];

export type JobRunPage = components['schemas']['JobRunPageResponse'];

export type JobRunStatus = components['schemas']['JobRunStatus'];

export type DeadLetter = components['schemas']['DeadLetterResponse'];

export type DeadLetterPage = components['schemas']['DeadLetterPageResponse'];

export type SignInHealth = components['schemas']['SignInHealthResponse'];

export type SignInEvent = components['schemas']['SignInEventResponse'];

export type SignInEventPage = components['schemas']['SignInEventPageResponse'];

export type SignInOutcome = components['schemas']['SignInOutcome'];
