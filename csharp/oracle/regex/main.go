// Development differential oracle only; never shipped or invoked by the backend.
package main

import (
	"bufio"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"os"

	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/diagnostics"
	"github.com/microsoft/TypeScript/tsc/internal/parser"
	"github.com/microsoft/TypeScript/tsc/internal/scanner"
)

func main() {
	lines := bufio.NewScanner(os.Stdin)
	lines.Buffer(make([]byte, 4096), 64*1024*1024)
	output := json.NewEncoder(os.Stdout)
	for lines.Scan() {
		var request struct {
			Text   string `json:"text"`
			Target int    `json:"target"`
			Path   string `json:"path"`
		}
		if err := json.Unmarshal(lines.Bytes(), &request); err != nil {
			panic(err)
		}
		bytes, err := base64.StdEncoding.DecodeString(request.Text)
		if err != nil {
			panic(err)
		}
		text := string(bytes)
		if request.Path != "" {
			bytes, err := os.ReadFile(request.Path)
			if err != nil {
				panic(err)
			}
			file := parser.ParseSourceFile(ast.SourceFileParseOptions{FileName: request.Path}, string(bytes), core.ScriptKindTS)
			stack := []*ast.Node{file.AsNode()}
			patterns := make([]string, 0)
			for len(stack) > 0 {
				node := stack[len(stack)-1]
				stack = stack[:len(stack)-1]
				if node.Kind == ast.KindRegularExpressionLiteral {
					patterns = append(patterns, node.Text())
				}
				node.ForEachChild(func(child *ast.Node) bool { stack = append(stack, child); return false })
			}
			if err := output.Encode(patterns); err != nil {
				panic(err)
			}
			continue
		}
		s := scanner.NewScanner()
		s.SetText(text)
		if request.Target != 0 {
			target := core.ScriptTargetESNext
			if request.Target < 2015 {
				target = core.ScriptTargetES5
			} else if request.Target <= 2025 {
				target = core.ScriptTarget(int(core.ScriptTargetES2015) + request.Target - 2015)
			}
			s.SetScriptTarget(target)
		}
		errors := make([]any, 0)
		s.SetOnError(func(message *diagnostics.Message, start, length int, args ...any) {
			arguments := make([]string, 0, len(args))
			for _, arg := range args {
				arguments = append(arguments, fmt.Sprint(arg))
			}
			errors = append(errors, []any{int(message.Code()), start, length, arguments})
		})
		s.Scan()
		kind := s.ReScanSlashToken(true)
		if err := output.Encode([]any{int(kind), s.TokenEnd(), int(s.TokenFlags()), base64.StdEncoding.EncodeToString([]byte(s.TokenValue())), errors}); err != nil {
			panic(err)
		}
	}
	if err := lines.Err(); err != nil {
		panic(err)
	}
}
