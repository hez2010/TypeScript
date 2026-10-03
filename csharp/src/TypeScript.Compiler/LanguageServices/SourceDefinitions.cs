using System.Runtime.CompilerServices;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial class LanguageServiceDocument
{
    private sealed record SourceDefinitionTargets(IReadOnlyList<SyntaxNode> Declarations, int? Start = null, int? End = null);

    private sealed class SourceDefinitionQuery(ProjectSnapshot project, ProjectRequest request, SourceFileNode file,
        DeclarationMaps maps, CancellationToken cancellation)
    {
        private readonly Dictionary<Utf8String, SourceFileNode?> parsed = [];
        private ModuleResolver? moduleResolver;
        private ModuleResolver Resolver
        {
            get
            {
                if (moduleResolver is not null) return moduleResolver;
                var program = project.Program!;
                var options = new CompilerOptions(); options.Merge(program.Configuration.Options);
                using var enabled = JsonDocument.Parse("true"); options.Set("noDtsResolution"u8, enabled.RootElement);
                return moduleResolver = new(program.FileSystem, options, program.CurrentDirectory, program.Configuration.FileName,
                    program.GlobalTypingsCache, program.Configuration.ContentMappers.SelectMany(mapper => mapper.Extensions));
            }
        }

        internal async ValueTask<SourceDefinitionTargets?> GetAsync(SyntaxNode node, int position)
        {
            var program = project.Program!;
            if (node is SourceFileNode)
            {
                if (await DefinitionReferenceAsync(program, file, position, cancellation) is not { } referenceName || program.GetFile(referenceName)?.Syntax is not { } referenceFile) return null;
                var reference = file.ReferencedFiles.Concat(file.TypeReferenceDirectives).Concat(file.LibReferenceDirectives).FirstOrDefault(reference => reference.Pos <= position && position <= reference.End);
                if (reference.FileName.IsEmpty) return null;
                if (!referenceFile.IsDeclarationFile) return new([Entry(referenceFile)], reference.Pos, reference.End);
                var implementation = await FromDeclarationFileAsync(referenceFile.FileName);
                return await ParseAsync(implementation) is { } source ? new([Entry(source)], reference.Pos, reference.End) : null;
            }
            var specifier = ContainingSpecifier(node);
            var resolved = specifier is null ? default : await ResolveAsync(Text(specifier), file.FileName, program.ResolutionModeForUsage(file, specifier));
            if (node == specifier) return await ParseAsync(resolved) is { } source ? new([Entry(source)]) : null;
            if (!resolved.IsEmpty && await SearchAsync(node, resolved, Names(node, null)) is { Count: > 0 } fast
                && (!QuerySyntax.PartOfType(node) && Ancestor(node, SemanticSyntax.TypeOnly) is null || fast.Any(Concrete))) return new(Unique(fast));

            IReadOnlyList<SyntaxNode> declarations; Utf8String imported = default;
            using (var lease = await project.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request, file).ConfigureAwait(false))
            {
                var checker = lease.Checker;
                declarations = await checker.GetDefinitionDeclarationsAsync(node, cancellation);
                bool property = node.Parent is PropertyAccessExpressionNode propertyAccess && propertyAccess.Name == node;
                if (declarations.Count == 0 && property && node.Parent is PropertyAccessExpressionNode parent
                    && await checker.Properties.PropertyAsync(await checker.GetTypeAtLocationAsync(parent.Expression!, cancellation), Text(node), cancellation: cancellation) is { } prop) declarations = prop.Declarations;
                if (await CalledDeclarationAsync(checker, node, cancellation) is { } called)
                    declarations = [.. declarations.Where(declaration => declaration is not IFunctionSignature), called];
                var resolveNode = property ? ((PropertyAccessExpressionNode)node.Parent!).Expression! : node;
                if (property)
                    while (resolveNode is PropertyAccessExpressionNode or ElementAccessExpressionNode)
                        resolveNode = resolveNode is PropertyAccessExpressionNode access ? access.Expression! : ((ElementAccessExpressionNode)resolveNode).Expression!;
                if (await checker.GetSymbolAtLocationAsync(resolveNode, cancellation) is { } symbol)
                    foreach (var declaration in symbol.Declarations)
                        if (declaration is ImportSpecifierNode or ImportClauseNode or NamespaceImportNode or ImportEqualsDeclarationNode
                            && ContainingSpecifier(declaration) is { } importedSpecifier) { imported = Text(importedSpecifier); break; }
            }
            if (resolved.IsEmpty && !imported.IsEmpty) resolved = await ResolveAsync(imported, file.FileName, Mode(file.FileName));
            if (declarations.Count == 0 && !resolved.IsEmpty && await SearchAsync(node, resolved, Names(node, null)) is { Count: > 0 } found) return new(Unique(found));
            List<SyntaxNode> mapped = [];
            foreach (var declaration in declarations)
            {
                cancellation.ThrowIfCancellationRequested();
                var source = SemanticSyntax.Source(declaration)!;
                var name = SyntaxLanguageService.Name(declaration) ?? declaration;
                int start = await SyntaxNavigation.GetStartAsync(name, source, cancellation: cancellation);
                if (name is StringLiteralNode or NoSubstitutionTemplateLiteralNode && name.End - start > 2) start++;
                if (maps.SourcePosition(source.FileName, start) is { } mappedPosition && await ParseAsync(mappedPosition.FileName) is { } mappedSource)
                {
                    var mappedNode = await SyntaxNavigation.GetTouchingPropertyNameAsync(mappedSource, mappedPosition.Offset, cancellation);
                    mapped.Add(Ancestor(mappedNode, QuerySyntax.Declaration) ?? Entry(mappedSource)); continue;
                }
                if (!CompilerPath.IsDeclarationFile(source.FileName)) { mapped.Add(declaration); continue; }
                var implementation = resolved.IsEmpty ? await FromDeclarationFileAsync(source.FileName) : resolved;
                mapped.AddRange(await SearchAsync(node, implementation, Names(node, declaration)));
            }
            if (mapped.Any(Concrete)) return new(Unique(mapped));
            if (specifier is not null && !resolved.IsEmpty && !declarations.Any(Concrete) && await ParseAsync(resolved) is { } fallback) return new([Entry(fallback)]);
            return null;
        }

        private async ValueTask<Utf8String> FromDeclarationFileAsync(Utf8String name)
        {
            Utf8String extension = name.EndsWith(".d.mts"u8) ? ".mjs"u8 : name.EndsWith(".d.cts"u8) ? ".cjs"u8 : name.EndsWith(".ts"u8) ? ".js"u8 : default;
            if (!extension.IsEmpty)
            {
                var candidate = RemoveExtension(name) + extension;
                if (project.Program!.FileSystem.FileExists(candidate)) return candidate;
            }
            int nodeModules = name.IndexOf("/node_modules/"u8);
            if (nodeModules < 0 || name.LastIndexOf("/node_modules/"u8) != nodeModules) return default;
            var remaining = name[(nodeModules + 14)..];
            int slash = remaining.IndexOf((byte)'/');
            if (slash < 0) return default;
            if (remaining.StartsWith("@"u8)) { int next = remaining[(slash + 1)..].IndexOf((byte)'/'); if (next < 0) return default; slash += next + 1; }
            var package = remaining[..slash]; var subpath = remaining[(slash + 1)..];
            if (package.StartsWith("@types/"u8)) package = package[7..];
            if (package.IndexOf("__"u8) is >= 0 and var scoped) package = "@"u8 + package[..scoped] + "/"u8 + package[(scoped + 2)..];
            var mode = Mode(name);
            if (!subpath.IsEmpty && await ResolveAsync(package + "/"u8 + RemoveExtension(subpath), file.FileName, mode) is { IsEmpty: false } deep) return deep;
            return await ResolveAsync(package, file.FileName, mode);
        }

        private async ValueTask<Utf8String> ResolveAsync(Utf8String module, Utf8String from, ReferenceResolutionMode mode)
        {
            if (module.IsEmpty) return default;
            foreach (var candidate in new[] { mode, ReferenceResolutionMode.Import, ReferenceResolutionMode.Require }.Distinct())
                if (await Resolver.ResolveAsync(module, from, candidate, cancellation: cancellation) is { IsResolved: true } resolved
                    && !CompilerPath.IsDeclarationFile(resolved.FileName)) return resolved.FileName;
            return default;
        }
        private ReferenceResolutionMode Mode(Utf8String name) => CompilerPath.Extension(name) is var extension
            && (extension == ".mjs"u8 || extension == ".mts"u8) ? ReferenceResolutionMode.Import
            : extension == ".cjs"u8 || extension == ".cts"u8 ? ReferenceResolutionMode.Require
            : extension == ".ts"u8 || extension == ".tsx"u8 || extension == ".js"u8 || extension == ".jsx"u8
                ? Resolver.Packages.Scope(CompilerPath.DirectoryName(name))?.Type == "module"u8 ? ReferenceResolutionMode.Import : ReferenceResolutionMode.Require
                : ReferenceResolutionMode.Unspecified;
        private async ValueTask<SourceFileNode?> ParseAsync(Utf8String name)
        {
            if (name.IsEmpty) return null;
            if (project.Program!.GetFile(name)?.Syntax is { } existing) return existing;
            if (parsed.TryGetValue(name, out var cached)) return cached;
            SourceFileNode? source = null;
            if (maps.Read(name) is { } text)
            {
                source = await Parser.ParseSourceFileAsync(new(name), text, cancellation);
                await Binder.BindAsync(source, cancellation);
            }
            parsed[name] = source;
            return source;
        }
        private async ValueTask<IReadOnlyList<SyntaxNode>> SearchAsync(SyntaxNode node, Utf8String name, IReadOnlyList<Utf8String> names)
        {
            if (await ParseAsync(name) is not { } source) return [];
            if (DefaultImport(node))
            {
                var defaults = await FindAsync(name, ["default"u8], []);
                return defaults.Count != 0 ? Preferred(node, defaults) : [Entry(source)];
            }
            return Preferred(node, await FindAsync(name, names, []));
        }
        private async ValueTask<IReadOnlyList<SyntaxNode>> FindAsync(Utf8String fileName, IReadOnlyList<Utf8String> names, HashSet<Utf8String> seen)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            if (fileName.IsEmpty || names.Count == 0 || !seen.Add(fileName) || await ParseAsync(fileName) is not { } source) return [];
            var declarations = FindNamed(source, names);
            if (declarations.Any(Concrete)) return declarations;
            List<SyntaxNode> forwarded = [];
            HashSet<Utf8String> files = [];
            foreach (var import in source.Imports)
            {
                var implementation = await ResolveAsync(Text(import), source.FileName, Mode(source.FileName));
                if (!implementation.IsEmpty && files.Add(implementation)) forwarded.AddRange(await FindAsync(implementation, names, seen));
            }
            return forwarded.Count == 0 ? declarations : Unique(forwarded.Any(Concrete) ? forwarded : [.. declarations, .. forwarded]);
        }

        private IReadOnlyList<SyntaxNode> FindNamed(SourceFileNode source, IReadOnlyList<Utf8String> names)
        {
            HashSet<Utf8String> wanted = new(names.Where(name => !name.IsEmpty));
            bool defaults = wanted.Remove("default"u8);
            List<(SyntaxNode Node, int Depth)> matches = [];
            int minimum = int.MaxValue;
            foreach (var node in source.DescendantsAndSelf())
            {
                cancellation.ThrowIfCancellationRequested();
                if (!(wanted.Contains(Text(SyntaxLanguageService.Name(node))) || defaults && (node is ExportAssignmentNode
                    || node is FunctionDeclarationNode or ClassDeclarationNode && SemanticSyntax.HasModifier(node, K.ExportKeyword) && SemanticSyntax.HasModifier(node, K.DefaultKeyword)))) continue;
                int depth = 1;
                for (var parent = node.Parent; parent is not null; parent = parent.Parent)
                    if (parent is SourceFileNode or MethodDeclarationNode or MethodSignatureDeclarationNode or FunctionDeclarationNode or FunctionExpressionNode
                        or GetAccessorDeclarationNode or SetAccessorDeclarationNode or ClassDeclarationNode or InterfaceDeclarationNode or EnumDeclarationNode or ModuleDeclarationNode) depth++;
                minimum = Math.Min(minimum, depth); matches.Add((node, depth));
            }
            return Unique(matches.Where(match => match.Depth == minimum).Select(match => match.Node));
        }
        private static IReadOnlyList<SyntaxNode> Preferred(SyntaxNode original, IReadOnlyList<SyntaxNode> declarations)
        {
            if (declarations.Count <= 1) return declarations;
            if (original.Parent is PropertyAccessExpressionNode access && access.Name == original)
            {
                var properties = declarations.Where(node => node is PropertyAssignmentNode or ShorthandPropertyAssignmentNode or PropertyDeclarationNode
                    or PropertySignatureDeclarationNode or MethodDeclarationNode or MethodSignatureDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode or EnumMemberNode).ToArray();
                if (properties.Length != 0) return properties;
            }
            var concrete = declarations.Where(Concrete).ToArray();
            return concrete.Length == 0 ? declarations : concrete;
        }
        private static bool Concrete(SyntaxNode node) => QuerySyntax.Declaration(node) && node is not (ExportAssignmentNode or ParameterDeclarationNode or TypeParameterDeclarationNode
            or BindingElementNode or ImportClauseNode or ImportSpecifierNode or NamespaceImportNode or ExportSpecifierNode or PropertyAccessExpressionNode or ElementAccessExpressionNode)
            && !(node is BinaryExpressionNode or CallExpressionNode && SyntaxLanguageService.IsAssignmentDeclaration(node));
        private static IReadOnlyList<SyntaxNode> Unique(IEnumerable<SyntaxNode> nodes) => nodes.DistinctBy(node => (SemanticSyntax.Source(node)!.FileName, node.Pos, node.End)).ToArray();
        private static SyntaxNode Entry(SourceFileNode source) => source.Statements?.FirstOrDefault() ?? (SyntaxNode)source;
        private static bool DefaultImport(SyntaxNode node) => node.Parent is ImportClauseNode clause && clause.Name == node && clause.Parent is ImportDeclarationNode;
        private static Utf8String Text(SyntaxNode? node) => node switch
        {
            IdentifierNode n => n.Text, PrivateIdentifierNode n => n.Text, StringLiteralNode n => n.Text,
            NoSubstitutionTemplateLiteralNode n => n.Text, NumericLiteralNode n => n.Text,
            ComputedPropertyNameNode computed => Text(computed.Expression), _ => default,
        };
        private static IReadOnlyList<Utf8String> Names(SyntaxNode original, SyntaxNode? declaration)
        {
            List<Utf8String> names = [];
            if (declaration is not null)
            {
                if (Text(SyntaxLanguageService.Name(declaration)) is { IsEmpty: false } name) names.Add(name);
                if (declaration is ExportAssignmentNode || declaration is FunctionDeclarationNode or ClassDeclarationNode
                    && SemanticSyntax.HasModifier(declaration, K.ExportKeyword) && SemanticSyntax.HasModifier(declaration, K.DefaultKeyword)) names.Add("default"u8);
                if (declaration is ImportSpecifierNode import && import.PropertyName is { } imported) names.Add(Text(imported));
                if (declaration is ExportSpecifierNode export && export.PropertyName is { } exported) names.Add(Text(exported));
            }
            if (original is IdentifierNode or PrivateIdentifierNode) names.Add(Text(original));
            if (DefaultImport(original)) names.Add("default"u8);
            if (original.Parent is ImportSpecifierNode parentImport && parentImport.PropertyName is { } importName) names.Add(Text(importName));
            if (original.Parent is ExportSpecifierNode parentExport && parentExport.PropertyName is { } exportName) names.Add(Text(exportName));
            return names;
        }
        private static SyntaxNode? ContainingSpecifier(SyntaxNode node)
        {
            for (var current = node; current is not null; current = current.Parent)
            {
                var module = current switch
                {
                    ImportDeclarationNode import => import.ModuleSpecifier, ExportDeclarationNode export => export.ModuleSpecifier,
                    ImportEqualsDeclarationNode { ModuleReference: ExternalModuleReferenceNode reference } => reference.Expression,
                    CallExpressionNode { Arguments.Count: > 0 } call when SemanticSyntax.RequireCall(call) || call.Expression?.Kind == K.ImportKeyword => call.Arguments[0], _ => null,
                };
                if (module is StringLiteralNode or NoSubstitutionTemplateLiteralNode) return module;
            }
            return null;
        }
        private static Utf8String RemoveExtension(Utf8String name)
        {
            foreach (var extension in new Utf8String[] { ".d.ts"u8, ".d.mts"u8, ".d.cts"u8, ".tsx"u8, ".ts"u8, ".mts"u8, ".cts"u8, ".js"u8, ".jsx"u8, ".mjs"u8, ".cjs"u8, ".json"u8 })
                if (name.EndsWith(extension)) return name[..^extension.Length];
            return name;
        }
    }
}
