// This development-only probe is compiled inside the archived, pinned Go
// module. It is never called by the C# candidate or shipped with the product.
package main

import (
	"context"
	"encoding/base64"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"math"
	"os"
	"slices"
	"sort"
	"unicode/utf16"

	"github.com/microsoft/TypeScript/tsc/internal/api/encoder"
	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/binder"
	"github.com/microsoft/TypeScript/tsc/internal/bundled"
	"github.com/microsoft/TypeScript/tsc/internal/checker"
	"github.com/microsoft/TypeScript/tsc/internal/compiler"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/jsnum"
	"github.com/microsoft/TypeScript/tsc/internal/parser"
	"github.com/microsoft/TypeScript/tsc/internal/stringutil"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions"
	"github.com/microsoft/TypeScript/tsc/internal/vfs/vfstest"
	"github.com/zeebo/xxh3"
)

type request struct {
	Cases   []projectInput `json:"cases"`
	Types   []string       `json:"types"`
	Texts   []string       `json:"texts"`
	Numbers [][2]string    `json:"numbers"`
	Files   []source       `json:"files"`
}
type source struct {
	Name string `json:"name"`
	Text string `json:"text"`
}
type textResult struct {
	Units    []uint16 `json:"units"`
	Combined string   `json:"combined"`
	ToUTF16  []int    `json:"toUtf16"`
	ToUTF8   []int    `json:"toUtf8"`
}
type diagnostic struct {
	Code   int32 `json:"code"`
	Pos    int   `json:"pos"`
	Length int   `json:"length"`
}
type fileResult struct {
	Name        string       `json:"name"`
	Wire        string       `json:"wire"`
	Locals      []string     `json:"locals"`
	Diagnostics []diagnostic `json:"diagnostics"`
}
type result struct {
	Cases     []projectOutput `json:"cases"`
	Relations []bool          `json:"relations"`
	Texts     []textResult    `json:"texts"`
	Numbers   [][]string      `json:"numbers"`
	Files     []fileResult    `json:"files"`
}

type projectInput struct {
	Name  string   `json:"name"`
	Files []source `json:"files"`
}
type projectDiagnostic struct {
	File   string `json:"file"`
	Code   int32  `json:"code"`
	Pos    int    `json:"pos"`
	Length int    `json:"length"`
}
type projectOutput struct {
	Name        string              `json:"name"`
	Files       []fileResult        `json:"files"`
	Diagnostics []projectDiagnostic `json:"diagnostics"`
	SymbolNames []string            `json:"symbolNames"`
	Relations   []bool              `json:"relations"`
}

func probeProject(input projectInput) projectOutput {
	output := projectOutput{Name: input.Name, Files: []fileResult{}, Diagnostics: []projectDiagnostic{}, SymbolNames: []string{}, Relations: []bool{}}
	files := map[string]string{}
	names := []string{}
	for _, file := range input.Files {
		files[file.Name] = string(bytes(file.Text))
		names = append(names, file.Name)
	}
	configText, err := json.Marshal(map[string]any{"compilerOptions": map[string]any{"strict": true, "module": "esnext", "moduleResolution": "bundler"}, "files": names})
	fail(err)
	files["/tsconfig.json"] = string(configText)
	fs := bundled.WrapFS(vfstest.FromMap(files, true))
	host := compiler.NewCompilerHost("/", fs, bundled.LibPath(), nil, nil, nil)
	config, errors := tsoptions.GetParsedCommandLineOfConfigFile("/tsconfig.json", &core.CompilerOptions{}, nil, host, nil)
	if len(errors) != 0 {
		panic("invalid project config")
	}
	program := compiler.NewProgram(compiler.ProgramOptions{Config: config, Host: host})
	program.BindSourceFiles()
	for _, name := range names {
		file := program.GetSourceFile(name)
		allDiags := append(slices.Clone(file.Diagnostics()), program.GetSemanticDiagnostics(context.Background(), file)...)
		for _, d := range allDiags {
			output.Diagnostics = append(output.Diagnostics, projectDiagnostic{File: name, Code: d.Code(), Pos: d.Pos(), Length: d.Len()})
		}
	}
	c, done := program.GetTypeChecker(context.Background())
	defer done()
	typesByName := map[string]*checker.Type{}
	for _, name := range names {
		file := program.GetSourceFile(name)
		wire, _, err := encoder.EncodeSourceFile(file)
		fail(err)
		r := fileResult{Name: name, Wire: base64.StdEncoding.EncodeToString(wire), Locals: []string{}, Diagnostics: []diagnostic{}}
		for local := range file.AsNode().Locals() {
			r.Locals = append(r.Locals, local)
		}
		slices.Sort(r.Locals)
		output.Files = append(output.Files, r)
		for _, statement := range file.Statements.Nodes {
			if statement.Kind == ast.KindTypeAliasDeclaration {
				alias := statement.AsTypeAliasDeclaration()
				key := name + ":" + alias.Name().AsIdentifier().Text
				typesByName[key] = c.GetTypeFromTypeNode(alias.Type)
			}
			if statement.Kind == ast.KindVariableStatement {
				for _, decl := range statement.AsVariableStatement().DeclarationList.AsVariableDeclarationList().Declarations.Nodes {
					id := decl.Name()
					key := name + ":" + id.AsIdentifier().Text
					typesByName[key] = c.GetTypeOfSymbolAtLocation(c.GetSymbolAtLocation(id), id)
				}
			}
		}
	}
	for name := range typesByName {
		output.SymbolNames = append(output.SymbolNames, name)
	}
	sort.Strings(output.SymbolNames)
	if len(output.Diagnostics) == 0 {
		for _, source := range output.SymbolNames {
			for _, target := range output.SymbolNames {
				output.Relations = append(output.Relations, c.IsTypeAssignableTo(typesByName[source], typesByName[target]))
			}
		}
	}
	return output
}

