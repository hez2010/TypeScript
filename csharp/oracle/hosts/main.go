// Development-only oracle. No dependency from the C# backend or distributed executable.
package main

import (
	"bufio"
	"encoding/json"
	"os"
	"reflect"
	"strings"
	"unicode/utf16"

	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/collections"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/stringutil"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions/tsoptionstest"
	"github.com/microsoft/TypeScript/tsc/internal/tspath"
	"github.com/microsoft/TypeScript/tsc/internal/vfs/vfsmatch"
)

type request struct {
	Name, Mode, Path, Other, Directory string
	Sensitive, Build, Exclude          bool
	Arguments                          []string
	Files                              map[string]string
	Symlinks                           map[string]string
	Existing                           *core.CompilerOptions
	Includes, Excludes, Extensions     []string
	Depth                              int
}

func main() {
	lines := bufio.NewScanner(os.Stdin)
	lines.Buffer(make([]byte, 4096), 64*1024*1024)
	writer := json.NewEncoder(os.Stdout)
	for lines.Scan() {
		var input request
		if err := json.Unmarshal(lines.Bytes(), &input); err != nil {
			panic(err)
		}
		if err := writer.Encode(process(input)); err != nil {
			panic(err)
		}
	}
	if err := lines.Err(); err != nil {
		panic(err)
	}
}

func diagnosticCodes(errors []*ast.Diagnostic) []int32 {
	result := []int32{}
	for _, d := range errors {
		result = append(result, d.Code())
	}
	return result
}

func definition(name string) *tsoptions.CommandLineOption {
	for _, d := range append(append(tsoptions.OptionsDeclarations, tsoptions.OptionsForWatch...), tsoptions.OptionsForBuild...) {
		if d.Name == name {
			return d
		}
	}
	return nil
}

func optionValue(def *tsoptions.CommandLineOption, value any) any {
	if def == nil || value == nil {
		return value
	}
	if def.Kind == tsoptions.CommandLineOptionTypeEnum {
		for key, v := range def.EnumMap().Entries() {
			if reflect.DeepEqual(value, v) {
				return key
			}
		}
		return value
	}
	if def.Kind == tsoptions.CommandLineOptionTypeList {
		val := reflect.ValueOf(value)
		if val.Kind() == reflect.Slice {
			result := []any{}
			for i := 0; i < val.Len(); i++ {
				result = append(result, optionValue(def.Elements(), val.Index(i).Interface()))
			}
			return result
		}
	}
	return value
}

func rawOptions(raw any, directory string) map[string]any {
	result := map[string]any{}
	if raw, ok := raw.(*collections.OrderedMap[string, any]); ok {
		for k, v := range raw.Entries() {
			def := definition(k)
			if def != nil {
				if def.IsFilePath {
					if text, ok := v.(string); ok {
						v = tspath.GetNormalizedAbsolutePath(text, directory)
					}
				}
				if def.Kind == tsoptions.CommandLineOptionTypeList && def.Elements().IsFilePath {
					if list, ok := v.([]any); ok {
						for i, item := range list {
							if text, ok := item.(string); ok {
								list[i] = tspath.GetNormalizedAbsolutePath(text, directory)
							}
						}
						v = list
					}
				}
			}
			result[k] = optionValue(def, v)
		}
	}
	return result
}

func structOptions(input any) map[string]any {
	result := map[string]any{}
	value := reflect.ValueOf(input)
	if value.IsNil() {
		return result
	}
	value = value.Elem()
	typ := value.Type()
	for i := 0; i < value.NumField(); i++ {
		field := value.Field(i)
		name := strings.Split(typ.Field(i).Tag.Get("json"), ",")[0]
		if name == "" || name == "-" || name == "configFilePath" || field.IsZero() {
			continue
		}
		result[name] = optionValue(definition(name), field.Interface())
	}
	return result
}

