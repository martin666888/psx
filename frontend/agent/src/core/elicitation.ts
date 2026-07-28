// elicitation.ts — pure elicitation-schema normalization (no DOM).
//
// Faithful port of the pure helpers in wwwroot/js/agent/elicitation.js
// (_readElicitationOptions, _normalizeElicitationOption, _defaultOptionValue,
// _isSupplementalElicitationField, and the field-ordering rule). The form
// Schema shaping is shared by the timeline elicitation presentation.

export interface ElicitationOption {
  value: unknown;
  title: string;
  description: string;
}

export interface ElicitationProperty {
  type?: unknown;
  title?: unknown;
  description?: unknown;
  default?: unknown;
  items?: unknown;
  oneOf?: unknown;
  anyOf?: unknown;
  enum?: unknown;
}

export interface ElicitationField {
  name: string;
  property: ElicitationProperty;
  index: number;
  isSupplement: boolean;
}

const SUPPLEMENT_PATTERN = /\b(other|custom|response|answer|comment|note|details?)\b/;

function isRecord(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === 'object';
}

export function normalizeElicitationOption(
  value: unknown,
  title: unknown,
  description: unknown
): ElicitationOption {
  const fallback = value === null || value === undefined ? '' : String(value);
  return {
    value,
    title: title === undefined || title === null || title === '' ? fallback : String(title),
    description: description === undefined || description === null || description === '' ? '' : String(description)
  };
}

export function readElicitationOptions(property: unknown): ElicitationOption[] {
  if (!isRecord(property)) return [];

  if (Array.isArray(property.oneOf)) {
    return property.oneOf
      .filter((item) => isRecord(item) && Object.prototype.hasOwnProperty.call(item, 'const'))
      .map((item) => normalizeElicitationOption((item as Record<string, unknown>).const, (item as Record<string, unknown>).title, (item as Record<string, unknown>).description));
  }

  if (Array.isArray(property.anyOf)) {
    return property.anyOf
      .filter((item) => isRecord(item) && Object.prototype.hasOwnProperty.call(item, 'const'))
      .map((item) => normalizeElicitationOption((item as Record<string, unknown>).const, (item as Record<string, unknown>).title, (item as Record<string, unknown>).description));
  }

  if (Array.isArray(property.enum)) {
    return property.enum.map((value) => normalizeElicitationOption(value, null, null));
  }

  return [];
}

export function defaultOptionValue(
  options: readonly ElicitationOption[],
  defaultValue: unknown,
  selectFirst: boolean
): unknown {
  if (defaultValue !== undefined && defaultValue !== null) {
    const exact = options.find((option) => option.value === defaultValue);
    if (exact) return exact.value;
  }
  return selectFirst && options.length ? options[0].value : undefined;
}

export function isSupplementalElicitationField(
  name: string,
  property: ElicitationProperty,
  required: boolean
): boolean {
  if (required || property.type !== 'string') return false;
  const text = ((name || '') + ' ' + (String(property.title ?? ''))).toLowerCase();
  return SUPPLEMENT_PATTERN.test(text);
}

/**
 * Reads schema fields in declaration order so each supplemental free-text
 * field ("Other" etc.) stays next to the question it belongs to; the
 * isSupplement flag only drives presentation.
 */
export function orderElicitationFields(schema: unknown): ElicitationField[] {
  const properties = isRecord(schema) && isRecord(schema.properties) ? schema.properties : {};
  const requiredList = isRecord(schema) && Array.isArray(schema.required) ? schema.required : [];
  const required = new Set(requiredList as unknown[]);

  return Object.entries(properties).map(([name, rawProperty], index) => {
    const property = (isRecord(rawProperty) ? rawProperty : {}) as ElicitationProperty;
    return {
      name,
      property,
      index,
      isSupplement: isSupplementalElicitationField(name, property, required.has(name))
    };
  });
}
