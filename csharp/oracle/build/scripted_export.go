// Test-only instrumentation compiled into the archived Go oracle's tsctests package.
// The original test runner and its assertions still execute; edits are captured
// from filesystem snapshots immediately before and after each original closure.
package tsctests

import (
    "crypto/sha256"
    "encoding/hex"
    "encoding/json"
    "io/fs"
    "os"
    "path/filepath"
    "slices"
    "testing"
    "time"
)

type csharpFile struct {
    Data []byte `json:"dataBase64"`
    Mode uint32 `json:"mode"`
    Modified time.Time `json:"modified"`
}
type csharpStep struct {
    Caption string `json:"caption"`
    Args []string `json:"args"`
    Edits map[string]*csharpFile `json:"edits"`
    ExpectedDiff string `json:"expectedDiff"`
}
type csharpExport struct {
    Name string `json:"name"`
    Suite string `json:"suite"`
    Scenario string `json:"scenario"`
    Cwd string `json:"cwd"`
    LibraryDirectory string `json:"libraryDirectory"`
    CaseInsensitive bool `json:"caseInsensitive"`
    OutputIsTTY bool `json:"outputIsTTY"`
    Environment map[string]string `json:"environment"`
    Files map[string]*csharpFile `json:"files"`
    Steps []csharpStep `json:"steps"`
    sys *TestSys
    before map[string]*csharpFile
    directory string
}

func csharpSnapshot(sys *TestSys) map[string]*csharpFile {
    result:=map[string]*csharpFile{}
    for path,file:=range sys.mapFs().Entries() {
        if file.Mode.IsRegular() || file.Mode&fs.ModeSymlink!=0 || file.Mode.IsDir() {
            result[path]=&csharpFile{Data:slices.Clone(file.Data),Mode:uint32(file.Mode),Modified:file.ModTime}
        }
    }
    return result
}

func newCSharpExport(t *testing.T,test *tscInput,scenario string,sys *TestSys) *csharpExport {
    directory:=os.Getenv("CSHARP_SCRIPTED_EXPORT")
    if directory=="" {return nil}
    result:=&csharpExport{Name:t.Name(),Suite:test.getBaselineSubFolder(),Scenario:scenario,Cwd:sys.cwd,LibraryDirectory:sys.defaultLibraryPath,
        CaseInsensitive:!sys.FS().UseCaseSensitiveFileNames(),OutputIsTTY:sys.outputIsTTY,Environment:sys.env,
        Files:csharpSnapshot(sys),Steps:[]csharpStep{{Caption:"Initial build",Args:test.commandLineArgs}},sys:sys,directory:directory}
    return result
}

func(e *csharpExport) beforeEdit(){if e!=nil{e.before=csharpSnapshot(e.sys)}}
func(e *csharpExport) afterEdit(edit *tscEdit,args []string){
    if e==nil{return}
    after:=csharpSnapshot(e.sys)
    changes:=map[string]*csharpFile{}
    for path,current:=range after{
        old,ok:=e.before[path]
        if !ok || old.Mode!=current.Mode || !old.Modified.Equal(current.Modified) || !slices.Equal(old.Data,current.Data){changes[path]=current}
    }
    for path:=range e.before{if _,ok:=after[path];!ok{changes[path]=nil}}
    e.Steps=append(e.Steps,csharpStep{Caption:edit.caption,Args:args,Edits:changes,ExpectedDiff:edit.expectedDiff})
    e.before=nil
}
func(e *csharpExport) finish(){
    if e==nil{return}
    bytes,err:=json.Marshal(e);if err!=nil{panic(err)}
    name:=sha256.Sum256([]byte(e.Name))
    if err=os.MkdirAll(e.directory,0755);err!=nil{panic(err)}
    if err=os.WriteFile(filepath.Join(e.directory,hex.EncodeToString(name[:])+".json"),bytes,0644);err!=nil{panic(err)}
}
