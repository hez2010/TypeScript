using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Syntax;
using R = TypeScript.Compiler.LanguageServices.ReferenceNavigation;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed record RenameInfo(bool CanRename, Utf8String ErrorMessage = default, Utf8String DisplayName = default,
    DocumentRange TriggerSpan = default, Utf8String FileToRename = default, Utf8String NewFileName = default);
public readonly record struct RenameCapabilities(bool DocumentChanges = false, bool RenameResourceOperations = false, bool WillRenameFiles = false);
public abstract record WorkspaceDocumentChange;
public sealed record RenameFileChange(Utf8String OldUri, Utf8String NewUri) : WorkspaceDocumentChange;
public sealed record TextDocumentChange(Utf8String Uri, IReadOnlyList<DocumentTextEdit> Edits) : WorkspaceDocumentChange;
public sealed record WorkspaceEdit(IReadOnlyDictionary<Utf8String, IReadOnlyList<DocumentTextEdit>>? Changes = null,
    IReadOnlyList<WorkspaceDocumentChange>? DocumentChanges = null);

public sealed partial class LanguageServiceDocument
{
    public async ValueTask<RenameInfo> GetRenameInfoAsync(ProjectSnapshot project, DocumentPosition position, Utf8String newName = default,
        UserPreferences? preferences = null, RenameCapabilities capabilities = default, CancellationToken cancellation = default)
    {
        using var request = new ProjectRequest(cancellation);
        using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request);
        foreach (var (projection, mapped) in FromPosition(position, MappingFeature.Rename))
        {
            if (mapped.Fidelity != MappingFidelity.Exact) continue;
            var node = await R.AdjustedAsync(await SyntaxNavigation.GetTouchingPropertyNameAsync(projection.File, mapped.Position, cancellation), true, cancellation);
            if (RenameEligible(node) && await RenameInfoAsync(project.Program!, lease.Checker, node, newName, preferences ?? new(), capabilities, cancellation) is { } info)
                return info;
        }
        return RenameError(Messages.You_cannot_rename_this_element);
    }

    public async ValueTask<WorkspaceEdit?> GetRenameEditsAsync(ProjectSnapshot project, DocumentPosition position, Utf8String newName,
        UserPreferences? preferences = null, RenameCapabilities capabilities = default, CancellationToken cancellation = default)
    {
        using var request = new ProjectRequest(cancellation);
        using var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request);
        preferences ??= new();
        var data = await ReferenceDataAsync(project, position, new(ReferenceUse.Rename, UseAliasesForRename: preferences.UseAliasesForRename), lease.Checker, cancellation);
        return await RenameEditsAsync(project, data, lease.Checker, newName, preferences, capabilities, cancellation);
    }

    internal async ValueTask<WorkspaceEdit?> RenameEditsAsync(ProjectSnapshot project, IReadOnlyList<ReferenceData> data, Checker checker,
        Utf8String newName, UserPreferences preferences, RenameCapabilities capabilities, CancellationToken cancellation)
    {
        if (data.FirstOrDefault()?.Node is not { } original || !RenameEligible(original)
            || await RenameInfoAsync(project.Program!, checker, original, newName, preferences, capabilities, cancellation) is not { CanRename: true }) return null;
        var program = project.Program!;
        var maps = new DeclarationMaps(program, cancellation);
        var quote = RenameQuote(SemanticSyntax.Source(original)!, preferences);
        Dictionary<(Utf8String, DocumentRange), Utf8String> texts = [];
        Dictionary<Utf8String, List<DocumentTextEdit>> changes = [];
        foreach (var entry in data.SelectMany(query => query.Groups).SelectMany(group => group.Entries))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!preferences.AllowRenameOfImportPath && RenameImport(entry.Node)) continue;
            var (file, start, end) = await ReferenceRangeAsync(entry, cancellation);
            var projection = ProjectionForFile(program, file);
            var mapped = projection.ToRange(start, end);
            var uri = DocumentUris.FromFileName(projection.OriginalFileName);
            if (!projection.IsMapped)
            {
                var reference = ReferenceMap(program, maps, file, start, end, MappingFeature.Rename);
                mapped = (reference.Range, reference.Fidelity); uri = reference.Uri;
            }
            if (mapped.Fidelity != MappingFidelity.Exact) continue;
            var text = await RenameTextAsync(checker, original, entry, newName, quote, preferences.UseAliasesForRename, cancellation);
            if (texts.TryGetValue((uri, mapped.Range), out var previous)) { if (previous != text) return null; continue; }
            texts.Add((uri, mapped.Range), text);
            if (!changes.TryGetValue(uri, out var edits)) changes.Add(uri, edits = []);
            edits.Add(new(mapped.Range, text));
        }
        return changes.Count == 0 ? null : new(changes.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<DocumentTextEdit>)pair.Value));
    }

    internal static bool RenameEligible(SyntaxNode node) => node is IdentifierNode or PrivateIdentifierNode or StringLiteralNode or NoSubstitutionTemplateLiteralNode
        || node.Kind == K.ThisKeyword || node is NumericLiteralNode && R.LiteralProperty(node);

    internal static bool RenameImport(SyntaxNode? node) => node is StringLiteralNode or NoSubstitutionTemplateLiteralNode && node.Parent is { } parent
        && (parent is ImportDeclarationNode or ExportDeclarationNode or ExternalModuleReferenceNode || parent.Kind == K.JSImportDeclaration
            || parent is CallExpressionNode call && (call.Expression?.Kind == K.ImportKeyword || call.Expression is IdentifierNode id && id.Text == "require"u8)
            || node is StringLiteralNode && parent is LiteralTypeNode { Parent: ImportTypeNode });

    private async ValueTask<RenameInfo?> RenameInfoAsync(CompilerProgram program, Checker checker, SyntaxNode node, Utf8String newName,
        UserPreferences preferences, RenameCapabilities capabilities, CancellationToken cancellation)
    {
        var file = SemanticSyntax.Source(node)!;
        var symbol = await checker.GetSymbolAtLocationAsync(node, cancellation);
        if (symbol is null)
        {
            if (node is StringLiteralNode or NoSubstitutionTemplateLiteralNode)
            {
                var type = await checker.GetReferenceStringContextAsync(node, cancellation);
                if (type is null || (type.Flags & TypeFlags.StringLiteral) == 0 && !(type is UnionType union && union.Types.All(t => (t.Flags & TypeFlags.StringLiteral) != 0))) return null;
            }
            else if (node.Parent is not (LabeledStatementNode or BreakStatementNode or ContinueStatementNode)) return null;
            return await SuccessAsync(R.Text(node));
        }
        if (symbol.Declarations.Count == 0) return null;
        foreach (var declaration in symbol.Declarations)
            if (SemanticSyntax.Source(declaration) is { } source && program.GetFile(source.FileName)?.Library == true && CompilerPath.IsDeclarationFile(source.FileName))
                return RenameError(Messages.You_cannot_rename_elements_that_are_defined_in_the_standard_TypeScript_library);
        if (node is IdentifierNode id && id.Text == "default"u8 && symbol.Parent is { } parentSymbol && (parentSymbol.Flags & SymbolFlags.Module) != 0)
            return RenameError(Messages.You_cannot_rename_this_element);
        var target = symbol;
        if (!preferences.UseAliasesForRename && (symbol.Flags & SymbolFlags.Alias) != 0 && symbol.Declarations.OfType<ImportSpecifierNode>().FirstOrDefault() is { PropertyName: null })
            target = await checker.GetAliasedSymbolAsync(symbol, cancellation);
        var package = RenamePackage(file.FileName);
        foreach (var declaration in target.Declarations)
        {
            var path = SemanticSyntax.Source(declaration)!.FileName;
            if (package.IsEmpty && path.Contains("/node_modules/"u8, StringComparison.Ordinal))
                return RenameError(Messages.You_cannot_rename_elements_that_are_defined_in_a_node_modules_folder);
            if (!package.IsEmpty && RenamePackage(path) is { IsEmpty: false } other && other != package)
                return RenameError(Messages.You_cannot_rename_elements_that_are_defined_in_another_node_modules_folder);
        }
        if (RenameImport(node))
        {
            if (!preferences.AllowRenameOfImportPath) return null;
            var specifier = R.Text(node);
            if (!Resolution.ModuleResolver.Relative(specifier)) return RenameError(Messages.You_cannot_rename_a_module_via_a_global_import);
            if (!capabilities.DocumentChanges || !capabilities.RenameResourceOperations) return RenameError(Messages.File_rename_is_not_supported_by_the_editor);
            if (symbol.Declarations.OfType<SourceFileNode>().FirstOrDefault() is not { } module) return null;
            var path = module.FileName;
            if (!specifier.EndsWith("/index"u8, StringComparison.Ordinal) && !specifier.EndsWith("/index.js"u8, StringComparison.Ordinal))
            {
                var withoutExtension = RenameRemoveExtension(path);
                if (withoutExtension.EndsWith("/index"u8, StringComparison.Ordinal)) path = withoutExtension[..^6];
            }
            var newPath = CompilerPath.Combine(CompilerPath.DirectoryName(path), newName);
            var extension = RenameExtension(path);
            var newExtension = CompilerPath.Extension(newPath);
            if (newExtension.IsEmpty) newPath += extension;
            else if (newExtension == CompilerPath.Extension(specifier)) newPath = newPath[..^newExtension.Length] + extension;
            int component = specifier.LastIndexOf((byte)'/') + 1;
            int start = await SyntaxNavigation.GetStartAsync(node, file, cancellation: cancellation) + 1 + component;
            var span = ProjectionForFile(program, file).ToRange(start, start + specifier.Length - component);
            return span.Fidelity == MappingFidelity.Exact ? new(true, DisplayName: specifier[component..], TriggerSpan: span.Range, FileToRename: path, NewFileName: newPath) : null;
        }
        return await SuccessAsync(await checker.GetSymbolDisplayNameAsync(symbol, cancellation: cancellation));

        async ValueTask<RenameInfo> SuccessAsync(Utf8String name)
        {
            int start = await SyntaxNavigation.GetStartAsync(node, file, cancellation: cancellation), end = node.End;
            if (node is StringLiteralNode or NoSubstitutionTemplateLiteralNode) { start++; end--; }
            var span = ProjectionForFile(program, file).ToRange(start, end);
            return span.Fidelity == MappingFidelity.Exact ? new(true, DisplayName: name, TriggerSpan: span.Range) : new(false);
        }
    }

    private static RenameInfo RenameError(DiagnosticMessage message) => new(false, message.Text);
    private static Utf8String RenamePackage(Utf8String name)
    {
        name = CompilerPath.Normalize(name);
        int offset = name.LastIndexOf("/node_modules/"u8, StringComparison.Ordinal);
        if (offset < 0) return default;
        offset += 14;
        int end = name.IndexOf((byte)'/', offset);
        if (end < 0) return name;
        if (name[offset] == '@') { end = name.IndexOf((byte)'/', end + 1); if (end < 0) return name; }
        return name[..end];
    }
    internal static Utf8String RenameExtension(Utf8String name)
    {
        if (!CompilerPath.IsDeclarationFile(name)) return CompilerPath.Extension(name);
        foreach (var extension in new Utf8String[] { ".d.ts"u8, ".d.mts"u8, ".d.cts"u8 })
            if (name.EndsWith(extension, StringComparison.Ordinal)) return extension;
        var baseName = CompilerPath.BaseName(name);
        return baseName[baseName.IndexOf(".d."u8)..];
    }
    private static Utf8String RenameRemoveExtension(Utf8String name)
    {
        var extension = RenameExtension(name);
        return extension.IsEmpty ? name : name[..^extension.Length];
    }
    private static Utf8String RenameQuote(SourceFileNode file, UserPreferences preferences)
    {
        if (!preferences.QuotePreference.IsEmpty && preferences.QuotePreference != "auto"u8) return preferences.QuotePreference == "single"u8 ? "'"u8 : "\""u8;
        var specifier = file.Imports.OfType<StringLiteralNode>().FirstOrDefault(node => node.Parent?.Pos >= 0);
        return specifier is not null && (specifier.TokenFlags & TokenFlags.SingleQuote) != 0 ? "'"u8 : "\""u8;
    }
    private static async ValueTask<Utf8String> RenameTextAsync(Checker checker, SyntaxNode original, ReferenceEntry entry, Utf8String text,
        Utf8String quote, bool aliases, CancellationToken cancellation)
    {
        if (aliases && entry.Kind != ReferenceEntryKind.Range && original is IdentifierNode or StringLiteralNode or NoSubstitutionTemplateLiteralNode)
        {
            var node = QuerySyntax.Reparsed(entry.Node!);
            var parent = node.Parent;
            var name = R.Text(original);
            if (parent is ShorthandPropertyAssignmentNode || R.ObjectBinding(parent) && parent!.DeclarationName == node && ((BindingElementNode)parent).DotDotDotToken is null)
            {
                if (entry.Kind == ReferenceEntryKind.SearchedLocalFoundProperty) return name + ": "u8 + text;
                if (entry.Kind == ReferenceEntryKind.SearchedPropertyFoundLocal) return text + ": "u8 + name;
                bool exports = parent?.Parent is ObjectLiteralExpressionNode { Parent: BinaryExpressionNode binary }
                    && (binary.Left is PropertyAccessExpressionNode { Expression: IdentifierNode receiver, Name: IdentifierNode member } && receiver.Text == "module"u8 && member.Text == "exports"u8
                        || binary.Left is ElementAccessExpressionNode { Expression: IdentifierNode receiver2 } access && receiver2.Text == "module"u8 && R.Text(access.ArgumentExpression!) == "exports"u8);
                return parent is ShorthandPropertyAssignmentNode && !exports ? text + ": "u8 + name : name + ": "u8 + text;
            }
            if (parent is ImportSpecifierNode { PropertyName: null })
            {
                var symbol = original.Parent is ExportSpecifierNode export ? await checker.GetExportSpecifierLocalTargetSymbolAsync(export, cancellation)
                    : await checker.GetSymbolAtLocationAsync(original, cancellation);
                return symbol?.Declarations.Contains(parent) == true ? name + " as "u8 + text : text;
            }
            if (parent is ExportSpecifierNode { PropertyName: null })
                return original == entry.Node || await checker.GetSymbolAtLocationAsync(original, cancellation) == await checker.GetSymbolAtLocationAsync(entry.Node!, cancellation)
                    ? name + " as "u8 + text : text + " as "u8 + name;
        }
        return entry.Kind != ReferenceEntryKind.Range && entry.Node is NumericLiteralNode { Parent: PropertyAccessExpressionNode or ElementAccessExpressionNode }
            ? quote + text + quote : text;
    }
}