func fail(err error) {
	if err != nil {
		panic(err)
	}
}
func bytes(s string) []byte { b, err := base64.StdEncoding.DecodeString(s); fail(err); return b }
func bits(n jsnum.Number) string {
	if n.IsNaN() {
		return "nan"
	}
	return fmt.Sprintf("%016x", math.Float64bits(float64(n)))
}

func number(s string) jsnum.Number {
	b, err := hex.DecodeString(s)
	fail(err)
	return jsnum.Number(math.Float64frombits(binary.BigEndian.Uint64(b)))
}

func main() {
	var input request
	fail(json.NewDecoder(os.Stdin).Decode(&input))
	output := result{Texts: []textResult{}, Numbers: [][]string{}, Files: []fileResult{}}
	for _, fixture := range input.Cases {
		output.Cases = append(output.Cases, probeProject(fixture))
	}
	if len(input.Types) > 0 {
		text := ""
		for i, expression := range input.Types {
			text += fmt.Sprintf("type T%d = %s;\n", i, expression)
		}
		fs := bundled.WrapFS(vfstest.FromMap(map[string]string{"/slice.ts": text, "/tsconfig.json": `{"compilerOptions":{"strict":true},"files":["slice.ts"]}`}, true))
		host := compiler.NewCompilerHost("/", fs, bundled.LibPath(), nil, nil, nil)
		config, errors := tsoptions.GetParsedCommandLineOfConfigFile("/tsconfig.json", &core.CompilerOptions{}, nil, host, nil)
		if len(errors) != 0 {
			panic("invalid slice config")
		}
		program := compiler.NewProgram(compiler.ProgramOptions{Config: config, Host: host})
		program.BindSourceFiles()
		checkerInstance, done := program.GetTypeChecker(context.Background())
		file := program.GetSourceFile("/slice.ts")
		if len(file.Diagnostics()) != 0 {
			panic("invalid type fixture")
		}
		types := make([]*checker.Type, len(input.Types))
		for i, statement := range file.Statements.Nodes {
			types[i] = checkerInstance.GetTypeFromTypeNode(statement.AsTypeAliasDeclaration().Type)
		}
		output.Relations = []bool{}
		for _, source := range types {
			for _, target := range types {
				output.Relations = append(output.Relations, checkerInstance.IsTypeAssignableTo(source, target))
			}
		}
		done()
	}
	for _, encoded := range input.Texts {
		text := string(bytes(encoded))
		pm := ast.ComputePositionMap(text)
		r := textResult{Units: []uint16{}, Combined: base64.StdEncoding.EncodeToString([]byte(stringutil.CombineSurrogatePairs(text))), ToUTF16: []int{}, ToUTF8: []int{}}
		for rest := text; len(rest) > 0; {
			value, size := stringutil.DecodeJSStringRune(rest)
			if value >= 0x10000 {
				high, low := utf16.EncodeRune(value)
				r.Units = append(r.Units, uint16(high), uint16(low))
			} else {
				r.Units = append(r.Units, uint16(value))
			}
			rest = rest[size:]
		}
		for i := -1; i <= len(text)+1; i++ {
			r.ToUTF16 = append(r.ToUTF16, pm.UTF8ToUTF16(i))
		}
		for i := -1; i <= len(r.Units)+1; i++ {
			r.ToUTF8 = append(r.ToUTF8, pm.UTF16ToUTF8(i))
		}
		output.Texts = append(output.Texts, r)
	}
	for _, pair := range input.Numbers {
		x, y := number(pair[0]), number(pair[1])
		output.Numbers = append(output.Numbers, []string{bits(x.LeftShift(y)), bits(x.SignedRightShift(y)), bits(x.UnsignedRightShift(y)), bits(x.BitwiseNOT()), bits(x.BitwiseOR(y)), bits(x.BitwiseAND(y)), bits(x.BitwiseXOR(y)), bits(x.Remainder(y)), bits(x.Exponentiate(y)), bits(x.Floor()), bits(x.Abs())})
	}
	for _, inputFile := range input.Files {
		file := parser.ParseSourceFile(ast.SourceFileParseOptions{FileName: "/" + inputFile.Name}, string(bytes(inputFile.Text)), core.ScriptKindTS)
		file.Hash = xxh3.Hash128(bytes(inputFile.Text))
		binder.BindSourceFile(file)
		wire, _, err := encoder.EncodeSourceFile(file)
		fail(err)
		r := fileResult{Name: inputFile.Name, Wire: base64.StdEncoding.EncodeToString(wire), Locals: []string{}, Diagnostics: []diagnostic{}}
		for name := range file.AsNode().Locals() {
			r.Locals = append(r.Locals, name)
		}
		slices.Sort(r.Locals)
		for _, d := range file.Diagnostics() {
			r.Diagnostics = append(r.Diagnostics, diagnostic{Code: d.Code(), Pos: d.Pos(), Length: d.Len()})
		}
		output.Files = append(output.Files, r)
	}
	fail(json.NewEncoder(os.Stdout).Encode(output))
}
