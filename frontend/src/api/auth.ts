import type { Credentials, PasswordChange, Registration, Session } from '../features/auth/types';
import { request, requestVoid } from './client';

export function login(credentials: Credentials): Promise<Session> {
  return request<Session>('/api/auth/login', {
    method: 'POST',
    body: JSON.stringify(credentials),
  });
}

export function register(registration: Registration): Promise<Session> {
  return request<Session>('/api/auth/register', {
    method: 'POST',
    body: JSON.stringify(registration),
  });
}

export function getSession(): Promise<Session> {
  return request<Session>('/api/auth/me');
}

export function logout(): Promise<void> {
  return requestVoid('/api/auth/logout', { method: 'POST' });
}

export function changePassword(change: PasswordChange): Promise<void> {
  return requestVoid('/api/auth/change-password', {
    method: 'POST',
    body: JSON.stringify(change),
  });
}
