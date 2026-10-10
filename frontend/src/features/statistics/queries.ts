import { keepPreviousData, useQuery } from '@tanstack/react-query';
import type { UseQueryResult } from '@tanstack/react-query';
import { getPopulation, getPopulationAreas } from '../../api/statistics';
import type { ApiError } from '../../api/client';
import type { Population, PopulationAreas } from './types';

export const statisticsKeys = {
  all: ['statistics'] as const,
  areas: () => [...statisticsKeys.all, 'population-areas'] as const,
  population: (area: string) => [...statisticsKeys.all, 'population', area] as const,
};

/**
 * Statistics Denmark publishes these quarterly and the API caches them for an hour, so an hour
 * here too: refetching sooner only returns the same figure.
 */
const PublishedFigureStaleMs = 60 * 60 * 1000;

// retry: false on both. A 503 arrives after the API's own retry budget for the source is spent
// (ADR 0014), and a 404 is the answer: retrying either only keeps the user on "Loading…" longer.

export function usePopulationAreas(): UseQueryResult<PopulationAreas, ApiError> {
  return useQuery({
    queryKey: statisticsKeys.areas(),
    queryFn: getPopulationAreas,
    staleTime: PublishedFigureStaleMs,
    retry: false,
  });
}

export function usePopulation(area: string): UseQueryResult<Population, ApiError> {
  return useQuery({
    queryKey: statisticsKeys.population(area),
    queryFn: () => getPopulation(area),
    staleTime: PublishedFigureStaleMs,
    retry: false,
    // Keep the last area's figure on screen while the next loads, instead of a jump to "Loading…".
    placeholderData: keepPreviousData,
  });
}
