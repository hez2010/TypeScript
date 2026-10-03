// Development oracle only. The adapted encoder keeps the original format layout,
// replacing only the protocol version and positional unit with UTF-8 bytes.
package main

import (
    "bufio"
    "encoding/base64"
    "encoding/json"
    "crypto/sha256"
    "encoding/hex"
    "fmt"
    "os"
    "github.com/microsoft/TypeScript/tsc/internal/ast"
    "github.com/microsoft/TypeScript/tsc/internal/binder"
    "github.com/microsoft/TypeScript/tsc/internal/core"
    encoder "github.com/microsoft/TypeScript/tsc/internal/csharpastencoder"
    "github.com/microsoft/TypeScript/tsc/internal/parser"
    "github.com/microsoft/TypeScript/tsc/internal/spanmap"
    "github.com/microsoft/TypeScript/tsc/internal/testrunner"
    "github.com/microsoft/TypeScript/tsc/internal/tspath"
    "github.com/zeebo/xxh3"
)

type input struct {
    FileName string `json:"fileName"`
    Path string `json:"path"`
    Text string `json:"text"`
    ScriptKind core.ScriptKind `json:"scriptKind"`
    Force bool `json:"force"`
    JSX bool `json:"jsx"`
    Bind bool `json:"bind"`
    NodeKind ast.Kind `json:"nodeKind"`
    Tree *ast.CSharpSyntaxTree `json:"tree"`
    Metadata ast.CSharpEncodingMetadata `json:"metadata"`
    Mapping *mapping `json:"mapping"`
    Supplemental []string `json:"supplemental"`
    Canonical string `json:"canonical"`
    Compact bool `json:"compact"`
    Mode string `json:"mode"`
    UnitPath string `json:"unitPath"`
}

type mapping struct {
    Original string `json:"original"`
    Mapper string `json:"mapper"`
    VirtualFileName string `json:"virtualFileName"`
    Segments [][]int `json:"segments"`
    HasSpanMap bool `json:"hasSpanMap"`
    Directives [][]int `json:"directives"`
}

func main() {
    reader := bufio.NewScanner(os.Stdin); reader.Buffer(make([]byte, 4096), 64*1024*1024)
    writer := json.NewEncoder(os.Stdout)
    for reader.Scan() {
        var in input; if err := json.Unmarshal(reader.Bytes(), &in); err != nil { panic(err) }
        if in.Mode == "units" {
            content, err := os.ReadFile(in.UnitPath); if err != nil { panic(err) }
            units, _, _, _, err := testrunner.ParseTestFilesAndSymlinks(string(content), in.UnitPath, func(name string, content string, _ map[string]string) (map[string]string, error) {
                return map[string]string{"fileName":name, "text":base64.StdEncoding.EncodeToString([]byte(content))}, nil
            }); if err != nil { panic(err) }; if err := writer.Encode(units); err != nil { panic(err) }; continue
        }
        source, err := base64.StdEncoding.DecodeString(in.Text); if err != nil { panic(err) }
        if in.Path == "" { in.Path = in.FileName }
        sf := parser.ParseSourceFile(ast.SourceFileParseOptions{FileName: in.FileName, Path: tspath.Path(in.Path),
            ExternalModuleIndicatorOptions: ast.ExternalModuleIndicatorOptions{Force: in.Force, JSX: in.JSX}}, string(source), in.ScriptKind)
        sf.Hash = xxh3.Hash128(source)
        if in.Tree != nil {
            nodes := ast.CSharpLoadSyntax(sf, *in.Tree)
            ast.CSharpSetEncodingMetadata(sf, nodes, in.Metadata)
        } else if in.Bind { binder.BindSourceFile(sf) }
        if in.Mapping != nil || len(in.Supplemental) != 0 || in.Canonical != "" {
            info := ast.ContentMapperSourceFileInfo{}
            if m := in.Mapping; m != nil {
                original, err := base64.StdEncoding.DecodeString(m.Original); if err != nil { panic(err) }
                info.OriginalText, info.ContentMapper, info.VirtualFileName = string(original), m.Mapper, m.VirtualFileName
                if m.HasSpanMap {
                    segments := make([]spanmap.Segment, 0, len(m.Segments))
                    for _, s := range m.Segments { segments = append(segments, spanmap.Segment{VirtualStart:core.TextPos(s[0]), VirtualEnd:core.TextPos(s[1]), OriginalStart:core.TextPos(s[2]), OriginalEnd:core.TextPos(s[3]), Kind:spanmap.Kind(s[4]), Features:spanmap.Feature(s[5])}) }
                    info.SpanMap = spanmap.New(segments)
                }
                for _, d := range m.Directives { info.DiagnosticDirectives = append(info.DiagnosticDirectives, ast.MappedDiagnosticDirective{OriginalRange:core.NewTextRange(d[0],d[1]), VirtualRange:core.NewTextRange(d[2],d[3]), Policy:ast.MappedDiagnosticDirectivePolicy(d[4]), UnusedCode:int32(d[5])}) }
            }
            stub := func(name string) *ast.SourceFile { return parser.ParseSourceFile(ast.SourceFileParseOptions{FileName:name, Path:tspath.Path(name)}, "", core.ScriptKindTS) }
            for _, name := range in.Supplemental { info.SupplementalSourceFiles = append(info.SupplementalSourceFiles, stub(name)) }
            if in.Canonical != "" { info.CanonicalSourceFile = stub(in.Canonical) }
            sf.SetContentMapperInfo(info)
        }
        root := sf.AsNode()
        if in.NodeKind != 0 {
            table := encoder.BuildNodeIndexTable(sf)
            root = nil; for _, node := range table.Nodes { if node != nil && node.Kind == in.NodeKind { root = node; break } }
            if root == nil { panic(fmt.Sprintf("No node kind %d", in.NodeKind)) }
        }
        data, table, err := encoder.EncodeNode(root, sf); if err != nil { panic(err) }
        kinds := make([]int, len(table.Nodes)); for i, n := range table.Nodes { if n != nil { kinds[i] = int(n.Kind) } }
        if in.Compact {
            hash := sha256.Sum256(data)
            if err := writer.Encode(map[string]any{"hash":hex.EncodeToString(hash[:]), "nodes":len(table.Nodes)}); err != nil { panic(err) }
        } else if err := writer.Encode(map[string]any{"bytes": base64.StdEncoding.EncodeToString(data), "kinds": kinds}); err != nil { panic(err) }
    }
    if err := reader.Err(); err != nil { panic(err) }
}
