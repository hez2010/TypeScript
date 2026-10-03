package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"io/fs"
	"os"
	"runtime/debug"
	"slices"
	"strings"
	"sync"
	"testing/fstest"
	"time"

	"github.com/microsoft/TypeScript/tsc/internal/bundled"
	"github.com/microsoft/TypeScript/tsc/internal/collections"
	"github.com/microsoft/TypeScript/tsc/internal/compiler"
	"github.com/microsoft/TypeScript/tsc/internal/core"
	"github.com/microsoft/TypeScript/tsc/internal/diagnostics"
	"github.com/microsoft/TypeScript/tsc/internal/execute"
	"github.com/microsoft/TypeScript/tsc/internal/execute/tsc"
	"github.com/microsoft/TypeScript/tsc/internal/execute/tsctests"
	"github.com/microsoft/TypeScript/tsc/internal/execute/watchmanager"
	"github.com/microsoft/TypeScript/tsc/internal/execute/incremental"
	"github.com/microsoft/TypeScript/tsc/internal/locale"
	"github.com/microsoft/TypeScript/tsc/internal/testutil/contentmappertest"
	"github.com/microsoft/TypeScript/tsc/internal/testutil/fsbaselineutil"
	"github.com/microsoft/TypeScript/tsc/internal/tspath"
	"github.com/microsoft/TypeScript/tsc/internal/vfs"
	"github.com/microsoft/TypeScript/tsc/internal/vfs/iovfs"
	"github.com/microsoft/TypeScript/tsc/internal/vfs/vfstest"
)

