package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"runtime/debug"
	"slices"
	"strings"
	"sync"
	"time"

	"github.com/microsoft/TypeScript/tsc/internal/bundled"
	"github.com/microsoft/TypeScript/tsc/internal/collections"
	"github.com/microsoft/TypeScript/tsc/internal/compiler"
	"github.com/microsoft/TypeScript/tsc/internal/diagnostics"
	"github.com/microsoft/TypeScript/tsc/internal/execute"
	"github.com/microsoft/TypeScript/tsc/internal/execute/incremental"
	"github.com/microsoft/TypeScript/tsc/internal/locale"
	"github.com/microsoft/TypeScript/tsc/internal/testutil/contentmappertest"
	"github.com/microsoft/TypeScript/tsc/internal/tspath"
	"github.com/microsoft/TypeScript/tsc/internal/vfs"
	"github.com/microsoft/TypeScript/tsc/internal/vfs/vfstest"
)

type clock struct { mutex sync.Mutex; now time.Time }
func(c *clock) Now() time.Time { c.mutex.Lock(); defer c.mutex.Unlock(); c.now = c.now.Add(time.Second); return c.now }
func(c *clock) SinceStart() time.Duration { return c.Now().Sub(time.Date(2000,1,1,0,0,0,0,time.UTC)) }
type write struct { Path string `json:"path"`; TextBase64 string `json:"textBase64,omitempty"`; BuildInfo json.RawMessage `json:"buildInfo,omitempty"` }
type trackingFS struct { vfs.FS; mutex sync.Mutex; writes []write; removed, touched []string; failWrite, failRemove string }
func(f *trackingFS) WriteFile(path, text string) error {
	if path == f.failWrite { return fmt.Errorf("test write failure") }
	if err := f.FS.WriteFile(path, text); err != nil { return err }
	f.mutex.Lock(); defer f.mutex.Unlock()
	w := write{Path: path}; if strings.HasSuffix(path, ".tsbuildinfo") { w.BuildInfo = json.RawMessage(text) } else { w.TextBase64 = base64.StdEncoding.EncodeToString([]byte(text)) }
	f.writes = append(f.writes, w); return nil
}
func(f *trackingFS) Remove(path string) error {
	if path == f.failRemove { return fmt.Errorf("test remove failure") }
	if err := f.FS.Remove(path); err != nil { return err }
	f.mutex.Lock(); defer f.mutex.Unlock(); f.removed = append(f.removed, path); return nil
}
func(f *trackingFS) Chtimes(path string, access, write time.Time) error {
	if err := f.FS.Chtimes(path, access, write); err != nil { return err }
	f.mutex.Lock(); defer f.mutex.Unlock(); f.touched = append(f.touched, path); return nil
}
type system struct { fs *trackingFS; clock *clock; cwd, library string; stdout, stderr bytes.Buffer }
func(s *system) Writer() io.Writer { return &s.stdout }
func(s *system) ErrorWriter() io.Writer { return &s.stderr }
func(s *system) FS() vfs.FS { return s.fs }
func(s *system) DefaultLibraryPath() string { return s.library }
func(s *system) GetCurrentDirectory() string { return s.cwd }
func(s *system) WriteOutputIsTTY() bool { return false }
func(s *system) GetWidthOfTerminal() int { return 80 }
func(s *system) GetEnvironmentVariable(string) (string,bool) { return "",false }
func(s *system) Spawn(args []string, dir string, stderr io.Writer) (io.ReadWriteCloser,error) { return contentmappertest.NewSpawner().Spawn(args,dir,stderr) }
func(s *system) Now() time.Time { return s.clock.Now() }
func(s *system) SinceStart() time.Duration { return s.clock.Now().Sub(time.Date(2000,1,1,0,0,0,0,time.UTC)) }
// The repository's command tests timestamp outputs in returned-file order so
// worker scheduling cannot choose different oldest-output witnesses.
func(s *system) OnEmittedFiles(result *compiler.EmitResult, cache *collections.SyncMap[tspath.Path,time.Time]) {
	if result==nil{return}
	for _,file:=range result.EmittedFiles {
		stamp:=s.Now();if err:=s.fs.FS.Chtimes(file,time.Time{},stamp);err!=nil{panic(err)}
		if cache!=nil{path:=tspath.ToPath(file,s.cwd,s.fs.UseCaseSensitiveFileNames());if _,ok:=cache.Load(path);ok{cache.Store(path,stamp)}}
	}
}
func(s *system) OnListFilesStart(io.Writer){}
func(s *system) OnListFilesEnd(io.Writer){}
func(s *system) OnStatisticsStart(io.Writer){}
func(s *system) OnStatisticsEnd(io.Writer){}
func(s *system) OnBuildStatusReportStart(io.Writer){}
func(s *system) OnBuildStatusReportEnd(io.Writer){}
func(s *system) OnWatchStatusReportStart(){}
func(s *system) OnWatchStatusReportEnd(){}
func(s *system) GetTrace(writer io.Writer, loc locale.Locale)func(*diagnostics.Message,...any){return func(message *diagnostics.Message,args ...any){fmt.Fprintln(writer,message.Localize(loc,args...))}}
func(s *system) OnProgram(*incremental.Program){}
type step struct {
	Edits map[string]*string `json:"edits"`
	Args []string `json:"args"`
	Touch []string `json:"touch"`
	Times map[string]int64 `json:"times"`
	FailWrite string `json:"failWrite"`
	FailRemove string `json:"failRemove"`
}
type request struct { Files map[string]string `json:"files"`; Cwd string `json:"cwd"`; Library string `json:"libraryDirectory"`; CaseInsensitive bool `json:"caseInsensitive"`; Steps []step `json:"steps"` }
func executeRequest(input request) (result any) {
	defer func(){if error := recover(); error != nil { result = map[string]any{"error":fmt.Sprint(error)} }}()
	if input.Cwd == "" { input.Cwd = "/source" }
	clock := &clock{now:time.Date(2000,1,1,0,0,0,0,time.UTC)}
	fs := &trackingFS{FS:bundled.WrapFS(vfstest.FromMapWithClock(input.Files,!input.CaseInsensitive,clock))}
	if input.Library=="" {input.Library=bundled.LibPath()}
	sys := &system{fs:fs,clock:clock,cwd:input.Cwd,library:input.Library}
	cycles:=[]any{}
	for _,step := range input.Steps {
		keys:=[]string{};for key:=range step.Edits { keys=append(keys,key) };slices.Sort(keys)
		for _,key:=range keys { if step.Edits[key]==nil { _=fs.FS.Remove(key) } else if err:=fs.FS.WriteFile(key,*step.Edits[key]);err!=nil {panic(err)} }
		for _,key:=range step.Touch { value:=clock.Now();if err:=fs.FS.Chtimes(key,value,value);err!=nil {panic(err)} }
		for key,milliseconds:=range step.Times { value:=time.UnixMilli(milliseconds);if err:=fs.FS.Chtimes(key,value,value);err!=nil {panic(err)} }
		fs.writes=[]write{};fs.removed=[]string{};fs.touched=[]string{};fs.failWrite=step.FailWrite;fs.failRemove=step.FailRemove
		sys.stdout.Reset();sys.stderr.Reset()
		args:=step.Args;if args==nil {args=[]string{"--build","--verbose","--pretty","false","--singleThreaded"}}
		status := 0; failure := ""; stack := ""
		func() {
			defer func(){if err:=recover();err!=nil {failure=fmt.Sprint(err);stack=string(debug.Stack())}}()
			out:=execute.CommandLine(context.Background(),sys,args,sys);status=int(out.Status)
		}()
		slices.SortFunc(fs.writes,func(a,b write)int{return strings.Compare(a.Path,b.Path)})
		slices.Sort(fs.removed);slices.Sort(fs.touched)
		row:=map[string]any{"status":status,"stdout":sys.stdout.String(),"stderr":sys.stderr.String(),"writes":fs.writes,"removed":fs.removed,"touched":fs.touched}
		if failure!=""{row["error"]=failure;row["stack"]=stack}
		cycles=append(cycles,row)
	}
	return cycles
}
func main(){
	scanner:=bufio.NewScanner(os.Stdin);scanner.Buffer(make([]byte,65536),64*1024*1024)
	writer:=bufio.NewWriter(os.Stdout);defer writer.Flush()
	for scanner.Scan(){var input request;if err:=json.Unmarshal(scanner.Bytes(),&input);err!=nil{panic(err)};out,err:=json.Marshal(executeRequest(input));if err!=nil{panic(err)};fmt.Fprintln(writer,string(out))}
	if err:=scanner.Err();err!=nil{panic(err)}
}
