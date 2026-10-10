import type { components } from '../../api/schema';

// Aliases over the generated schema, as features/products/types.ts does: they stop compiling the
// moment the backend renames or drops a property.
export type Population = components['schemas']['PopulationResponse'];

export type PopulationAreas = components['schemas']['PopulationAreasResponse'];
