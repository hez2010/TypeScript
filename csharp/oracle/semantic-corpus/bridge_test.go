package testrunner

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"sync"
	"testing"

	"github.com/microsoft/TypeScript/tsc/internal/ast"
	"github.com/microsoft/TypeScript/tsc/internal/bundled"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/testutil"
	"github.com/microsoft/TypeScript/tsc/internal/testutil/harnessutil"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions"
	"github.com/microsoft/TypeScript/tsc/internal/tspath"
)

// This bridge is compiled only into the frozen reference's test runner. It uses
// the runner's actual configuration expansion, roots, libraries and skip rules.
func TestCSharpSemanticCorpus(t *testing.T) {
	outDir := os.Getenv("CSHARP_CORPUS_OUTPUT")
	if outDir == "" {
		t.Fatal("CSHARP_CORPUS_OUTPUT is required")
	}
	if err := os.MkdirAll(filepath.Join(outDir, "blobs"), 0o755); err != nil {
		t.Fatal(err)
	}
	output, err := os.Create(filepath.Join(outDir, "cases.jsonl"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		if err := output.Close(); err != nil {
			t.Error(err)
		}
	})
	encoder := json.NewEncoder(output)
	var mu sync.Mutex
	writtenBlobs := map[string]bool{}
	write := func(row map[string]any) {
		mu.Lock()
		defer mu.Unlock()
		if err := encoder.Encode(row); err != nil {
			t.Error(err)
		}
	}
	blob := func(text string) string {
		hash := sha256.Sum256([]byte(text))
		key := hex.EncodeToString(hash[:])
		mu.Lock()
		defer mu.Unlock()
		if !writtenBlobs[key] {
			if err := os.WriteFile(filepath.Join(outDir, "blobs", key), []byte(text), 0o644); err != nil {
				panic(err)
			}
			writtenBlobs[key] = true
		}
		return key
	}
	filter := os.Getenv("CSHARP_CORPUS_FILTER")
	runners := []*CompilerBaselineRunner{NewCompilerBaselineRunner(TestTypeRegression), NewCompilerBaselineRunner(TestTypeConformance)}
	var inventory []string
	var planned []map[string]string
	save := func(name string, value any) {
		data, err := json.Marshal(value)
		if err != nil {
			t.Error(err)
			return
		}
		if err := os.WriteFile(filepath.Join(outDir, name), data, 0o644); err != nil {
			t.Error(err)
		}
	}
	for _, runner := range runners {
		for _, filename := range runner.EnumerateTestFiles() {
			if filter == "" || strings.Contains(filename, filter) {
				inventory = append(inventory, filename)
			}
		}
	}
	save("inventory.json", inventory)
	defer func() { save("plan.json", planned) }()
	for _, runner := range runners {
		for _, filename := range runner.EnumerateTestFiles() {
			if filter != "" && !strings.Contains(filename, filter) {
				continue
			}
			if slices.Contains(skippedTests, tspath.GetBaseFileName(filename)) {
				planned = append(planned, map[string]string{"name": filename, "source": filename})
				write(map[string]any{"name": filename, "source": filename, "status": "reference-skipped", "reason": "compiler runner skippedTests"})
				continue
			}
			test := getCompilerFileBasedTest(t, filename)
			configs := test.configurations
			if len(configs) == 0 {
				configs = []*harnessutil.NamedTestConfiguration{nil}
			}
			for _, config := range configs {
				name := runner.testSuitName + "/" + tspath.GetBaseFileName(filename)
				if config != nil && config.Name != "" {
					name += " " + config.Name
				}
				planned = append(planned, map[string]string{"name": name, "source": filename})
				t.Run(name, func(t *testing.T) {
					t.Parallel()
					row := map[string]any{"name": name, "source": filename, "status": "reference-failed"}
					defer func() {
						if failure := recover(); failure != nil {
							row["error"] = fmt.Sprint(failure)
							t.Errorf("Reference failure: %v", failure)
						}
						if t.Skipped() {
							row["status"] = "reference-skipped"
						}
						if t.Failed() {
							row["status"] = "reference-failed"
						}
						write(row)
					}()
					payload := makeUnitsFromTest(test.content, filename)
					c := newCompilerTest(t, name, filename, &payload, config)
					harnessutil.SkipUnsupportedCompilerOptions(t, c.options)
					program := c.result.Program.Program()
					// Emit-resolver queries populate caches and can change diagnostic
					// attribution. The development-only harness patch captures diagnostics
					// at the original pre-emit call, before any declaration or JS emission.
					row["postEmitSemanticDiagnostics"] = csharpCorpusDiagnostics(program.GetSemanticDiagnostics(context.Background(), nil))
					row["postEmitGlobalDiagnostics"] = csharpCorpusDiagnostics(program.GetGlobalDiagnostics(context.Background()))
					files := map[string]string{}
					// Match the harness filesystem's input-then-other-file overwrite order.
					for _, group := range [][]*harnessutil.TestFile{c.toBeCompiled, c.otherFiles} {
						for _, unit := range group {
							content := unit.Content
							if file := program.GetSourceFile(unit.UnitName); file != nil && file.ContentMapper() != "" {
								content = file.OriginalText()
							}
							files[tspath.GetNormalizedAbsolutePath(unit.UnitName, c.currentDirectory)] = blob(content)
						}
					}
					if unit := payload.tsConfigFileUnitData; unit != nil {
						files[tspath.GetNormalizedAbsolutePath(unit.name, c.currentDirectory)] = blob(unit.content)
					}
					// The runner supplies additional @libFiles only from /.lib.
					var readLib func(string)
					readLib = func(directory string) {
						entries := c.result.Host.FS().GetAccessibleEntries(directory)
						for _, name := range entries.Files {
							path := tspath.CombinePaths(directory, name)
							if text, ok := c.result.Host.FS().ReadFile(path); ok {
								files[path] = blob(text)
							}
						}
						for _, name := range entries.Directories {
							readLib(tspath.CombinePaths(directory, name))
						}
					}
					if c.result.Host.FS().DirectoryExists("/.lib") {
						readLib("/.lib")
					}
					links := map[string]string{}
					for source, target := range payload.symlinks {
						links[tspath.GetNormalizedAbsolutePath(source, c.currentDirectory)] = tspath.GetNormalizedAbsolutePath(target, c.currentDirectory)
					}
					var sources []map[string]any
					for _, file := range program.GetSourceFiles() {
						hash := sha256.Sum256([]byte(file.Text()))
						sources = append(sources, map[string]any{"file": file.FileName(), "sha256": hex.EncodeToString(hash[:]), "library": program.IsSourceFileDefaultLibrary(file.Path())})
					}
					semantic := c.result.CSharpSemanticDiagnostics
					global := c.result.CSharpGlobalDiagnostics
					row["files"], row["symlinks"], row["sources"] = files, links, sources
					row["currentDirectory"], row["caseSensitive"], row["libraryDirectory"] = c.currentDirectory, c.harnessOptions.UseCaseSensitiveFileNames, bundled.LibPath()
					row["roots"], row["options"] = program.CommandLine().FileNames(), csharpCorpusOptions(c.options)
					row["singleThreaded"] = testutil.TestProgramIsSingleThreaded()
					row["configFileName"] = program.CommandLine().ConfigName()
					row["contentMappers"] = len(program.CommandLine().ContentMappers())
					row["semanticDiagnostics"], row["globalDiagnostics"] = csharpCorpusDiagnostics(semantic), csharpCorpusDiagnostics(global)
					row["status"] = "ready"
				})
			}
		}
	}
}

