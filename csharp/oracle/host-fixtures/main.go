// Extract fixture inputs from Go syntax without executing compiler code in the C# candidate.
package main

import (
	"encoding/json"
	"fmt"
	goast "go/ast"
	goparser "go/parser"
	"go/token"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
)

func main() {
	if len(os.Args) != 2 {
		panic("fixtures <reference-tsc-dir>")
	}
	root := os.Args[1]
	cases := []map[string]any{}
	unhandled := []string{}
	expandedLoops := []map[string]any{}
	for _, name := range []string{"internal/tspath/path_test.go", "internal/tspath/untitled_test.go", "internal/tspath/startsWithDirectory_test.go", "internal/tsoptions/commandlineparser_test.go", "internal/tsoptions/tsconfigparsing_test.go", "internal/vfs/vfsmatch/vfsmatch_test.go"} {
		fset := token.NewFileSet()
		file, err := goparser.ParseFile(fset, filepath.Join(root, name), nil, 0)
		if err != nil {
			panic(err)
		}
		variables := map[string]goast.Expr{}
		functions := map[string]*goast.FuncDecl{}
		goast.Inspect(file, func(n goast.Node) bool {
			if declaration, ok := n.(*goast.FuncDecl); ok {
				functions[declaration.Name.Name] = declaration
			}
			if spec, ok := n.(*goast.ValueSpec); ok {
				for i, n := range spec.Names {
					if i < len(spec.Values) {
						variables[n.Name] = spec.Values[i]
					}
				}
			}
			return true
		})
		var eval func(goast.Expr) (any, bool)
		eval = func(expr goast.Expr) (any, bool) {
			switch value := expr.(type) {
			case *goast.BasicLit:
				if value.Kind == token.STRING {
					s, e := strconv.Unquote(value.Value)
					return s, e == nil
				}
				if value.Kind == token.INT {
					number, err := strconv.Atoi(value.Value)
					return number, err == nil
				}
			case *goast.Ident:
				if v, ok := variables[value.Name]; ok {
					return eval(v)
				}
				if value.Name == "nil" {
					return nil, true
				}
				if value.Name == "true" || value.Name == "false" {
					return value.Name == "true", true
				}
			case *goast.BinaryExpr:
				if value.Op == token.ADD {
					a, ok := eval(value.X)
					b, ok2 := eval(value.Y)
					if ok && ok2 {
						aa, o := a.(string)
						bb, p := b.(string)
						if o && p {
							return aa + bb, true
						}
					}
				}
			case *goast.CompositeLit:
				if _, ok := value.Type.(*goast.MapType); ok {
					m := map[string]string{}
					for _, element := range value.Elts {
						kv, ok := element.(*goast.KeyValueExpr)
						if !ok {
							return nil, false
						}
						k, o := eval(kv.Key)
						v, p := eval(kv.Value)
						kk, oo := k.(string)
						vv, pp := v.(string)
						if !o || !p || !oo || !pp {
							return nil, false
						}
						m[kk] = vv
					}
					return m, true
				}
				if _, ok := value.Type.(*goast.ArrayType); ok {
					a := []string{}
					for _, element := range value.Elts {
						v, o := eval(element)
						vv, p := v.(string)
						if !o || !p {
							return nil, false
						}
						a = append(a, vv)
					}
					return a, true
				}
			}
			return nil, false
		}
		seen := map[string]bool{}
		expandedFunctions := map[*goast.FuncDecl]int{}
		containingFunction := func(position token.Pos) *goast.FuncDecl {
			for _, declaration := range functions {
				if declaration.Pos() <= position && position <= declaration.End() {
					return declaration
				}
			}
			return nil
		}
		goast.Inspect(file, func(n goast.Node) bool {
			location := func() string { return fmt.Sprintf("%s:%d", name, fset.Position(n.Pos()).Line) }
			if strings.Contains(name, "tspath") {
				if literal, ok := n.(*goast.BasicLit); ok && literal.Kind == token.STRING {
					value, _ := strconv.Unquote(literal.Value)
					if !seen[value] {
						seen[value] = true
						for _, other := range []string{value, "/a/b", "../c", "C:/a/b", "file:///c:/x"} {
							if tspathRooted(value) != tspathRooted(other) {
								continue
							}
							cases = append(cases, map[string]any{"name": location() + ":" + other, "mode": "path", "path": value, "other": other, "sensitive": true})
						}
					}
				}
				return true
			}
			composite, ok := n.(*goast.CompositeLit)
			if !ok {
				return true
			}
			if strings.Contains(name, "tsoptions") {
				if _, isMap := composite.Type.(*goast.MapType); isMap {
					if evaluated, ok := eval(composite); ok {
						if files, ok := evaluated.(map[string]string); ok {
							keys := []string{}
							for key := range files {
								if strings.HasPrefix(key, "/") && strings.HasSuffix(key, ".json") && !strings.HasSuffix(key, "/package.json") {
									keys = append(keys, key)
								}
							}
							sort.Strings(keys)
							for _, key := range keys {
								for _, sensitive := range []bool{true, false} {
									cases = append(cases, map[string]any{"name": location() + ":" + key + fmt.Sprint(sensitive), "mode": "config", "directory": filepath.ToSlash(filepath.Dir(key)), "path": key, "sensitive": sensitive, "files": files})
								}
							}
						}
					}
				}
			}
			if strings.Contains(name, "commandlineparser") {
				if typ, ok := composite.Type.(*goast.ArrayType); ok {
					if ident, ok := typ.Elt.(*goast.Ident); ok && ident.Name == "string" {
						if args, ok := eval(composite); ok {
							build := false
							if declaration := functions["TestParseBuildCommandLine"]; declaration != nil {
								build = composite.Pos() >= declaration.Pos() && composite.End() <= declaration.End()
							}
							cases = append(cases, map[string]any{"name": location(), "mode": "cli", "arguments": args, "directory": "/project", "sensitive": true, "build": build})
						}
					}
				}
				return true
			}
			values := map[string]goast.Expr{}
			for _, element := range composite.Elts {
				if pair, ok := element.(*goast.KeyValueExpr); ok {
					if key, ok := pair.Key.(*goast.Ident); ok {
						values[key.Name] = pair.Value
					}
				}
			}
			if content, ok := eval(values["contentMappers"]); ok {
				function := containingFunction(composite.Pos())
				fileMap := map[string]string{"/tsconfig.json": "{\"contentMappers\":" + content.(string) + "}", "/app.ts": "export {};"}
				goast.Inspect(function, func(n goast.Node) bool {
					if pair, ok := n.(*goast.KeyValueExpr); ok {
						key, o := eval(pair.Key)
						value, p := eval(pair.Value)
						k, oo := key.(string)
						v, pp := value.(string)
						if o && p && oo && pp && strings.HasPrefix(k, "/node_modules/") {
							fileMap[k] = v
						}
					}
					if assignment, ok := n.(*goast.AssignStmt); ok {
						for i, left := range assignment.Lhs {
							if index, ok := left.(*goast.IndexExpr); ok && i < len(assignment.Rhs) {
								key, o := eval(index.Index)
								value, p := eval(assignment.Rhs[i])
								k, oo := key.(string)
								v, pp := value.(string)
								if o && p && oo && pp && strings.HasPrefix(k, "/node_modules/") {
									fileMap[k] = v
								}
							}
						}
					}
					return true
				})
				sensitive := true
				if flag, ok := eval(values["useCaseSensitiveFileNames"]); ok {
					sensitive = flag.(bool)
				}
				cases = append(cases, map[string]any{"name": location(), "mode": "config", "directory": "/", "path": "/tsconfig.json", "sensitive": sensitive, "existing": map[string]any{"runExternalCode": true}, "files": fileMap})
				expandedFunctions[function]++
			}
			if strings.Contains(name, "vfsmatch") && values["usage"] != nil {
				specs := []string{}
				if list, ok := eval(values["specs"]); ok {
					specs, _ = list.([]string)
				}
				if single, ok := eval(values["spec"]); ok {
					specs = []string{single.(string)}
				}
				base, baseOK := eval(values["basePath"])
				sensitive, sensitiveOK := eval(values["useCaseSensitiveFileNames"])
				usage, usageOK := values["usage"].(*goast.Ident)
				if len(specs) > 0 && baseOK && sensitiveOK && usageOK {
					for _, key := range []string{"paths", "matchingPaths", "nonMatchingPaths"} {
						if paths, ok := eval(values[key]); ok {
							for _, path := range paths.([]string) {
								for _, spec := range specs {
									cases = append(cases, map[string]any{"name": location() + ":" + spec + ":" + path, "mode": "glob", "path": spec, "other": path, "directory": base, "sensitive": sensitive, "exclude": usage.Name == "UsageExclude"})
								}
							}
						}
					}
				}
			}
			if host, ok := values["host"].(*goast.Ident); ok && values["expect"] != nil {
				var files any
				if declaration := functions[host.Name]; declaration != nil {
					goast.Inspect(declaration, func(n goast.Node) bool {
						if call, ok := n.(*goast.CallExpr); ok && len(call.Args) > 0 {
							if selector, ok := call.Fun.(*goast.SelectorExpr); ok && selector.Sel.Name == "FromMap" {
								files, _ = eval(call.Args[0])
							}
						}
						return true
					})
				}
				if files == nil {
					unhandled = append(unhandled, location())
					return true
				}
				input := map[string]any{"name": location(), "mode": "readDirectory", "files": files, "directory": "/", "path": "/dev", "sensitive": host.Name == "caseSensitiveHost", "depth": -1}
				for from, to := range map[string]string{"includes": "includes", "excludes": "excludes", "extensions": "extensions", "depth": "depth", "path": "path", "currentDir": "directory"} {
					if expr := values[from]; expr != nil {
						if value, ok := eval(expr); ok {
							input[to] = value
						} else {
							unhandled = append(unhandled, location()+":"+from)
							return true
						}
					}
				}
				cases = append(cases, input)
			}
			if config, ok := eval(values["config"]); ok && values["configName"] != nil {
				if configName, ok := eval(values["configName"]); ok {
					path := "/apath/" + configName.(string)
					cases = append(cases, map[string]any{"name": location(), "mode": "config", "directory": "/apath", "path": path, "sensitive": true, "files": map[string]string{path: config.(string), "/apath/a.ts": "", "/apath/b.ts": ""}})
					expandedFunctions[containingFunction(composite.Pos())]++
				}
			}
			if text, ok := values["jsonText"]; ok {
				jsonText, ok := eval(text)
				base, baseOK := eval(values["basePath"])
				config, configOK := eval(values["configFileName"])
				files, filesOK := eval(values["allFileList"])
				if !ok || !baseOK || !configOK || !filesOK {
					function := containingFunction(composite.Pos())
					if count := expandedFunctions[function]; count > 0 {
						expandedLoops = append(expandedLoops, map[string]any{"source": location(), "function": function.Name.Name, "expandedCases": count})
					} else {
						unhandled = append(unhandled, location())
					}
					return true
				}
				baseText := base.(string)
				if baseText == "" {
					baseText = "/"
				}
				configText := config.(string)
				if configText == "" {
					configText = "tsconfig.json"
				}
				if !strings.HasPrefix(configText, "/") {
					configText = strings.TrimSuffix(baseText, "/") + "/" + configText
				}
				fileMap := files.(map[string]string)
				fileMap[configText] = jsonText.(string)
				input := map[string]any{"name": location(), "mode": "config", "path": configText, "directory": baseText, "files": fileMap, "sensitive": true}
				if expr := values["existingOptions"]; expr != nil {
					pointer, ok := expr.(*goast.UnaryExpr)
					if !ok {
						unhandled = append(unhandled, location()+":existingOptions")
						return true
					}
					options, ok := pointer.X.(*goast.CompositeLit)
					if !ok {
						unhandled = append(unhandled, location()+":existingOptions")
						return true
					}
					if len(options.Elts) != 1 {
						unhandled = append(unhandled, location()+":existingOptions")
						return true
					}
					pair, ok := options.Elts[0].(*goast.KeyValueExpr)
					if !ok {
						unhandled = append(unhandled, location()+":existingOptions")
						return true
					}
					key, ok := pair.Key.(*goast.Ident)
					if !ok || key.Name != "RunExternalCode" {
						unhandled = append(unhandled, location()+":existingOptions")
						return true
					}
					input["existing"] = map[string]any{"runExternalCode": true}
				}
				cases = append(cases, input)
			}
			return true
		})
	}
	if err := json.NewEncoder(os.Stdout).Encode(map[string]any{"cases": cases, "unhandled": unhandled, "expandedLoops": expandedLoops}); err != nil {
		panic(err)
	}
}

func tspathRooted(s string) bool {
	return strings.HasPrefix(s, "/") || strings.Contains(s, ":/") || strings.Contains(s, ":\\") || strings.HasPrefix(s, "^") || len(s) == 2 && s[1] == ':'
}
