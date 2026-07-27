// elicitation.ts — pure elicitation-schema normalization (no DOM).
//
// Faithful port of the pure helpers in wwwroot/js/agent/elicitation.js
// (_readElicitationOptions, _normalizeElicitationOption, _defaultOptionValue,
// _isSupplementalElicitationField, and the field-ordering rule). The form
// Schema shaping is shared by the timeline elicitation presentation.
const SUPPLEMENT_PATTERN = /\b(other|custom|response|answer|comment|note|details?)\b/;
function isRecord(value) {
    return !!value && typeof value === 'object';
}
export function normalizeElicitationOption(value, title, description) {
    const fallback = value === null || value === undefined ? '' : String(value);
    return {
        value,
        title: title === undefined || title === null || title === '' ? fallback : String(title),
        description: description === undefined || description === null || description === '' ? '' : String(description)
    };
}
export function readElicitationOptions(property) {
    if (!isRecord(property))
        return [];
    if (Array.isArray(property.oneOf)) {
        return property.oneOf
            .filter((item) => isRecord(item) && Object.prototype.hasOwnProperty.call(item, 'const'))
            .map((item) => normalizeElicitationOption(item.const, item.title, item.description));
    }
    if (Array.isArray(property.anyOf)) {
        return property.anyOf
            .filter((item) => isRecord(item) && Object.prototype.hasOwnProperty.call(item, 'const'))
            .map((item) => normalizeElicitationOption(item.const, item.title, item.description));
    }
    if (Array.isArray(property.enum)) {
        return property.enum.map((value) => normalizeElicitationOption(value, null, null));
    }
    return [];
}
export function defaultOptionValue(options, defaultValue, selectFirst) {
    if (defaultValue !== undefined && defaultValue !== null) {
        const exact = options.find((option) => option.value === defaultValue);
        if (exact)
            return exact.value;
    }
    return selectFirst && options.length ? options[0].value : undefined;
}
export function isSupplementalElicitationField(name, property, required) {
    if (required || property.type !== 'string')
        return false;
    const text = ((name || '') + ' ' + (String(property.title ?? ''))).toLowerCase();
    return SUPPLEMENT_PATTERN.test(text);
}
/**
 * Orders schema fields the way the legacy form did: required/primary fields
 * first (in declaration order), supplemental free-text fields last.
 */
export function orderElicitationFields(schema) {
    const properties = isRecord(schema) && isRecord(schema.properties) ? schema.properties : {};
    const requiredList = isRecord(schema) && Array.isArray(schema.required) ? schema.required : [];
    const required = new Set(requiredList);
    const entries = Object.entries(properties).map(([name, rawProperty], index) => {
        const property = (isRecord(rawProperty) ? rawProperty : {});
        return {
            name,
            property,
            index,
            isSupplement: isSupplementalElicitationField(name, property, required.has(name))
        };
    });
    return entries.sort((a, b) => Number(a.isSupplement) - Number(b.isSupplement) || a.index - b.index);
}