func csharpCorpusOptions(options *core.CompilerOptions) map[string]json.RawMessage {
	data, err := json.Marshal(options)
	if err != nil {
		panic(err)
	}
	result := map[string]json.RawMessage{}
	if err := json.Unmarshal(data, &result); err != nil {
		panic(err)
	}
	// Both frontends accept option names. Restore names for enum values so the
	// replay uses the same public compiler-option representation as parsed configs.
	for _, option := range tsoptions.OptionsDeclarations {
		value, ok := result[option.Name]
		if !ok || option.Kind != tsoptions.CommandLineOptionTypeEnum {
			continue
		}
		for name := range option.EnumMap().Keys() {
			identity, _ := option.EnumMap().Get(name)
			encoded, _ := json.Marshal(identity)
			if string(encoded) == string(value) {
				result[option.Name], _ = json.Marshal(name)
				break
			}
		}
	}
	return result
}

func csharpCorpusDiagnostics(diagnostics []*ast.Diagnostic) []map[string]any {
	result := make([]map[string]any, 0, len(diagnostics))
	for _, diagnostic := range diagnostics {
		file := ""
		if diagnostic.File() != nil {
			file = diagnostic.File().FileName()
		}
		result = append(result, map[string]any{
			"file": file, "start": diagnostic.Pos(), "length": diagnostic.End() - diagnostic.Pos(), "code": diagnostic.Code(), "category": diagnostic.Category(),
			"key": diagnostic.MessageKey(), "arguments": append([]string{}, diagnostic.MessageArgs()...),
			"chain": csharpCorpusDiagnostics(diagnostic.MessageChain()), "related": csharpCorpusDiagnostics(diagnostic.RelatedInformation()),
		})
	}
	return result
}
