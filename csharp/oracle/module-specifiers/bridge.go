// Development-only access to the pinned module-specifier naming algorithms.
package modulespecifiers

import (
	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/collections"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/module"
	"github.com/microsoft/TypeScript/tsc/internal/outputpaths"
	"github.com/microsoft/TypeScript/tsc/internal/packagejson"
	"github.com/microsoft/TypeScript/tsc/internal/tspath"
)

type CSharpPathHost struct {
	ModuleSpecifierGenerationHost
	Directory          string
	Sensitive          bool
	DefaultMode        core.ResolutionMode
	Exists             func(string) bool
	Read               func(string) (string, bool)
	CommonDirectory    string
	MapperExtensions   []string
	GlobalTypingsCache string
}

func (h *CSharpPathHost) GetGlobalTypingsCacheLocation() string { return h.GlobalTypingsCache }

func CSharpNodeModuleSpecifier(file *ast.SourceFile, options *core.CompilerOptions, host *CSharpPathHost,
	target, preference string, mode core.ResolutionMode, packageNameOnly, redirect bool,
) string {
	return tryGetModuleNameAsNodeModule(ModulePath{FileName: target, IsInNodeModules: true, IsRedirect: redirect},
		getInfo(file.FileName(), host), file, host, options,
		UserPreferences{ImportModuleSpecifierEnding: ImportModuleSpecifierEndingPreference(preference)}, packageNameOnly, mode)
}

func (h *CSharpPathHost) CommonSourceDirectory() string     { return h.CommonDirectory }
func (h *CSharpPathHost) ContentMapperExtensions() []string { return h.MapperExtensions }
func (h *CSharpPathHost) GetNearestAncestorDirectoryWithPackageJson(directory string) string {
	for {
		if h.Exists(tspath.CombinePaths(directory, "package.json")) {
			return directory
		}
		parent := tspath.GetDirectoryPath(directory)
		if parent == directory || directory == "" {
			return ""
		}
		directory = parent
	}
}

func (h *CSharpPathHost) GetPackageJsonInfo(path string) *packagejson.InfoCacheEntry {
	text, ok := h.Read(path)
	if !ok {
		return nil
	}
	fields, err := packagejson.Parse([]byte(text))
	return &packagejson.InfoCacheEntry{PackageDirectory: tspath.GetDirectoryPath(path), DirectoryExists: true, Contents: &packagejson.PackageJson{Fields: fields, Parseable: err == nil}}
}

func CSharpPackageSpecifiers(operation string, options *core.CompilerOptions, host *CSharpPathHost, target, directory, name, sourceDirectory string,
	value packagejson.ExportsOrImports, conditions []string, mode MatchingMode, imports, preferTypeScript bool, importMode core.ResolutionMode,
) any {
	switch operation {
	case "package-map":
		return tryGetModuleNameFromExportsOrImports(options, host, target, directory, name, value, conditions, mode, imports, preferTypeScript)
	case "package-exports":
		return tryGetModuleNameFromExports(options, host, target, directory, name, value, conditions)
	case "package-imports":
		return tryGetModuleNameFromPackageJsonImports(target, sourceDirectory, options, host, importMode, preferTypeScript)
	case "package-conditions":
		return module.GetConditions(options, importMode)
	case "output-paths":
		return []string{outputpaths.GetOutputJSFileNameWorker(target, options, host), outputpaths.GetOutputDeclarationFileNameWorker(target, options, host)}
	}
	panic("Unknown package naming operation")
}

func (h *CSharpPathHost) GetCurrentDirectory() string     { return h.Directory }
func (h *CSharpPathHost) UseCaseSensitiveFileNames() bool { return h.Sensitive }
func (h *CSharpPathHost) FileExists(path string) bool     { return h.Exists(path) }
func (h *CSharpPathHost) GetDefaultResolutionModeForFile(file ast.HasFileName) core.ResolutionMode {
	return h.DefaultMode
}

func CSharpSpecifierPaths(operation string, file *ast.SourceFile, options *core.CompilerOptions, host *CSharpPathHost,
	mode core.ResolutionMode, preference, oldSpecifier, target, sourceDirectory, baseDirectory string,
	roots []string, endings []ModuleSpecifierEnding, paths *collections.OrderedMap[string, []string],
) any {
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
