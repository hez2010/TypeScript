// Development JSDoc diagnostic oracle, never used by the C# runtime.
package main

import (
	"bufio"
	"encoding/base64"
	"encoding/json"
	"os"

	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/parser"
	"github.com/microsoft/TypeScript/tsc/internal/testrunner"
	"github.com/microsoft/TypeScript/tsc/internal/tspath"
)

func main() {
	lines := bufio.NewScanner(os.Stdin)
	lines.Buffer(make([]byte, 4096), 64*1024*1024)
	output := json.NewEncoder(os.Stdout)
	for lines.Scan() {
		var request struct {
			Text     string `json:"text"`
			FileName string `json:"fileName"`
			Mode     string `json:"mode"`
			Path     string `json:"path"`
		}
		if err := json.Unmarshal(lines.Bytes(), &request); err != nil {
			panic(err)
		}
		bytes, err := base64.StdEncoding.DecodeString(request.Text)
		if request.Path != "" {
			bytes, err = os.ReadFile(request.Path)
		}
		if err != nil {
			panic(err)
		}
		if request.Mode == "units" {
			units, _, _, _, err := testrunner.ParseTestFilesAndSymlinks(string(bytes), request.Path, func(name string, content string, _ map[string]string) (map[string]string, error) {
				return map[string]string{"fileName": name, "text": base64.StdEncoding.EncodeToString([]byte(content))}, nil
			})
			if err != nil {
				panic(err)
			}
			if err := output.Encode(units); err != nil {
				panic(err)
			}
			continue
		}
		name := tspath.GetNormalizedAbsolutePath(request.FileName, "/fixtures")
		file := parser.ParseSourceFile(ast.SourceFileParseOptions{FileName: name}, string(bytes), core.EnsureScriptKindFromFileName(name))
		positions := ast.ComputePositionMap(string(bytes))
		pos := positions.UTF8ToUTF16
		var documentation any
		if request.Mode == "documentation" {
			hosts := make([]any, 0)
			stack := []*ast.Node{file.AsNode()}
			for len(stack) > 0 {
				host := stack[len(stack)-1]
				stack = stack[:len(stack)-1]
				children := []*ast.Node{}
				host.ForEachChild(func(child *ast.Node) bool { children = append(children, child); return false })
				for i := len(children) - 1; i >= 0; i-- {
					stack = append(stack, children[i])
				}
				comments := host.JSDoc(file)
				if len(comments) == 0 {
					continue
				}
				docs := make([]any, 0)
				for _, comment := range comments {
					rows := make([]any, 0)
					pending := []*ast.Node{comment}
					for len(pending) > 0 {
						node := pending[len(pending)-1]
						pending = pending[:len(pending)-1]
						children := []*ast.Node{}
						node.ForEachChild(func(child *ast.Node) bool { children = append(children, child); return false })
						rows = append(rows, []any{int(node.Kind), pos(node.Pos()), pos(node.End()), uint32(node.Flags), len(children), ast.CSharpScalarProperties(node), ast.CSharpListProperties(node, pos)})
						for i := len(children) - 1; i >= 0; i-- {
							pending = append(pending, children[i])
						}
					}
					docs = append(docs, rows)
				}
				hosts = append(hosts, []any{int(host.Kind), pos(host.Pos()), pos(host.End()), docs})
			}
			documentation = hosts
		}
		result := make([]any, 0)
		for _, d := range file.JSDocDiagnostics() {
			result = append(result, []any{int(d.Code()), positions.UTF8ToUTF16(d.Pos()), positions.UTF8ToUTF16(d.End()) - positions.UTF8ToUTF16(d.Pos())})
		}
		var payload any = result
		if request.Mode == "documentation" {
			payload = []any{documentation, result}
		}
		if err := output.Encode(payload); err != nil {
			panic(err)
		}
	}
	if err := lines.Err(); err != nil {
		panic(err)
	}
}
