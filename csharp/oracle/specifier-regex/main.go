// Development differential oracle only; never invoked by the backend.
package main

import (
	"bufio"
	"compress/bzip2"
	"encoding/base64"
	"encoding/json"
	"github.com/microsoft/TypeScript/tsc/internal/modulespecifiers"
	"go/ast"
	"go/parser"
	"go/token"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"regexp/syntax"
	"runtime"
	"sort"
	"strconv"
	"strings"
	"unicode"
)

func decode(s string) string {
	b, err := base64.StdEncoding.DecodeString(s)
	if err != nil {
		panic(err)
	}
	return string(b)
}
func main() {
	if len(os.Args) > 1 && os.Args[1] == "--fixtures" {
		fixtures()
		return
	}
	scanner := bufio.NewScanner(os.Stdin)
	scanner.Buffer(make([]byte, 4096), 64*1024*1024)
	writer := json.NewEncoder(os.Stdout)
	for scanner.Scan() {
		var input struct {
			Pattern string   `json:"pattern"`
			Texts   []string `json:"texts"`
		}
		if err := json.Unmarshal(scanner.Bytes(), &input); err != nil {
			panic(err)
		}
		pattern := decode(input.Pattern)
		compiled, err := regexp.Compile(pattern)
		matches, excludes := []bool{}, []bool{}
		for _, text := range input.Texts {
			value := decode(text)
			matches = append(matches, err == nil && compiled.MatchString(value))
			excludes = append(excludes, modulespecifiers.IsExcludedByRegex(value, []string{pattern}))
		}
		if err := writer.Encode(map[string]any{"valid": err == nil, "matches": matches, "excludes": excludes}); err != nil {
			panic(err)
		}
	}
	if scanner.Err() != nil {
		panic(scanner.Err())
	}
}

func fixtures() {
	out := json.NewEncoder(os.Stdout)
	emit := func(pattern string, texts []string) {
		encoded := []string{}
		for _, text := range texts {
			encoded = append(encoded, base64.StdEncoding.EncodeToString([]byte(text)))
		}
		out.Encode(map[string]any{"pattern": base64.StdEncoding.EncodeToString([]byte(pattern)), "texts": encoded})
	}
	subjects := []string{"", "a", "ab", "abab", "aaaaab", "ABC", "abc\n", "a\nb", "0", "99", "\x00", "\xff", "α", "日", "ſ", "K", "𐐀", "😀"}
	names := []string{"Any", "Assigned", "ASCII"}
	for name := range unicode.Categories {
		names = append(names, name)
	}
	for name := range unicode.CategoryAliases {
		names = append(names, name)
	}
	for name := range unicode.Scripts {
		names = append(names, name)
	}
	sort.Strings(names)
	for _, name := range names {
		for _, flags := range []string{"", "(?i)"} {
			pattern := flags + `\p{` + name + `}`
			expr, err := syntax.Parse(pattern, syntax.Perl)
			if err != nil {
				panic(err)
			}
			points := map[rune]bool{0: true, unicode.MaxRune: true, 0xfffd: true, 0x10400: true}
			for _, r := range expr.Rune {
				for d := rune(-1); d <= 1; d++ {
					p := r + d
					if p >= 0 && p <= unicode.MaxRune && !(p >= 0xd800 && p <= 0xdfff) {
						points[p] = true
					}
				}
			}
			sorted := []int{}
			for p := range points {
				sorted = append(sorted, int(p))
			}
			sort.Ints(sorted)
			texts := []string{}
			for _, p := range sorted {
				texts = append(texts, string(rune(p)))
			}
			emit(pattern, texts)
			emit(flags+`\P{`+name+`}`, texts)
		}
	}
	var eval func(ast.Expr) (string, bool)
	eval = func(expr ast.Expr) (string, bool) {
		switch v := expr.(type) {
		case *ast.BasicLit:
			if v.Kind == token.STRING {
				s, err := strconv.Unquote(v.Value)
				return s, err == nil
			}
		case *ast.BinaryExpr:
			if v.Op == token.ADD {
				a, ok := eval(v.X)
				b, ok2 := eval(v.Y)
				return a + b, ok && ok2
			}
		case *ast.CallExpr:
			if len(v.Args) == 2 {
				if callee, ok := v.Fun.(*ast.SelectorExpr); ok && callee.Sel.Name == "Repeat" {
					s, ok := eval(v.Args[0])
					if n, ok2 := v.Args[1].(*ast.BasicLit); ok && ok2 {
						count, err := strconv.Atoi(n.Value)
						if err == nil {
							return strings.Repeat(s, count), true
						}
					}
				}
			}
		}
		return "", false
	}
	source, err := parser.ParseFile(token.NewFileSet(), filepath.Join(runtime.GOROOT(), "src/regexp/syntax/parse_test.go"), nil, 0)
	if err != nil {
		panic(err)
	}
	for _, decl := range source.Decls {
		if g, ok := decl.(*ast.GenDecl); ok {
			for _, spec := range g.Specs {
				if v, ok := spec.(*ast.ValueSpec); ok && len(v.Names) == 1 && len(v.Values) == 1 {
					name := v.Names[0].Name
					if name != "parseTests" && name != "invalidRegexps" && name != "onlyPerl" && name != "onlyPOSIX" {
						continue
					}
					if list, ok := v.Values[0].(*ast.CompositeLit); ok {
						for _, expr := range list.Elts {
							if row, ok := expr.(*ast.CompositeLit); ok {
								expr = row.Elts[0]
							}
							if pattern, ok := eval(expr); ok {
								emit(pattern, subjects)
							}
						}
					}
				}
			}
		}
	}
	files := []string{"re2-search.txt"}
	if len(os.Args) > 2 && os.Args[2] == "--exhaustive" {
		files = append(files, "re2-exhaustive.txt.bz2")
	}
	for _, name := range files {
		f, err := os.Open(filepath.Join(runtime.GOROOT(), "src/regexp/testdata", name))
		if err != nil {
			panic(err)
		}
		var reader io.Reader = f
		if strings.HasSuffix(name, ".bz2") {
			reader = bzip2.NewReader(f)
		}
		scanner := bufio.NewScanner(reader)
		scanner.Buffer(make([]byte, 4096), 64*1024*1024)
		texts := []string{}
		inStrings := false
		for scanner.Scan() {
			line := scanner.Text()
			switch {
			case line == "strings":
				texts = []string{}
				inStrings = true
			case line == "regexps":
				inStrings = false
			case strings.HasPrefix(line, "\""):
				value, err := strconv.Unquote(line)
				if err != nil {
					panic(err)
				}
				if inStrings {
					texts = append(texts, value)
				} else {
					emit(value, texts)
				}
			}
		}
		if scanner.Err() != nil {
			panic(scanner.Err())
		}
		f.Close()
	}
}
