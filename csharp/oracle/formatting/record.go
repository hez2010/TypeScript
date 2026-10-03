package fourslash

import (
    "os"
    "sync"
    "testing"
    "github.com/microsoft/TypeScript/tsc/internal/ast"
    "github.com/microsoft/TypeScript/tsc/internal/json"
    "github.com/microsoft/TypeScript/tsc/internal/ls/lsutil"
    "github.com/microsoft/TypeScript/tsc/internal/lsp/lsproto"
    "github.com/microsoft/TypeScript/tsc/internal/project"
)

var csharpFormattingRecords sync.Mutex

func csharpFormattingEncoding() lsproto.PositionEncodingKind {
    if os.Getenv("CSHARP_FORMAT_ENCODING")=="utf-16" { return lsproto.PositionEncodingKindUTF16 }
    return lsproto.PositionEncodingKindUTF8
}

func (f *FourslashTest) csharpRecordFormatting[Params, Resp any](t *testing.T, info lsproto.RequestInfo[Params, Resp], params Params, result Resp) {
    var uri lsproto.DocumentUri
    var options *lsproto.FormattingOptions
    switch p:=any(params).(type) {
    case *lsproto.DocumentFormattingParams: uri=p.TextDocument.Uri;options=p.Options
    case *lsproto.DocumentRangeFormattingParams: uri=p.TextDocument.Uri;options=p.Options
    case *lsproto.DocumentOnTypeFormattingParams: uri=p.TextDocument.Uri;options=p.Options
    default:return
    }
    path:=os.Getenv("CSHARP_FORMAT_RECORD");if path=="" { return }
    script:=f.scriptInfos[uri.FileName()];if script==nil { t.Fatalf("missing formatting script %s",uri) }
    record:=map[string]any{"name":t.Name(),"method":info.Method,"params":params,"result":result,
        "file":script.fileName,"text":script.content,"options":lsutil.FromLSFormatOptions(f.userPreferences.FormatCodeSettings,options),"encoding":csharpFormattingEncoding()}
    f.client.Server.Session().WithSnapshotForDocument(t.Context(),uri,func(snapshot *project.Snapshot) {
        project:=snapshot.GetDefaultProject(uri);if project==nil { return }
        file:=project.GetProgram().GetSourceFile(uri.FileName());if file==nil||file.ContentMapper()=="" { return }
        projections:=[]any{}
        for _,projection:=range append([]*ast.SourceFile{file},file.SupplementalSourceFiles()...) {
            segments:=[][]int{}
            for _,segment:=range projection.SpanMap().Segments() {
                segments=append(segments,[]int{int(segment.VirtualStart),int(segment.VirtualEnd),int(segment.OriginalStart),int(segment.OriginalEnd),int(segment.Kind),int(segment.Features)})
            }
            projections=append(projections,map[string]any{"fileName":projection.FileName(),"text":projection.Text(),"original":projection.OriginalText(),"scriptKind":int(projection.ScriptKind),"segments":segments})
        }
        record["projections"]=projections
    })
    bytes,err:=json.Marshal(record);if err!=nil { t.Fatal(err) }
    csharpFormattingRecords.Lock();defer csharpFormattingRecords.Unlock()
    output,err:=os.OpenFile(path,os.O_CREATE|os.O_WRONLY|os.O_APPEND,0600);if err!=nil { t.Fatal(err) };defer output.Close()
    if _,err=output.Write(append(bytes,'\n'));err!=nil { t.Fatal(err) }
}
