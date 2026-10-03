package fourslash

import (
    "os"
    "sync"
    "maps"
    "slices"
    "bytes"
    stdjson "encoding/json"
    "testing"
    iofs "io/fs"
    "github.com/microsoft/TypeScript/tsc/internal/json"
    "github.com/microsoft/TypeScript/tsc/internal/jsonrpc"
    "github.com/microsoft/TypeScript/tsc/internal/lsp/lsproto"
    "github.com/microsoft/TypeScript/tsc/internal/ast"
    "github.com/microsoft/TypeScript/tsc/internal/project"
    "github.com/microsoft/TypeScript/tsc/internal/compiler"
    "github.com/microsoft/TypeScript/tsc/internal/ls/lsutil"
    "github.com/microsoft/TypeScript/tsc/internal/vfs"
    "github.com/microsoft/TypeScript/tsc/internal/locale"
)

var csharpSyntaxRecords sync.Mutex

// Preserve preceding completion queries while the captured project inputs are unchanged.
// Preferences intentionally remain in each query: changing them can reuse the registry's specifier cache.
type csharpCompletionState struct {
    files map[string]string
    context []byte
    queries []stdjson.RawMessage
}

func csharpSyntaxEncoding() lsproto.PositionEncodingKind {
    if os.Getenv("CSHARP_SYNTAX_ENCODING") == "utf-16" { return lsproto.PositionEncodingKindUTF16 }
    return lsproto.PositionEncodingKindUTF8
}