type clock struct { mutex sync.Mutex; now time.Time }
func(c *clock) Now() time.Time { c.mutex.Lock(); defer c.mutex.Unlock(); c.now = c.now.Add(time.Second); return c.now }
func(c *clock) SinceStart() time.Duration { return c.Now().Sub(time.Date(2000,1,1,0,0,0,0,time.UTC)) }
type write struct { Path string `json:"path"`; TextBase64 string `json:"textBase64,omitempty"`; BuildInfo json.RawMessage `json:"buildInfo,omitempty"` }
type trackingFS struct { vfs.FS; mutex sync.Mutex; writes []write; removed, touched []string; failWrite, failRemove string }
func(f *trackingFS) ReadFile(path string)(string,bool) {
	text,ok:=f.FS.ReadFile(path)
	if ok && strings.HasSuffix(path,".tsbuildinfo") {var value map[string]json.RawMessage;if json.Unmarshal([]byte(text),&value)==nil && string(value["version"])==`"FakeTSVersion"` {value["version"],_=json.Marshal(core.Version());bytes,_:=json.Marshal(value);text=string(bytes)}}
	return text,ok
}
func(f *trackingFS) WriteFile(path, text string) error {
	if path == f.failWrite { return fmt.Errorf("test write failure") }
	if err := f.FS.WriteFile(path, text); err != nil { return err }
	f.mutex.Lock(); defer f.mutex.Unlock()
	w := write{Path: path}; if strings.HasSuffix(path, ".tsbuildinfo") { w.BuildInfo = json.RawMessage(text) } else { w.TextBase64 = base64.StdEncoding.EncodeToString([]byte(text)) }
	f.writes = append(f.writes, w); return nil
}
func(f *trackingFS) AppendFile(path, text string) error {
	if path == f.failWrite { return fmt.Errorf("test write failure") }
	f.mutex.Lock(); defer f.mutex.Unlock()
	if err := f.FS.AppendFile(path, text); err != nil { return err }
	complete,ok:=f.FS.ReadFile(path);if !ok{return fmt.Errorf("appended file was not readable: %s",path)}
	w:=write{Path:path,TextBase64:base64.StdEncoding.EncodeToString([]byte(complete))}
	for i:=len(f.writes)-1;i>=0;i--{if f.writes[i].Path==path{f.writes[i]=w;return nil}}
	f.writes=append(f.writes,w);return nil
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
type system struct { fs *trackingFS; clock *clock; cwd, library string; stdout, stderr bytes.Buffer; watches *tsctests.MockWatchBackend; program *incremental.Program; tty bool; env map[string]string }
func(s *system) WatchBackend() watchmanager.WatchBackend{return s.watches}
func(s *system) Writer() io.Writer { return &s.stdout }
func(s *system) ErrorWriter() io.Writer { return &s.stderr }
func(s *system) FS() vfs.FS { return s.fs }
func(s *system) DefaultLibraryPath() string { return s.library }
func(s *system) GetCurrentDirectory() string { return s.cwd }
func(s *system) WriteOutputIsTTY() bool { return s.tty }
func(s *system) GetWidthOfTerminal() int { return 80 }
func(s *system) GetEnvironmentVariable(name string) (string,bool) { value,ok:=s.env[name];return value,ok }
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
func(s *system) OnProgram(program *incremental.Program){s.program=program}
type step struct {
	Edits map[string]*inputFile `json:"edits"`
	Args []string `json:"args"`
	Touch []string `json:"touch"`
	FailWrite string `json:"failWrite"`
	FailRemove string `json:"failRemove"`
	Overflow bool `json:"overflow"`
}
type inputFile struct {Data []byte `json:"dataBase64"`;Mode uint32 `json:"mode"`; Modified time.Time `json:"modified"`}
type request struct { Files map[string]*inputFile `json:"files"`; Cwd string `json:"cwd"`; Library string `json:"libraryDirectory"`; CaseInsensitive bool `json:"caseInsensitive"`; Steps []step `json:"steps"`;TTY bool `json:"outputIsTTY"`;Environment map[string]string `json:"environment"` }
func executeRequest(input request) (result any) {
	defer func(){if error := recover(); error != nil { result = map[string]any{"error":fmt.Sprint(error)} }}()
	if input.Cwd == "" { input.Cwd = "/source" }
	clock := &clock{now:time.Date(2000,1,1,0,0,0,0,time.UTC)}
	initial:=map[string]*fstest.MapFile{}
	for path,file:=range input.Files {
		data:=slices.Clone(file.Data);if fs.FileMode(file.Mode)&fs.ModeSymlink!=0 && !tspath.PathIsAbsolute(string(data)){data=[]byte("/"+string(data))}
		initial[path]=&fstest.MapFile{Data:data,Mode:fs.FileMode(file.Mode)}
	}
	raw:=vfstest.FromMapWithClock(initial,!input.CaseInsensitive,clock)
	mapFS:=raw.(iovfs.FsWithSys).FSys().(*vfstest.MapFS)
	clock.now=time.Date(2000,1,1,0,0,0,0,time.UTC)
	initialKeys:=[]string{};for path,file:=range input.Files {if fs.FileMode(file.Mode).IsRegular(){initialKeys=append(initialKeys,path)}}
	slices.SortFunc(initialKeys,func(a,b string)int{if input.Files[a].Modified.Equal(input.Files[b].Modified){return strings.Compare(a,b)};return input.Files[a].Modified.Compare(input.Files[b].Modified)})
	for _,path:=range initialKeys{stamp:=clock.Now();_=raw.Chtimes(path,stamp,stamp)}
	fs := &trackingFS{FS:bundled.WrapFS(raw)}
	if input.Library=="" {input.Library=bundled.LibPath()}
	sys := &system{fs:fs,clock:clock,cwd:input.Cwd,library:input.Library,tty:input.TTY,env:input.Environment}
	sys.watches=tsctests.NewMockWatchBackend();sys.watches.DirectoryExists=fs.DirectoryExists;sys.watches.UseCaseSensitiveFileNames=!input.CaseInsensitive
	ctx,cancel:=context.WithCancel(context.Background());defer cancel()
	var watcher tsc.Watcher
	cycles:=[]any{}
	for index,step := range input.Steps {
		keys:=[]string{};for key:=range step.Edits { keys=append(keys,key) };slices.Sort(keys)
		for _,key:=range keys {file:=step.Edits[key];if file==nil{_=raw.Remove(key)}else if file.Mode&2147483648!=0{if err:=mapFS.MkdirAll(strings.TrimPrefix(key,"/"),0755);err!=nil{panic(err)}}else if file.Mode&134217728!=0{target:=string(file.Data);if !tspath.PathIsAbsolute(target){target="/"+target};mapFS.AddSymlink(key,target)}else if err:=raw.WriteFile(key,string(file.Data));err!=nil{panic(err)}}
		ordered:=slices.Clone(keys);slices.SortFunc(ordered,func(a,b string)int{af,bf:=step.Edits[a],step.Edits[b];if af==nil||bf==nil{return strings.Compare(a,b)};if af.Modified.Equal(bf.Modified){return strings.Compare(a,b)};return af.Modified.Compare(bf.Modified)})
		for _,key:=range ordered{if file:=step.Edits[key];file!=nil && file.Mode&2281701376==0{stamp:=clock.Now();_=raw.Chtimes(key,stamp,stamp)}}
		for _,key:=range step.Touch { value:=clock.Now();if err:=fs.FS.Chtimes(key,value,value);err!=nil {panic(err)} }
		fs.writes=[]write{};fs.removed=[]string{};fs.touched=[]string{};fs.failWrite=step.FailWrite;fs.failRemove=step.FailRemove
		sys.stdout.Reset();sys.stderr.Reset()
		args:=step.Args
		status := 0; failure := ""; stack := ""
		func() {
			defer func(){if err:=recover();err!=nil {failure=fmt.Sprint(err);stack=string(debug.Stack())}}()
			if index==0 || watcher==nil {
				out:=execute.CommandLine(ctx,sys,args,sys);status=int(out.Status);watcher=out.Watcher
			} else if watcher!=nil {
				changes:=[]fsbaselineutil.FileChange{};for _,key:=range keys{changes=append(changes,fsbaselineutil.FileChange{Path:key,Deleted:step.Edits[key]==nil})};for _,key:=range step.Touch{changes=append(changes,fsbaselineutil.FileChange{Path:key})}
				sys.watches.SendChangedPaths(changes);if step.Overflow{sys.watches.SendOverflow()};watcher.DoCycle()
			}
		}()
		slices.SortFunc(fs.writes,func(a,b write)int{return strings.Compare(a.Path,b.Path)})
		slices.Sort(fs.removed);slices.Sort(fs.touched)
		row:=map[string]any{"status":status,"stdout":sys.stdout.String(),"stderr":sys.stderr.String(),"writes":fs.writes,"removed":fs.removed,"touched":fs.touched}
		dirs:=map[string]bool{};for dir,watch:=range sys.watches.Dirs {if !watch.Closed{dirs[dir]=watch.Recursive}};row["watches"]=dirs
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
