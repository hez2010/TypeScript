// Development oracle only. The C# backend never invokes this executable.
package main

import (
	"bufio"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"os"
	"sort"

	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/diagnostics"
	"github.com/microsoft/TypeScript/tsc/internal/parser"
	"github.com/microsoft/TypeScript/tsc/internal/scanner"
	"github.com/microsoft/TypeScript/tsc/internal/testrunner"
	"github.com/microsoft/TypeScript/tsc/internal/tspath"
	"github.com/microsoft/TypeScript/tsc/internal/vfs/osvfs"
)

type input struct {
	Name      string `json:"name"`
	FileName  string `json:"fileName"`
	Path      string `json:"path"`
	Text      string `json:"text"`
	Trivia    bool   `json:"trivia"`
	Jsx       bool   `json:"jsx"`
	Mode      string `json:"mode"`
	Details   bool   `json:"details"`
	JSDetails bool   `json:"jsDetails"`
}

func main() {
	lines := bufio.NewScanner(os.Stdin)
	lines.Buffer(make([]byte, 4096), 64*1024*1024)
	output := json.NewEncoder(os.Stdout)
	for lines.Scan() {
		var request input
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
		text := string(bytes)
		if request.Mode == "units" {
			if request.Path != "" {
				var ok bool
				text, ok = osvfs.FS().ReadFile(request.Path)
				if !ok {
					panic("Cannot read test source")
				}
			}
			encode := func(value string) string { return base64.StdEncoding.EncodeToString([]byte(value)) }
			pairs := func(values map[string]string) [][2]string {
				keys := make([]string, 0, len(values))
				for key := range values {
					keys = append(keys, key)
				}
				sort.Strings(keys)
				rows := make([][2]string, 0, len(keys))
				for _, key := range keys {
					rows = append(rows, [2]string{encode(key), encode(values[key])})
				}
				return rows
			}
			units, links, directory, options, err := testrunner.ParseTestFilesAndSymlinks(text, request.Name, func(name string, content string, options map[string]string) ([]any, error) {
				return []any{encode(name), encode(content), pairs(options)}, nil
			})
			if err != nil {
				panic(err)
			}
			payload, _ := json.Marshal([]any{units, pairs(links), encode(directory), pairs(options)})
			hash := sha256.Sum256(payload)
			result := map[string]any{"name": request.Name, "tokens": len(units), "diagnostics": 0, "hash": hex.EncodeToString(hash[:])}
			if request.Details {
				result["details"] = json.RawMessage(payload)
			}
			if err := output.Encode(result); err != nil {
				panic(err)
			}
			continue
		}
		position := func(offset int) int { return offset }
		s := scanner.NewScanner()
		s.SetText(text)
		s.SetSkipTrivia(!request.Trivia)
		if request.Jsx {
			s.SetLanguageVariant(core.LanguageVariantJSX)
		}
		var errors [][3]int
		jsErrors := make([][3]int, 0)
		var javascriptDiagnostics []*ast.Diagnostic
		s.SetOnError(func(message *diagnostics.Message, start, length int, _ ...any) {
			errors = append(errors, [3]int{int(message.Code()), position(start), position(start+length) - position(start)})
		})
		tokens := make([][]any, 0)
		if request.Mode == "parse" {
			kind := core.ScriptKindTS
			if request.Jsx {
				kind = core.ScriptKindTSX
			}
			fileName := "/" + request.Name
			if request.FileName != "" {
				fileName = tspath.GetNormalizedAbsolutePath(request.FileName, "/fixtures")
				kind = core.EnsureScriptKindFromFileName(fileName)
			}
			file := parser.ParseSourceFile(ast.SourceFileParseOptions{FileName: fileName}, text, kind)
			stack := []*ast.Node{file.AsNode()}
			for len(stack) > 0 {
				node := stack[len(stack)-1]
				stack = stack[:len(stack)-1]
				children := []*ast.Node{}
				node.ForEachChild(func(child *ast.Node) bool { children = append(children, child); return false })
				value := ""
				switch node.Kind {
				case ast.KindIdentifier, ast.KindPrivateIdentifier, ast.KindStringLiteral, ast.KindNumericLiteral, ast.KindBigIntLiteral, ast.KindRegularExpressionLiteral, ast.KindNoSubstitutionTemplateLiteral, ast.KindTemplateHead, ast.KindTemplateMiddle, ast.KindTemplateTail:
					value = node.Text()
				case ast.KindJsxText:
					value = node.AsJsxText().Text
				}
				tokens = append(tokens, []any{int(node.Kind), position(node.Pos()), position(node.End()), uint32(node.Flags), base64.StdEncoding.EncodeToString([]byte(value)), len(children), ast.CSharpScalarProperties(node), ast.CSharpListProperties(node, position)})
				for i := len(children) - 1; i >= 0; i-- {
					stack = append(stack, children[i])
				}
			}
			for _, d := range file.Diagnostics() {
				errors = append(errors, [3]int{int(d.Code()), position(d.Pos()), position(d.End()) - position(d.Pos())})
			}
			javascriptDiagnostics = file.JSDiagnostics()
			for _, d := range javascriptDiagnostics {
				jsErrors = append(jsErrors, [3]int{int(d.Code()), position(d.Pos()), position(d.End()) - position(d.Pos())})
			}
		} else {
			for {
				var kind ast.Kind
				switch request.Mode {
				case "jsdoc":
					kind = s.ScanJSDocToken()
				case "jsx":
					kind = s.ScanJsxToken()
				default:
					kind = s.Scan()
				}
				if request.Mode == "greater" {
					kind = s.ReScanGreaterThanToken()
				}
				if request.Mode == "regex" && (kind == ast.KindSlashToken || kind == ast.KindSlashEqualsToken) {
					kind = s.ReScanSlashToken()
				}
				value := ""
				if kind == ast.KindIdentifier || kind == ast.KindPrivateIdentifier || kind >= ast.KindFirstKeyword && kind <= ast.KindLastKeyword || kind >= ast.KindNumericLiteral && kind <= ast.KindTemplateTail {
					value = base64.StdEncoding.EncodeToString([]byte(s.TokenValue()))
				}
				tokens = append(tokens, []any{int(kind), position(s.TokenFullStart()), position(s.TokenStart()), position(s.TokenEnd()), int(s.TokenFlags()), value})
				if kind == ast.KindEndOfFile {
					break
				}
			}
		}
		if errors == nil {
			errors = make([][3]int, 0)
		}
		sort.Slice(jsErrors, func(i, j int) bool {
			for k := range 3 {
				if jsErrors[i][k] != jsErrors[j][k] {
					return jsErrors[i][k] < jsErrors[j][k]
				}
			}
			return false
		})
		payload, _ := json.Marshal([]any{tokens, errors, jsErrors})
		hash := sha256.Sum256(payload)
		result := map[string]any{"name": request.Name, "tokens": len(tokens), "diagnostics": len(errors), "hash": hex.EncodeToString(hash[:])}
		result["jsDiagnostics"] = len(jsErrors)
		if request.Details {
			result["details"] = json.RawMessage(payload)
		}
		if request.JSDetails && request.Mode == "parse" {
			details := make([][]any, 0, len(javascriptDiagnostics))
			var describe func(*ast.Diagnostic) []any
			describe = func(d *ast.Diagnostic) []any {
				arguments := append([]string{}, d.MessageArgs()...)
				related := make([][]any, 0, len(d.RelatedInformation()))
				for _, r := range d.RelatedInformation() {
					related = append(related, describe(r))
				}
				return []any{int(d.Code()), position(d.Pos()), position(d.End()) - position(d.Pos()), arguments, related}
			}
			for _, d := range javascriptDiagnostics {
				details = append(details, describe(d))
			}
			sort.Slice(details, func(i, j int) bool {
				for k := range 3 {
					if details[i][k] != details[j][k] {
						return details[i][k].(int) < details[j][k].(int)
					}
				}
				return false
			})
			result["jsDetails"] = details
		}
		if err := output.Encode(result); err != nil {
			panic(err)
		}
	}
	if err := lines.Err(); err != nil {
		panic(err)
	}
}
