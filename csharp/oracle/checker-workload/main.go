package main

import (
	"bufio"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"syscall"
	"time"
	"unsafe"

	"github.com/microsoft/TypeScript/tsc/internal/bundled"
	"github.com/microsoft/TypeScript/tsc/internal/compiler"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions"
	"github.com/microsoft/TypeScript/tsc/internal/tsoptions/tsoptionstest"
)

type request struct {
	Files, Symlinks                                 map[string]string
	BlobDirectory, CurrentDirectory, ConfigFileName string
	Roots                                           []string
	Options                                         json.RawMessage
	CaseSensitive, SingleThreaded                   bool
}

func main() {
	lines := bufio.NewScanner(os.Stdin)
	lines.Buffer(make([]byte, 4096), 128*1024*1024)
	out := json.NewEncoder(os.Stdout)
	for lines.Scan() {
		var input request
		must(json.Unmarshal(lines.Bytes(), &input))
		files := make(map[string]string, len(input.Files))
		for name, hash := range input.Files {
			data, err := os.ReadFile(filepath.Join(input.BlobDirectory, hash))
			must(err)
			files[name] = string(data)
		}
		configText, err := json.Marshal(map[string]any{"compilerOptions": input.Options, "files": input.Roots})
		must(err)
		configName := input.ConfigFileName
		if configName == "" {
			configName = input.CurrentDirectory + "/tsconfig.json"
		}
		files[configName] = string(configText)
		host := tsoptionstest.NewVFSParseConfigHostWithSymlinks(files, input.Symlinks, input.CurrentDirectory, input.CaseSensitive)
		host.Vfs = bundled.WrapFS(host.Vfs)
		config, errors := tsoptions.GetParsedCommandLineOfConfigFile(configName, nil, nil, host, nil)
		if config == nil || len(errors) != 0 || len(config.Errors) != 0 {
			panic("invalid workload config")
		}
		opts := compiler.ProgramOptions{Config: config, Host: compiler.NewCompilerHost(input.CurrentDirectory, host.FS(), bundled.LibPath(), nil, nil, nil)}
		if input.SingleThreaded {
			opts.SingleThreaded = core.TSTrue
		}
		runtime.GC()
		result := measure(opts)
		runtime.GC()
		var memory runtime.MemStats
		runtime.ReadMemStats(&memory)
		result["releasedBytes"] = memory.HeapAlloc
		result["peakRssBytes"] = peakRss()
		must(out.Encode(result))
		runtime.KeepAlive(host)
	}
	must(lines.Err())
}

func measure(opts compiler.ProgramOptions) map[string]any {
	var before, after runtime.MemStats
	runtime.ReadMemStats(&before)
	cpu := cpuTime()
	start := time.Now()
	program := compiler.NewProgram(opts)
	program.BindSourceFiles()
	programMs := time.Since(start).Seconds() * 1000
	diagnostics := program.GetSemanticDiagnostics(context.Background(), nil)
	globals := program.GetGlobalDiagnostics(context.Background())
	elapsed := time.Since(start).Seconds() * 1000
	cpuMs := cpuTime() - cpu
	runtime.ReadMemStats(&after)
	var graph strings.Builder
	for _, file := range program.GetSourceFiles() {
		hash := sha256.Sum256([]byte(file.Text()))
		fmt.Fprintf(&graph, "%s\x00%x\n", file.FileName(), hash)
	}
	hash := sha256.Sum256([]byte(graph.String()))
	runtime.GC()
	var live runtime.MemStats
	runtime.ReadMemStats(&live)
	result := map[string]any{
		"elapsedMs": elapsed, "cpuMs": cpuMs, "programMs": programMs,
		"allocatedBytes": after.TotalAlloc - before.TotalAlloc, "liveBytes": live.HeapAlloc,
		"sourceFiles": len(program.GetSourceFiles()), "diagnosticCount": len(diagnostics) + len(globals), "graphSha256": hex.EncodeToString(hash[:]),
	}
	runtime.KeepAlive(program)
	return result
}

func must(err error) {
	if err != nil {
		panic(err)
	}
}

var (
	kernel    = syscall.NewLazyDLL("kernel32.dll")
	getTimes  = kernel.NewProc("GetProcessTimes")
	getMemory = syscall.NewLazyDLL("psapi.dll").NewProc("GetProcessMemoryInfo")
)

func cpuTime() float64 {
	var times [4]uint64
	ok, _, err := getTimes.Call(^uintptr(0), uintptr(unsafe.Pointer(&times[0])), uintptr(unsafe.Pointer(&times[1])), uintptr(unsafe.Pointer(&times[2])), uintptr(unsafe.Pointer(&times[3])))
	if ok == 0 {
		panic(err)
	}
	return float64(times[2]+times[3]) / 10000
}

func peakRss() uintptr {
	var memory struct {
		Size, Faults                                                                                                 uint32
		PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool, Pagefile, PeakPagefile uintptr
	}
	memory.Size = uint32(unsafe.Sizeof(memory))
	ok, _, err := getMemory.Call(^uintptr(0), uintptr(unsafe.Pointer(&memory)), uintptr(memory.Size))
	if ok == 0 {
		panic(err)
	}
	return memory.PeakWorkingSet
}
