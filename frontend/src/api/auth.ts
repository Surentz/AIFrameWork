import type { Credentials, Session } from '../features/auth/types';
import { request } from './client';

export function login(credentials: Credentials): Promise<Session> {
  return request<Session>('/api/auth/login', {
    method: 'POST',
    body: JSON.stringify(credentials),
  });
}