func (f *FourslashTest) csharpRecordSyntax[Params, Resp any](t *testing.T, info lsproto.RequestInfo[Params, Resp], params Params, result Resp, raw ...any) {
    if os.Getenv("CSHARP_SYNTAX_MODE") == "file-rename" {
        if info.Method == lsproto.MethodWorkspaceWillRenameFiles { f.csharpRecordFileRename(t, any(params), raw[0]) }
        return
    }
    if os.Getenv("CSHARP_SYNTAX_MODE") == "workspace" {
        if info.Method == lsproto.MethodWorkspaceSymbol { f.csharpRecordWorkspace(t, any(params).(*lsproto.WorkspaceSymbolParams), any(result)) }
        return
    }
    codeActions := info.Method == lsproto.MethodTextDocumentCodeAction
    if os.Getenv("CSHARP_SYNTAX_MODE") == "code-actions" && !codeActions { return }
    completion := info.Method == lsproto.MethodTextDocumentCompletion || info.Method == lsproto.MethodCompletionItemResolve
    if os.Getenv("CSHARP_SYNTAX_MODE") == "completion" && !completion { return }
    diagnostics := info.Method == lsproto.MethodTextDocumentDiagnostic
    if os.Getenv("CSHARP_SYNTAX_MODE") == "diagnostics" && !diagnostics { return }
    autoInsert := info.Method == lsproto.MethodTextDocumentVSOnAutoInsert
    if os.Getenv("CSHARP_SYNTAX_MODE") == "auto-insert" && !autoInsert { return }
    inlayHints := info.Method == lsproto.MethodTextDocumentInlayHint
    if os.Getenv("CSHARP_SYNTAX_MODE") == "inlay-hints" && !inlayHints { return }
    codeLens := info.Method == lsproto.MethodTextDocumentCodeLens || info.Method == lsproto.MethodCodeLensResolve
    if os.Getenv("CSHARP_SYNTAX_MODE") == "code-lens" && !codeLens { return }
    callHierarchy := info.Method == lsproto.MethodTextDocumentPrepareCallHierarchy || info.Method == lsproto.MethodCallHierarchyIncomingCalls || info.Method == lsproto.MethodCallHierarchyOutgoingCalls
    if os.Getenv("CSHARP_SYNTAX_MODE") == "call-hierarchy" && !callHierarchy { return }
    rename := info.Method == lsproto.MethodTextDocumentRename || info.Method == lsproto.MethodTextDocumentPrepareRename
    if os.Getenv("CSHARP_SYNTAX_MODE") == "rename" && !rename { return }
    semantic := info.Method == lsproto.MethodTextDocumentSemanticTokensFull || info.Method == lsproto.MethodTextDocumentSemanticTokensRange
    hover := info.Method == lsproto.MethodTextDocumentHover
    signature := info.Method == lsproto.MethodTextDocumentSignatureHelp
    definitions := info.Method == lsproto.MethodTextDocumentDefinition || info.Method == lsproto.MethodTextDocumentTypeDefinition || info.Method == lsproto.MethodCustomTextDocumentSourceDefinition
    references := info.Method == lsproto.MethodTextDocumentReferences || info.Method == lsproto.MethodTextDocumentVSReferences
    highlights := info.Method == lsproto.MethodTextDocumentDocumentHighlight || info.Method == lsproto.MethodCustomTextDocumentMultiDocumentHighlight
    if os.Getenv("CSHARP_SYNTAX_MODE") == "highlights" && !highlights { return }
    implementations := info.Method == lsproto.MethodTextDocumentImplementation
    if os.Getenv("CSHARP_SYNTAX_MODE") == "references" && info.Method != lsproto.MethodTextDocumentReferences { return }
    if os.Getenv("CSHARP_SYNTAX_MODE") == "vs-references" && info.Method != lsproto.MethodTextDocumentVSReferences { return }
    if os.Getenv("CSHARP_SYNTAX_MODE") == "implementations" && !implementations { return }
    if os.Getenv("CSHARP_SYNTAX_MODE") == "definitions" && !definitions { return }
    if os.Getenv("CSHARP_SYNTAX_MODE") == "signature" && !signature { return }
    if os.Getenv("CSHARP_SYNTAX_MODE") == "hover" && !hover { return }
    if !semantic && !hover && !signature && !definitions && !references && !implementations && !highlights && !rename && !callHierarchy && !codeLens && !inlayHints && !autoInsert && !diagnostics && !completion && !codeActions && info.Method != lsproto.MethodTextDocumentSelectionRange && info.Method != lsproto.MethodTextDocumentLinkedEditingRange && info.Method != lsproto.MethodTextDocumentFoldingRange && info.Method != lsproto.MethodTextDocumentDocumentSymbol { return }
    path := os.Getenv("CSHARP_SYNTAX_RECORD")
    if path == "" { return }
    files := map[string]string{}
    for name, script := range f.scriptInfos { files[name] = script.content }
    if references || implementations || highlights || rename || callHierarchy || codeLens || inlayHints || autoInsert || diagnostics || completion || codeActions {
        if err := vfs.WalkDir(f.vfs, "/", func(name string, entry iofs.DirEntry, err error) error {
            if err != nil { return err }; if entry.IsDir() { return nil }
            if _, exists := files[name]; !exists { if text, ok := f.vfs.ReadFile(name); ok { files[name] = text } }
            return nil
        }); err != nil { t.Fatal(err) }
    }
    record := map[string]any{"name": t.Name(), "method": info.Method, "params": params, "result": result, "files": files, "encoding": csharpSyntaxEncoding(), "symlinks": f.testData.Symlinks, "caseSensitive": f.vfs.UseCaseSensitiveFileNames()}
    if codeLens { record["codeLensShowLocationsCommandName"] = showCodeLensLocationsCommandName }
    if references || implementations || highlights || rename || callHierarchy || codeLens || inlayHints || autoInsert || diagnostics || completion || codeActions { record["inferredOptions"] = f.csharpInferredOptions }
    if (signature || definitions || references || implementations || highlights || rename || callHierarchy || codeLens || inlayHints || autoInsert || diagnostics || completion || codeActions) && len(raw) != 0 { record["result"] = raw[0] }
    if rename && len(raw) > 1 && raw[1] != (*jsonrpc.ResponseError)(nil) { record["error"] = raw[1] }
    if info.Method == lsproto.MethodTextDocumentFoldingRange { record["foldingCapabilities"] = f.capabilities.TextDocument.FoldingRange }
    if semantic { record["semanticCapabilities"] = f.capabilities.TextDocument.SemanticTokens }
    if hover || signature || definitions || references || implementations || highlights || rename || callHierarchy || codeLens || inlayHints || autoInsert || diagnostics || completion || codeActions {
        record["capabilities"] = f.capabilities; record["preferences"] = f.userPreferences
        opened := []string{}; for name := range f.openFiles { opened = append(opened,name) }; record["openFiles"] = opened
    }
    if info.Method == lsproto.MethodTextDocumentDocumentSymbol { record["symbolCapabilities"] = f.capabilities.TextDocument.DocumentSymbol }
    var uri lsproto.DocumentUri
    switch p := any(params).(type) {
    case *lsproto.CompletionItem: if p.Data != nil { uri = lsproto.DocumentUri("file://" + p.Data.FileName) }
    case *lsproto.CodeActionParams: uri = p.TextDocument.Uri
    case *lsproto.CompletionParams: uri = p.TextDocument.Uri
    case *lsproto.DocumentDiagnosticParams: uri = p.TextDocument.Uri
    case *lsproto.VSOnAutoInsertParams: uri = p.VSTextDocument.Uri
    case *lsproto.InlayHintParams: uri = p.TextDocument.Uri
    case *lsproto.CodeLensParams: uri = p.TextDocument.Uri
    case *lsproto.CodeLens: uri = p.Data.Uri
    case *lsproto.CallHierarchyPrepareParams: uri = p.TextDocument.Uri
    case *lsproto.CallHierarchyIncomingCallsParams: uri = p.Item.Uri
    case *lsproto.CallHierarchyOutgoingCallsParams: uri = p.Item.Uri
    case *lsproto.RenameParams: uri = p.TextDocument.Uri
    case *lsproto.PrepareRenameParams: uri = p.TextDocument.Uri
    case *lsproto.SelectionRangeParams: uri = p.TextDocument.Uri
    case *lsproto.LinkedEditingRangeParams: uri = p.TextDocument.Uri
    case *lsproto.FoldingRangeParams: uri = p.TextDocument.Uri
    case *lsproto.SemanticTokensParams: uri = p.TextDocument.Uri
    case *lsproto.SemanticTokensRangeParams: uri = p.TextDocument.Uri
    case *lsproto.DocumentSymbolParams: uri = p.TextDocument.Uri
    case *lsproto.HoverParams: uri = p.TextDocument.Uri
    case *lsproto.SignatureHelpParams: uri = p.TextDocument.Uri
    case *lsproto.DefinitionParams: uri = p.TextDocument.Uri
    case *lsproto.TypeDefinitionParams: uri = p.TextDocument.Uri
    case *lsproto.DocumentHighlightParams: uri = p.TextDocument.Uri
    case *lsproto.MultiDocumentHighlightParams: uri = p.TextDocument.Uri
    case *lsproto.ReferenceParams: uri = p.TextDocument.Uri
    case *lsproto.ImplementationParams: uri = p.TextDocument.Uri
    case *lsproto.TextDocumentPositionParams: uri = p.TextDocument.Uri
    }
    f.client.Server.Session().WithSnapshotForDocument(t.Context(), uri, func(snapshot *project.Snapshot) {
        project := snapshot.GetDefaultProject(uri)
        if project == nil { return }
        record["projectId"] = project.ID().String()
        if hover { record["maximumHoverLength"] = snapshot.UserPreferences().MaximumHoverLength }
        if semantic || hover || signature || definitions || references || implementations || highlights || rename || callHierarchy || codeLens || inlayHints || autoInsert || diagnostics || completion || codeActions {
            program := project.GetProgram()
            record["compilerOptions"] = program.Options()
            record["rootFiles"] = program.CommandLine().FileNames()
            record["cwd"] = f.client.Server.Session().GetCurrentDirectory()
            for _, source := range program.GetSourceFiles() {
                if !program.IsSourceFileDefaultLibrary(source.Path()) { files[source.FileName()] = source.Text() }
            }
            if hover || signature || definitions || references || implementations || highlights || rename || callHierarchy || codeLens || inlayHints || autoInsert || diagnostics || completion || codeActions {
                mappedFiles := []any{}
                for _, source := range program.GetSourceFiles() {
                    if source.ContentMapper() == "" { continue }
                    segments := [][]int{}
                    for _, segment := range source.SpanMap().Segments() {
                        segments = append(segments,[]int{int(segment.VirtualStart),int(segment.VirtualEnd),int(segment.OriginalStart),int(segment.OriginalEnd),int(segment.Kind),int(segment.Features)})
                    }
                    mappedFiles = append(mappedFiles,csharpMapperInput(source, segments))
                }
                if len(mappedFiles) != 0 { record["mappedFiles"] = mappedFiles }
            }
        }
        file := project.GetProgram().GetSourceFile(uri.FileName())
        if file == nil || file.ContentMapper() == "" { return }
        projections := []any{}
        for _, projection := range append([]*ast.SourceFile{file}, file.SupplementalSourceFiles()...) {
            segments := [][]int{}
            for _, segment := range projection.SpanMap().Segments() {
                segments = append(segments, []int{int(segment.VirtualStart),int(segment.VirtualEnd),int(segment.OriginalStart),int(segment.OriginalEnd),int(segment.Kind),int(segment.Features)})
            }
            projections = append(projections,map[string]any{"fileName":projection.FileName(),"originalName":projection.OriginalFileName(),"mapper":projection.ContentMapper(),"text":projection.Text(),"original":projection.OriginalText(),"scriptKind":int(projection.ScriptKind),"segments":segments})
        }
        record["projections"] = projections
    })
    if info.Method == lsproto.MethodTextDocumentCompletion {
        opened := slices.Clone(record["openFiles"].([]string)); slices.Sort(opened)
        context, err := json.Marshal([]any{record["compilerOptions"], record["rootFiles"], record["projectId"], opened, record["mappedFiles"], record["projections"]})
        if err != nil { t.Fatal(err) }
        state := f.csharpCompletionState
        if state == nil || !maps.Equal(state.files, files) || !bytes.Equal(state.context, context) {
            state = &csharpCompletionState{files: maps.Clone(files), context: context}
            f.csharpCompletionState = state
        }
        if len(state.queries) != 0 { record["priorCompletions"] = state.queries }
        query, err := json.Marshal(map[string]any{"params":params,"preferences":f.userPreferences,"capabilities":f.capabilities})
        if err != nil { t.Fatal(err) }
        csharpWriteSyntaxRecord(t, path, record)
        state.queries = append(state.queries, query)
    } else { csharpWriteSyntaxRecord(t, path, record) }
}

