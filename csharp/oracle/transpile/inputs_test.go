package testrunner

import (
	"encoding/json"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/testutil/harnessutil"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions"
	"github.com/microsoft/TypeScript/tsc/internal/tspath"
	"os"
	"testing"
)

// Extract the reference runner's own units and option matrix without changing its compiler.
func TestCSharpTranspileInputs(t *testing.T) {
	rows := []map[string]any{}
	for _, file := range NewTranspileBaselineRunner().EnumerateTestFiles() {
		bytes, err := os.ReadFile(file)
		if err != nil {
			t.Fatal(err)
		}
		settings := extractCompilerSettings(string(bytes))
		configurations := harnessutil.GetFileBasedTestConfigurations(t, settings, transpileVaryBy)
		if len(configurations) == 0 {
			configurations = []*harnessutil.NamedTestConfiguration{{Config: settings}}
		}
		units := makeUnitsFromTest(string(bytes), tspath.GetBaseFileName(file)).testUnitData
		for _, config := range configurations {
			options := &core.CompilerOptions{}
			harness := &harnessutil.HarnessOptions{}
			harnessutil.SetOptionsFromTestConfig(t, config.Config, options, harness, srcFolder, false)
			encoded, err := json.Marshal(options)
			if err != nil {
				t.Fatal(err)
			}
			values := map[string]json.RawMessage{}
			if err := json.Unmarshal(encoded, &values); err != nil {
				t.Fatal(err)
			}
			for _, option := range tsoptions.OptionsDeclarations {
				value, ok := values[option.Name]
				if !ok || option.Kind != tsoptions.CommandLineOptionTypeEnum {
					continue
				}
				for name := range option.EnumMap().Keys() {
					identity, _ := option.EnumMap().Get(name)
					data, _ := json.Marshal(identity)
					if string(data) == string(value) {
						values[option.Name], _ = json.Marshal(name)
						break
					}
				}
			}
			for _, declaration := range []bool{false, true} {
				if declaration && !options.Declaration.IsTrue() || !declaration && options.EmitDeclarationOnly.IsTrue() {
					continue
				}
				for _, unit := range units {
					rows = append(rows, map[string]any{"name": tspath.GetBaseFileName(file) + "/" + config.Name + "/" + unit.name + map[bool]string{false: "/js", true: "/dts"}[declaration],
						"source": file, "text": unit.content, "file": unit.name, "options": values, "report": harness.ReportDiagnostics, "declaration": declaration})
				}
			}
		}
	}
	bytes, err := json.Marshal(rows)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(os.Getenv("CSHARP_TRANSPILE_INPUTS"), bytes, 0644); err != nil {
		t.Fatal(err)
	}
}
