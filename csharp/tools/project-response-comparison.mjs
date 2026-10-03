// These original tests enumerate two overlay roots through a Go map. Repeated
// unchanged-reference controls retain both orders in phase7-project-ordering.json.
export const unorderedProjectRoots = new Set([
    "TestProjectProgramUpdateKind/NewFiles_on_root_addition",
    "TestContentMapperCreatedFileAdoptedByConfiguredProject",
]);

export function comparableProjectRoots(value, name) {
    if (!value?.projects || !unorderedProjectRoots.has(name)) return value;
    return { ...value, projects: value.projects.map(project => ({ ...project,
        roots: [...project.roots].sort(),
        sources: [...project.sources].sort((a, b) => a.fileName < b.fileName ? -1 : a.fileName > b.fileName ? 1 : 0),
    })) };
}