func (f *FourslashTest) csharpRecordFileRename(t *testing.T, params any, result any) {
    files := map[string]string{}
    for name, script := range f.scriptInfos { files[name] = script.content }
    if err := vfs.WalkDir(f.vfs, "/", func(name string, entry iofs.DirEntry, err error) error {
        if err != nil { return err }; if entry.IsDir() { return nil }
        if _, exists := files[name]; !exists { if text, ok := f.vfs.ReadFile(name); ok { files[name] = text } }
        return nil
    }); err != nil { t.Fatal(err) }
    record := map[string]any{"name":t.Name(),"method":lsproto.MethodWorkspaceWillRenameFiles,"params":params,"result":result,
        "files":files,"encoding":csharpSyntaxEncoding(),"cwd":f.client.Server.Session().GetCurrentDirectory(),"preferences":f.userPreferences,
        "capabilities":f.capabilities,"symlinks":f.testData.Symlinks,"caseSensitive":f.vfs.UseCaseSensitiveFileNames(),"inferredOptions":f.csharpInferredOptions}
    opened:=[]string{}; for name:=range f.openFiles { opened=append(opened,name) }; record["openFiles"]=opened
    f.client.Server.Session().WithSnapshotLoadingProjectTree(t.Context(),nil,func(snapshot *project.Snapshot) {
        programs:=[]any{}; mappedFiles:=[]any{}; seen:=map[string]bool{}
        for _, project:=range snapshot.ProjectCollection.LanguageServiceProjects() {
            program:=project.GetProgram(); if program==nil {continue}
            programs=append(programs,map[string]any{"compilerOptions":program.Options(),"rootFiles":program.CommandLine().FileNames(),"projectId":project.ID().String()})
            for _, source:=range program.GetSourceFiles() {
                if !program.IsSourceFileDefaultLibrary(source.Path()) { files[source.FileName()]=source.Text() }
                if source.ContentMapper()=="" || seen[source.FileName()] {continue};seen[source.FileName()]=true
                segments:=[][]int{}
                for _, segment:=range source.SpanMap().Segments() {segments=append(segments,[]int{int(segment.VirtualStart),int(segment.VirtualEnd),int(segment.OriginalStart),int(segment.OriginalEnd),int(segment.Kind),int(segment.Features)})}
                mappedFiles=append(mappedFiles,map[string]any{"fileName":source.FileName(),"originalName":source.OriginalFileName(),"mapper":source.ContentMapper(),"text":source.Text(),"original":source.OriginalText(),"scriptKind":int(source.ScriptKind),"segments":segments})
            }
        }
        record["programs"]=programs
        if len(mappedFiles)!=0 {record["mappedFiles"]=mappedFiles}
    })
    csharpWriteSyntaxRecord(t,os.Getenv("CSHARP_SYNTAX_RECORD"),record)
}

