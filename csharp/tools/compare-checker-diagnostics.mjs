const canonical = value =>
    Array.isArray(value) ? value.map(canonical)
        : value !== null && typeof value === "object"
        ? Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])])) : value;

// Top-level diagnostic order is normalized; nested chains, related information
// and duplicate counts remain significant.
export function sameDiagnostics(actual, expected) {
    if (!Array.isArray(actual) || !Array.isArray(expected)) return false;
    const collection = diagnostics => diagnostics.map(d => JSON.stringify(canonical(d))).sort();
    return JSON.stringify(collection(actual)) === JSON.stringify(collection(expected));
}
