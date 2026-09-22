using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

if (args is ["--native-check"])
{
    if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
        throw new InvalidOperationException("Expected NativeAOT");
    Console.WriteLine(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
    return;
}
while (Console.ReadLine() is { } line)
{
    using JsonDocument document = JsonDocument.Parse(line);
    JsonElement request = document.RootElement;
    var source = new SourceText(request.GetProperty("text").GetBytesFromBase64());
    bool Flag(string key) => request.TryGetProperty(key, out var value) && value.GetBoolean();
    string name = request.TryGetProperty("fileName", out var fileName) ? fileName.GetString()! : "/test.ts";
    SourceFileNode file = Parser.ParseSourceFile(new(name, ForceExternalModule: Flag("force"), JsxExternalModule: Flag("jsx")), source);
    if (request.TryGetProperty("mode", out var documentationMode) && documentationMode.GetString() == "documentation")
    {
        using var docs = new MemoryStream();
        using (var writer = new Utf8JsonWriter(docs))
        {
            writer.WriteStartArray();
            writer.WriteStartArray();
            foreach (SyntaxNode host in file.DescendantsAndSelf())
            {
                var comments = file.GetDocumentation(host);
                if (comments.Count == 0)
                    continue;
                writer.WriteStartArray();
                writer.WriteNumberValue((int)host.Kind);
                writer.WriteNumberValue(source.ToUtf16Position(host.Pos));
                writer.WriteNumberValue(source.ToUtf16Position(host.End));
                writer.WriteStartArray();
                foreach (JSDocNode comment in comments)
                {
                    var visited = new HashSet<SyntaxNode>(ReferenceEqualityComparer.Instance);
                    writer.WriteStartArray();
                    foreach (SyntaxNode node in comment.DescendantsAndSelf())
                    {
                        if (!visited.Add(node))
                            throw new InvalidDataException("Duplicate node or cycle in a documentation tree");
                        if (node.Pos < 0 || node.End < node.Pos || node.End > source.Bytes.Length)
                            throw new InvalidDataException("Documentation node span is outside its source");
                        for (int i = 0; i < node.ChildCount; i++)
                            if (!ReferenceEquals(node.GetChild(i).Parent, node))
                                throw new InvalidDataException("Documentation child has a different parent");
                        writer.WriteStartArray();
                        writer.WriteNumberValue((int)node.Kind);
                        writer.WriteNumberValue(source.ToUtf16Position(node.Pos));
                        writer.WriteNumberValue(source.ToUtf16Position(node.End));
                        writer.WriteNumberValue((uint)node.Flags);
                        writer.WriteNumberValue(node.ChildCount);
                        AstScalarProperties.Write(writer, node);
                        AstScalarProperties.WriteLists(writer, node, source.ToUtf16Position);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteStartArray();
            foreach (var diagnostic in file.JSDocDiagnostics)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(diagnostic.Code);
                writer.WriteNumberValue(source.ToUtf16Position(diagnostic.Start));
                writer.WriteNumberValue(
                    source.ToUtf16Position(diagnostic.Start + diagnostic.Length) - source.ToUtf16Position(diagnostic.Start));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndArray();
        }
        Console.WriteLine(Encoding.UTF8.GetString(docs.GetBuffer().AsSpan(0, (int)docs.Length)));
        continue;
    }
    if (request.TryGetProperty("mode", out var mode) && mode.GetString() == "parse")
    {
        using var tree = new MemoryStream();
        using (var writer = new Utf8JsonWriter(tree))
        {
            writer.WriteStartArray();
            writer.WriteStartArray();
            foreach (SyntaxNode node in file.DescendantsAndSelf())
            {
                string value = node switch
                {
                    IdentifierNode n => n.Text,
                    PrivateIdentifierNode n => n.Text,
                    StringLiteralNode n => n.Text,
                    NumericLiteralNode n => n.Text,
                    BigIntLiteralNode n => n.Text,
                    RegularExpressionLiteralNode n => n.Text,
                    NoSubstitutionTemplateLiteralNode n => n.Text,
                    TemplateHeadNode n => n.Text,
                    TemplateMiddleNode n => n.Text,
                    TemplateTailNode n => n.Text,
                    JsxTextNode n => n.Text,
                    _ => "",
                };
                writer.WriteStartArray();
                writer.WriteNumberValue((int)node.Kind);
                writer.WriteNumberValue(source.ToUtf16Position(node.Pos));
                writer.WriteNumberValue(source.ToUtf16Position(node.End));
                writer.WriteNumberValue((uint)node.Flags);
                writer.WriteBase64StringValue(Wtf8.Encode(value));
                writer.WriteNumberValue(node.ChildCount);
                AstScalarProperties.Write(writer, node);
                AstScalarProperties.WriteLists(writer, node, source.ToUtf16Position);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteStartArray();
            foreach (var diagnostic in file.ParseDiagnostics)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(diagnostic.Code);
                writer.WriteNumberValue(source.ToUtf16Position(diagnostic.Start));
                writer.WriteNumberValue(
                    source.ToUtf16Position(diagnostic.Start + diagnostic.Length) - source.ToUtf16Position(diagnostic.Start));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteStartArray();
            foreach (var diagnostic in file.JSDocDiagnostics)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(diagnostic.Code);
                writer.WriteNumberValue(source.ToUtf16Position(diagnostic.Start));
                writer.WriteNumberValue(
                    source.ToUtf16Position(diagnostic.Start + diagnostic.Length) - source.ToUtf16Position(diagnostic.Start));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndArray();
        }
        Console.WriteLine(Encoding.UTF8.GetString(tree.GetBuffer().AsSpan(0, (int)tree.Length)));
        continue;
    }
    if (Flag("clone"))
        file = file.DeepClone<SourceFileNode>();
    if (file.ExternalModuleIndicator is { } indicator && !file.DescendantsAndSelf().Contains(indicator))
        throw new InvalidDataException("External-module indicator belongs to a different tree");
    foreach (SyntaxNode reference in file.Imports.Concat(file.ModuleAugmentations))
    {
        SyntaxNode owner = reference;
        while (owner.Parent is { } parent)
            owner = parent;
        if (!ReferenceEquals(owner, file))
            throw new InvalidDataException("Module reference belongs to a different tree");
    }
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer))
    {
        void Position(int pos) => writer.WriteNumberValue(source.ToUtf16Position(pos));
        void References(IReadOnlyList<FileReference> references)
        {
            writer.WriteStartArray();
            foreach (var reference in references)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(reference.FileName);
                Position(reference.Pos);
                Position(reference.End);
                writer.WriteNumberValue((int)reference.ResolutionMode);
                writer.WriteBooleanValue(reference.Preserve);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        void Nodes(IReadOnlyList<SyntaxNode> nodes)
        {
            writer.WriteStartArray();
            foreach (SyntaxNode node in nodes)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue((int)node.Kind);
                writer.WriteStringValue(
                    node switch
                    {
                        StringLiteralNode n => n.Text,
                        NoSubstitutionTemplateLiteralNode n => n.Text,
                        IdentifierNode n => n.Text,
                        _ => ""
                    });
                Position(node.Pos);
                Position(node.End);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        writer.WriteStartArray();
        writer.WriteStartArray();
        foreach (var pragma in file.Pragmas)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(pragma.Name);
            writer.WriteNumberValue((int)pragma.Range.Kind);
            Position(pragma.Range.Pos);
            Position(pragma.Range.End);
            writer.WriteBooleanValue(pragma.Range.HasTrailingNewLine);
            writer.WriteStartArray();
            foreach (var arg in pragma.Arguments.Values.OrderBy(arg => arg.Name, StringComparer.Ordinal))
            {
                writer.WriteStartArray();
                writer.WriteStringValue(arg.Name);
                writer.WriteStringValue(arg.Value);
                Position(arg.Pos);
                Position(arg.End);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        References(file.ReferencedFiles);
        References(file.TypeReferenceDirectives);
        References(file.LibReferenceDirectives);
        if (file.CheckJsDirective is { } check)
        {
            writer.WriteStartArray();
            writer.WriteBooleanValue(check.Enabled);
            Position(check.Range.Pos);
            Position(check.Range.End);
            writer.WriteEndArray();
        }
        else
            writer.WriteNullValue();
        if (file.ExternalModuleIndicator is { } external)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue((int)external.Kind);
            Position(external.Pos);
            Position(external.End);
            writer.WriteEndArray();
        }
        else
            writer.WriteNullValue();
        writer.WriteStartArray();
        foreach (var diagnostic in file.ParseDiagnostics)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(diagnostic.Code);
            Position(diagnostic.Start);
            writer.WriteNumberValue(
                source.ToUtf16Position(diagnostic.Start + diagnostic.Length) - source.ToUtf16Position(diagnostic.Start));
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        Nodes(file.Imports);
        Nodes(file.ModuleAugmentations);
        writer.WriteStartArray();
        foreach (string module in file.AmbientModuleNames)
            writer.WriteStringValue(module);
        writer.WriteEndArray();
        writer.WriteStartArray();
        foreach (var dependency in file.AmdDependencies)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(dependency.Path);
            writer.WriteStringValue(dependency.Name);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WriteStringValue(file.ModuleName);
        writer.WriteBooleanValue(file.HasNoDefaultLib);
        writer.WriteEndArray();
    }
    Console.WriteLine(Encoding.UTF8.GetString(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
}
