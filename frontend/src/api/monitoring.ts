import type { MonitoringAccess } from '../features/monitoring/types';
import { request } from './client';

export function getMonitoringAccess(): Promise<MonitoringAccess> {
  return request<MonitoringAccess>('/api/monitoring/access');
}
