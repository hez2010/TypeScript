// Development metadata oracle; independent of the production C# compiler.
package main

import (
	"bufio"
	"encoding/base64"
	"encoding/json"
	"os"
	"sort"

	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/parser"
)

func main() {
	lines := bufio.NewScanner(os.Stdin)
	lines.Buffer(make([]byte, 4096), 64*1024*1024)
	output := json.NewEncoder(os.Stdout)
	for lines.Scan() {
		var request struct {
			Text     string `json:"text"`
			FileName string `json:"fileName"`
			Force    bool   `json:"force"`
			JSX      bool   `json:"jsx"`
		}
		if err := json.Unmarshal(lines.Bytes(), &request); err != nil {
			panic(err)
		}
		bytes, err := base64.StdEncoding.DecodeString(request.Text)
		if err != nil {
			panic(err)
		}
		if request.FileName == "" {
			request.FileName = "/test.ts"
		}
		file := parser.ParseSourceFile(ast.SourceFileParseOptions{FileName: request.FileName, ExternalModuleIndicatorOptions: ast.ExternalModuleIndicatorOptions{Force: request.Force, JSX: request.JSX}}, string(bytes), core.EnsureScriptKindFromFileName(request.FileName))
		positions := ast.ComputePositionMap(string(bytes))
		pos := positions.UTF8ToUTF16
		pragmas := make([]any, 0)
		for _, pragma := range file.Pragmas {
			names := make([]string, 0)
			for name := range pragma.Args {
				names = append(names, name)
			}
			sort.Strings(names)
			args := make([]any, 0)
			for _, name := range names {
				arg := pragma.Args[name]
				args = append(args, []any{arg.Name, arg.Value, pos(arg.Pos()), pos(arg.End())})
			}
			pragmas = append(pragmas, []any{pragma.Name, int(pragma.Kind), pos(pragma.Pos()), pos(pragma.End()), pragma.HasTrailingNewLine, args})
		}
		refs := func(values []*ast.FileReference) []any {
			result := make([]any, 0)
			for _, value := range values {
				result = append(result, []any{value.FileName, pos(value.Pos()), pos(value.End()), int(value.ResolutionMode), value.Preserve})
			}
			return result
		}
		var check any
		if value := file.CheckJsDirective; value != nil {
			check = []any{value.Enabled, pos(value.Range.Pos()), pos(value.Range.End())}
		}
		var external any
		if value := file.ExternalModuleIndicator; value != nil {
			external = []any{int(value.Kind), pos(value.Pos()), pos(value.End())}
		}
		diagnostics := make([]any, 0)
		for _, d := range file.Diagnostics() {
			diagnostics = append(diagnostics, []any{int(d.Code()), pos(d.Pos()), pos(d.End()) - pos(d.Pos())})
		}
		nodes := func(values []*ast.Node) []any {
			result := make([]any, 0)
			for _, value := range values {
				result = append(result, []any{int(value.Kind), value.Text(), pos(value.Pos()), pos(value.End())})
			}
			return result
		}
		ambient := file.AmbientModuleNames
		if ambient == nil {
			ambient = []string{}
		}
		if err := output.Encode([]any{pragmas, refs(file.ReferencedFiles), refs(file.TypeReferenceDirectives), refs(file.LibReferenceDirectives), check, external, diagnostics, nodes(file.Imports()), nodes(file.ModuleAugmentations), ambient}); err != nil {
			panic(err)
		}
	}
	if err := lines.Err(); err != nil {
		panic(err)
	}
}