func csharpWriteSyntaxRecord(t *testing.T, path string, record any) {
    bytes, err := json.Marshal(record)
    if err != nil { t.Fatal(err) }
    csharpSyntaxRecords.Lock()
    defer csharpSyntaxRecords.Unlock()
    output, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0600)
    if err != nil { t.Fatal(err) }
    defer output.Close()
    if _,err := output.Write(append(bytes, '\n')); err != nil { t.Fatal(err) }
}

func (f *FourslashTest) csharpRecordWorkspace(t *testing.T, params *lsproto.WorkspaceSymbolParams, result any) {
    path := os.Getenv("CSHARP_SYNTAX_RECORD")
    if path == "" { return }
    files := map[string]string{}
    for name, script := range f.scriptInfos { files[name] = script.content }
    record := map[string]any{"name":t.Name(),"method":lsproto.MethodWorkspaceSymbol,"params":params,"result":result,"files":files,
        "encoding":csharpSyntaxEncoding(),"cwd":f.client.Server.Session().GetCurrentDirectory(),"preferences":f.userPreferences}
    opened := []string{}
    for name := range f.openFiles { opened = append(opened,name) }
    record["openFiles"] = opened
    capture := func(snapshot *project.Snapshot, programs []*compiler.Program) {
        record["excludeLibrarySymbols"] = snapshot.UserPreferences().ExcludeLibrarySymbolsInNavTo.IsTrue()
        inputs := []any{}
        projections := []any{}
        for _, program := range programs {
            inputs = append(inputs,map[string]any{"compilerOptions":program.Options(),"rootFiles":program.CommandLine().FileNames()})
            for _, source := range program.GetSourceFiles() {
                if !program.IsSourceFileDefaultLibrary(source.Path()) { files[source.FileName()] = source.Text() }
                if source.ContentMapper() != "" {
                    segments:=[][]int{}
                    for _, segment:=range source.SpanMap().Segments() { segments=append(segments,[]int{int(segment.VirtualStart),int(segment.VirtualEnd),int(segment.OriginalStart),int(segment.OriginalEnd),int(segment.Kind),int(segment.Features)}) }
                    projections=append(projections,map[string]any{"fileName":source.FileName(),"originalName":source.OriginalFileName(),"mapper":source.ContentMapper(),"text":source.Text(),"original":source.OriginalText(),"scriptKind":int(source.ScriptKind),"segments":segments})
                }
            }
        }
        record["programs"] = inputs
        if len(projections)!=0 { record["projections"]=projections }
    }
    session := f.client.Server.Session()
    if params.TextDocument != nil && session.Config().WorkspaceSymbolsScope == lsutil.WorkspaceSymbolsScopeCurrentProject {
        session.WithSnapshotForDocument(t.Context(),params.TextDocument.Uri,func(snapshot *project.Snapshot) {
            programs := []*compiler.Program{}
            for _, project := range snapshot.GetLanguageServiceProjectsContainingFile(params.TextDocument.Uri) { programs = append(programs,project.GetProgram()) }
            capture(snapshot,programs)
        })
    } else {
        session.WithSnapshotLoadingProjectTree(t.Context(),nil,func(snapshot *project.Snapshot) {
            programs := []*compiler.Program{}
            for _, project := range snapshot.ProjectCollection.LanguageServiceProjects() { programs = append(programs,project.GetProgram()) }
            capture(snapshot,programs)
        })
    }
    csharpWriteSyntaxRecord(t,path,record)
}