func process(input request) any {
	if input.Mode == "schema" {
		result := []any{}
		for _, group := range []struct {
			name        string
			definitions []*tsoptions.CommandLineOption
		}{{"compiler", tsoptions.OptionsDeclarations}, {"build", tsoptions.BuildOpts}, {"watch", tsoptions.OptionsForWatch}} {
			seen := map[string]bool{}
			for _, definition := range group.definitions {
				if seen[definition.Name] {
					continue
				}
				seen[definition.Name] = true
				values := []string{}
				if definition.Kind == tsoptions.CommandLineOptionTypeEnum {
					for key := range definition.EnumMap().Keys() {
						values = append(values, key)
					}
				}
				if definition.Kind == tsoptions.CommandLineOptionTypeList && definition.Elements().Kind == tsoptions.CommandLineOptionTypeEnum {
					for key := range definition.Elements().EnumMap().Keys() {
						values = append(values, key)
					}
				}
				result = append(result, map[string]any{"name": definition.Name, "group": group.name, "kind": definition.Kind, "values": values})
			}
		}
		return result
	}
	if input.Mode == "path" {
		return map[string]any{"normalize": tspath.NormalizePath(input.Path), "root": tspath.GetRootLength(input.Path), "absolute": tspath.PathIsAbsolute(input.Path), "url": tspath.IsUrl(input.Path), "directory": tspath.GetDirectoryPath(input.Path), "base": tspath.GetBaseFileName(input.Path), "extension": tspath.GetAnyExtensionFromPath(input.Path, nil, false), "declaration": tspath.IsDeclarationFileName(input.Path), "combine": tspath.CombinePaths(input.Path, input.Other), "resolve": tspath.ResolvePath(input.Path, input.Other), "contains": tspath.ContainsPath(input.Path, input.Other, tspath.ComparePathsOptions{UseCaseSensitiveFileNames: input.Sensitive}), "relative": relative(input.Path, input.Other, input.Sensitive)}
	}
	if input.Mode == "glob" {
		usage := vfsmatch.UsageFiles
		if input.Exclude {
			usage = vfsmatch.UsageExclude
		}
		matcher := vfsmatch.NewSpecMatcher([]string{input.Path}, input.Directory, usage, input.Sensitive)
		return matcher != nil && matcher.MatchString(input.Other)
	}
	host := tsoptionstest.NewVFSParseConfigHostWithSymlinks(input.Files, input.Symlinks, input.Directory, input.Sensitive)
	if input.Mode == "readDirectory" {
		result := vfsmatch.ReadDirectory(host.FS(), input.Directory, input.Path, input.Extensions, input.Excludes, input.Includes, input.Depth)
		if result == nil {
			result = []string{}
		}
		return result
	}
	if input.Mode == "cli" {
		if input.Build {
			parsed := tsoptions.ParseBuildCommandLine(input.Arguments, host)
			return map[string]any{"options": rawOptions(parsed.Raw, input.Directory), "files": parsed.Projects, "diagnostics": diagnosticCodes(parsed.Errors)}
		}
		parsed := tsoptions.ParseCommandLine(input.Arguments, host)
		return map[string]any{"options": rawOptions(parsed.Raw, input.Directory), "files": parsed.FileNames(), "diagnostics": diagnosticCodes(parsed.Errors)}
	}
	parsed, errors := tsoptions.GetParsedCommandLineOfConfigFile(input.Path, input.Existing, nil, host, nil)
	if parsed == nil {
		return map[string]any{"options": map[string]any{}, "files": []string{}, "diagnostics": diagnosticCodes(errors), "references": []any{}, "compileOnSave": false}
	}
	if input.Mode == "configUnits" {
		units := func(value string) []uint16 {
			result := []uint16{}
			for len(value) > 0 {
				r, size := stringutil.DecodeJSStringRune(value)
				if r >= 0x10000 {
					high, low := utf16.EncodeRune(r)
					result = append(result, uint16(high), uint16(low))
				} else {
					result = append(result, uint16(r))
				}
				value = value[size:]
			}
			return result
		}
		files := [][]uint16{}
		for _, value := range parsed.FileNames() {
			files = append(files, units(value))
		}
		roots := [][]uint16{}
		for _, value := range parsed.CompilerOptions().TypeRoots {
			roots = append(roots, units(value))
		}
		paths := []any{}
		if parsed.CompilerOptions().Paths != nil {
			for key, value := range parsed.CompilerOptions().Paths.Entries() {
				entries := [][]uint16{}
				for _, item := range value {
					entries = append(entries, units(item))
				}
				paths = append(paths, []any{units(key), entries})
			}
		}
		return map[string]any{"files": files, "outDir": units(parsed.CompilerOptions().OutDir), "typeRoots": roots, "paths": paths, "diagnostics": diagnosticCodes(append(errors, parsed.Errors...))}
	}
	references := []any{}
	for _, ref := range parsed.ProjectReferences() {
		references = append(references, []any{ref.Path, ref.Circular})
	}
	files := parsed.FileNames()
	if files == nil {
		files = []string{}
	}
	return map[string]any{"options": structOptions(parsed.CompilerOptions()), "files": files, "diagnostics": diagnosticCodes(append(errors, parsed.Errors...)), "references": references, "compileOnSave": parsed.CompileOnSave != nil && *parsed.CompileOnSave}
}

func relative(a, b string, sensitive bool) string {
	if tspath.PathIsAbsolute(a) != tspath.PathIsAbsolute(b) {
		return b
	}
	return tspath.GetRelativePathFromDirectory(a, b, tspath.ComparePathsOptions{UseCaseSensitiveFileNames: sensitive})
}
