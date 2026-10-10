import type { Population, PopulationAreas } from '../features/statistics/types';
import { request } from './client';

export function getPopulation(area: string): Promise<Population> {
  return request<Population>(`/api/statistics/population?area=${encodeURIComponent(area)}`);
}

export function getPopulationAreas(): Promise<PopulationAreas> {
  return request<PopulationAreas>('/api/statistics/population/areas');
}