// The captured values below are external mapper inputs, not compiler diagnostic expectations.
func csharpMapperInput(file *ast.SourceFile, segments [][]int) map[string]any {
    result := map[string]any{"fileName":file.FileName(),"originalName":file.OriginalFileName(),"mapper":file.ContentMapper(),
        "text":file.Text(),"original":file.OriginalText(),"scriptKind":int(file.ScriptKind),"segments":segments}
    if file.IsContentMapperFailureStub() {result["transformFailed"]=true}
    external := []any{}
    for _, diagnostic := range file.Diagnostics() {
        if diagnostic.Source() != "" {
            result["diagnosticSource"]=diagnostic.Source()
            external=append(external,map[string]any{"start":diagnostic.Pos(),"length":diagnostic.Len(),"code":diagnostic.Code(),"messageText":diagnostic.Localize(locale.Default)})
        }
    }
    if len(external)!=0 {result["externalDiagnostics"]=external}
    directives:=[][]int{};unused:=[]any{}
    for _, directive:=range file.DiagnosticDirectives() {
        result["diagnosticSource"]=directive.Source
        tuple:=[]int{directive.OriginalRange.Pos(),directive.OriginalRange.Len(),directive.VirtualRange.Pos(),directive.VirtualRange.End(),int(directive.Policy)}
        if directive.Policy==ast.MappedDiagnosticDirectivePolicyExpect {
            tuple=append(tuple,len(unused));unused=append(unused,map[string]any{"code":directive.UnusedCode,"messageText":directive.UnusedMessageText})
        }
        directives=append(directives,tuple)
    }
    if len(directives)!=0 {result["diagnosticDirectives"]=map[string]any{"directives":directives,"unusedExpectDirectiveDiagnostics":unused}}
    return result
}
