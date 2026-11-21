import schema from '../docs/openapi.json' with { type: 'json' };

export const nestedPageSize = schema.components.schemas.JobListScopeDto.properties.limit.default;
