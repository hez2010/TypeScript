// Development-only access to the pinned module-specifier naming algorithms.
package modulespecifiers

import (
	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/collections"
	"github.com/microsoft/TypeScript/tsc/internal/core"
)

type CSharpPathHost struct {
	ModuleSpecifierGenerationHost
	Directory   string
	Sensitive   bool
	DefaultMode core.ResolutionMode
	Exists      func(string) bool
}

func (h *CSharpPathHost) GetCurrentDirectory() string     { return h.Directory }
func (h *CSharpPathHost) UseCaseSensitiveFileNames() bool { return h.Sensitive }
func (h *CSharpPathHost) FileExists(path string) bool     { return h.Exists(path) }
func (h *CSharpPathHost) GetDefaultResolutionModeForFile(file ast.HasFileName) core.ResolutionMode {
	return h.DefaultMode
}

func CSharpSpecifierPaths(operation string, file *ast.SourceFile, options *core.CompilerOptions, host *CSharpPathHost,
	mode core.ResolutionMode, preference, oldSpecifier, target, sourceDirectory, baseDirectory string,
	roots []string, endings []ModuleSpecifierEnding, paths *collections.OrderedMap[string, []string]) any {
	switch operation {
	case "endings":
		endings := GetAllowedEndingsInPreferredOrder(UserPreferences{ImportModuleSpecifierEnding: ImportModuleSpecifierEndingPreference(preference)}, host, options, file, oldSpecifier, mode)
		result := make([]int, len(endings))
		for i, ending := range endings {
			result[i] = int(ending)
		}
		return result
	case "process":
		return processEnding(target, endings, options, host)
	case "roots":
		return tryGetModuleNameFromRootDirs(roots, target, sourceDirectory, endings, options, host)
	case "paths":
		return tryGetModuleNameFromPaths(target, paths, endings, baseDirectory, host, options)
	case "non-js":
		return TryGetRealFileNameForNonJSDeclarationFileName(target)
	}
	panic("Unknown specifier naming operation")
}
