using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial class LanguageServiceDocument
{
    private sealed partial class CompletionQuery
    {
        private sealed record PathCompletion(Utf8String Name, int Kind, Utf8String Extension = default);
        private sealed record PathOptions(Utf8String[] Extensions, ReferenceResolutionMode Mode, bool FileName = false);
        private IFileSystem FileSystem => program.FileSystem;
        private StringComparison PathComparison => FileSystem.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        private PackageJsonCache? pathPackages;
        private PackageJsonCache PathPackages => pathPackages ??= new(FileSystem, program.CurrentDirectory);
        private bool NodeModulesResolution => options.EmitModuleResolutionKind is ModuleResolutionKind.Node16 or ModuleResolutionKind.NodeNext or ModuleResolutionKind.Bundler;

        private async ValueTask<CompletionList?> ModulePathCompletionsAsync()
        {
            if (await ReferencePathCompletionsAsync() is { } reference) return reference;
            if (previous is not (StringLiteralNode or NoSubstitutionTemplateLiteralNode) || position <= await StartAsync(previous)
                || position > previous.End || position == previous.End && !UnterminatedString(previous)) return null;
            var parent = SkipParentheses(previous.Parent);
            bool module = parent is ImportDeclarationNode or ExportDeclarationNode or JSDocImportTagNode or ExternalModuleReferenceNode
                || parent is LiteralTypeNode { Parent: ImportTypeNode }
                || parent is CallExpressionNode call && (call.Expression?.Kind == K.ImportKeyword
                    || call.Expression is IdentifierNode { Text.Span: var text } && text.SequenceEqual("require"u8)
                        && call.Arguments is { Count: > 0 } arguments && arguments[0] == previous);
            if (!module) return null;
            var fragment = CompilerPath.NormalizeSlashes(PropertyName(previous));
            var pathOptions = CompletionPathOptions(program.ResolutionModeForUsage(File, previous));
            var directory = CompilerPath.DirectoryName(File.FileName);
            List<PathCompletion> entries;
            if (fragment.StartsWith("./"u8) || fragment.StartsWith("../"u8) || options.Paths is null or { Count: 0 } && CompilerPath.IsAbsolute(fragment))
            {
                entries = [];
                foreach (var root in CompletionRootDirectories(directory))
                {
                    Dictionary<Utf8String, PathCompletion> paths = [];
                    DirectoryPaths(fragment, root, pathOptions, true, File.FileName, paths);
                    entries.AddRange(paths.Values);
                }
                entries = entries.Distinct().ToList();
            }
            else entries = NonRelativePaths(fragment, directory, pathOptions);
            return await PathItemsAsync(entries, PropertyName(previous), await StartAsync(previous) + 1);
        }

        private async ValueTask<CompletionList?> ReferencePathCompletionsAsync()
        {
            if (await SyntaxNavigation.GetEnclosingCommentAsync(File, position, previous, cancellation) is not { } enclosing
                || !File.Source.Text[enclosing.Pos..enclosing.End].StartsWith("///"u8)) return null;
            var token = await SyntaxNavigation.GetTokenAtPositionAsync(File, position, cancellation);
            var comment = SyntaxPrinter.CommentRanges(File.Source.Text, token.Pos, false).FirstOrDefault(range => range.Pos <= position && position <= range.End);
            if (comment.End == 0 || !ReferenceFragment(File.Source.Text[comment.Pos..position], out var fragment, out bool types)) return null;
            var directory = CompilerPath.DirectoryName(File.FileName);
            var pathOptions = CompletionPathOptions(0, !types);
            Dictionary<Utf8String, PathCompletion> result = [];
            if (types) TypingPaths(directory, FragmentDirectory(fragment), pathOptions, result);
            else DirectoryPaths(fragment, directory, pathOptions, true, File.FileName, result);
            return await PathItemsAsync(result.Values.ToArray(), fragment, position - fragment.Length);
        }

        private static bool ReferenceFragment(Utf8String text, out Utf8String fragment, out bool types)
        {
            fragment = default; types = false;
            if (!text.StartsWith("///"u8)) return false;
            text = Trim(text[3..]);
            if (!text.StartsWith("<reference"u8)) return false;
            text = text[10..];
            if (text.IsEmpty || !WhiteSpace(Wtf8.Decode(text.Span, out _))) return false;
            text = Trim(text);
            if (text.StartsWith("path"u8)) text = text[4..];
            else if (text.StartsWith("types"u8)) { text = text[5..]; types = true; }
            else return false;
            text = Trim(text);
            if (!text.StartsWith("="u8)) return false;
            text = Trim(text[1..]);
            if (text.IsEmpty || text[0] is not ((byte)'\'' or (byte)'"')) return false;
            fragment = text[1..];
            return !fragment.Contains("'"u8) && !fragment.Contains("\""u8);

            static bool WhiteSpace(int point) => TokenFacts.IsWhiteSpace(point) || TokenFacts.IsLineBreak(point);
            static Utf8String Trim(Utf8String value)
            {
                int index = 0;
                while (index < value.Length && WhiteSpace(Wtf8.Decode(value.Span[index..], out int width))) index += width;
                return value[index..];
            }
        }

        private PathOptions CompletionPathOptions(ReferenceResolutionMode mode, bool fileName = false)
        {
            List<Utf8String> extensions = [];
            if (!fileName)
                foreach (var name in AmbientModuleNames())
                    if (name.StartsWith("*."u8) && !name.Contains("/"u8)) extensions.Add(name[1..]);
            extensions.AddRange([".ts"u8, ".tsx"u8, ".d.ts"u8]);
            if (options.AllowJs ?? options.CheckJs == true) extensions.AddRange([".js"u8, ".jsx"u8]);
            extensions.AddRange([".cts"u8, ".d.cts"u8]);
            if (options.AllowJs ?? options.CheckJs == true) extensions.Add(".cjs"u8);
            extensions.AddRange([".mts"u8, ".d.mts"u8]);
            if (options.AllowJs ?? options.CheckJs == true) extensions.Add(".mjs"u8);
            extensions.AddRange(program.Configuration.ContentMappers.SelectMany(mapper => mapper.Extensions));
            if (NodeModulesResolution && (options.ResolveJsonModule ?? options.EmitModuleResolutionKind == ModuleResolutionKind.Bundler)) extensions.Add(".json"u8);
            return new(extensions.Distinct().ToArray(), mode, fileName);
        }

        private IEnumerable<Utf8String> AmbientModuleNames()
        {
            foreach (var symbol in checker.Symbols.Globals.Where(pair => pair.Key.StartsWith("\""u8)).Select(pair => pair.Value)
                .Concat(checker.Symbols.PatternModules.Select(module => checker.Symbols.Merger.GetMergedSymbol(module.Symbol)!)).Distinct())
            {
                cancellation.ThrowIfCancellationRequested();
                var declaration = symbol.Declarations.OfType<ModuleDeclarationNode>().FirstOrDefault(node => node.Name is StringLiteralNode name
                    && !SemanticSyntax.Source(node)!.ModuleAugmentations.Contains(name));
                yield return declaration?.Name is StringLiteralNode literal ? literal.Text : symbol.Name.Length >= 2 ? symbol.Name[1..^1] : symbol.Name;
            }
        }

        private IEnumerable<Utf8String> CompletionRootDirectories(Utf8String directory)
        {
            if (options.RootDirs is not { Length: > 0 } roots) return [directory];
            var basePath = options.Project ?? program.CurrentDirectory;
            var normalized = roots.Select(root => CompilerPath.EnsureTrailingSeparator(CompilerPath.Resolve(basePath, root))).ToArray();
            var rootDirectory = normalized.FirstOrDefault(root => CompilerPath.Contains(root, directory, FileSystem.CaseSensitive));
            var relative = rootDirectory.IsEmpty || rootDirectory.Length > directory.Length ? Utf8String.Empty : directory[rootDirectory.Length..];
            return normalized.Select(root => CompilerPath.Normalize(CompilerPath.Combine(root, relative))).Append(directory).Distinct();
        }

        private List<PathCompletion> NonRelativePaths(Utf8String fragment, Utf8String directory, PathOptions pathOptions)
        {
            Dictionary<Utf8String, PathCompletion> result = [];
            if (options.Paths is { Count: > 0 } mappings)
                AddPathMappings(result, fragment, options.PathsBasePath ?? program.CurrentDirectory, pathOptions, mappings);
            var fragmentDirectory = FragmentDirectory(fragment);
            foreach (var name in AmbientModuleNames())
                if (name.StartsWith(fragment) && !name.Contains("*"u8))
                    AddPath(result, new(fragmentDirectory.IsEmpty ? name : name[CompilerPath.EnsureTrailingSeparator(fragmentDirectory).Length..], 2));
            TypingPaths(directory, fragmentDirectory, pathOptions, result);
            if (!NodeModulesResolution) return result.Values.ToList();
            bool foundGlobal = false;
            if (fragmentDirectory.IsEmpty)
                foreach (var ancestor in PathAncestors(directory))
                    if (PathPackages.Get(ancestor).Contents is { } package)
                        foreach (var (name, _, _) in package.Dependencies())
                            if (!name.StartsWith("@types/"u8) && !result.ContainsKey(name)) { AddPath(result, new(name, 2)); foundGlobal = true; }
            bool seenPackage = false;
            if (!foundGlobal)
                foreach (var ancestor in PathAncestors(directory))
                {
                    if (!fragmentDirectory.IsEmpty && options.ResolvePackageJsonExports != false)
                    {
                        var components = fragment.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Utf8String.FromString).ToArray();
                        int count = components.Length > 0 && components[0].StartsWith("@"u8) ? 2 : 1;
                        if (components.Length >= count)
                        {
                            var packageName = Utf8String.Join("/"u8, components.Take(count));
                            if (options.ResolvePackageJsonImports != false && packageName.StartsWith("#"u8)) { Imports(ancestor); continue; }
                            var packageDirectory = CompilerPath.Combine(ancestor, "node_modules"u8, packageName);
                            if (PathPackages.Get(packageDirectory).Contents is { } package)
                            {
                                var subpath = Utf8String.Join("/"u8, components.Skip(count));
                                if (components.Length > count && CompilerPath.HasTrailingSeparator(fragment)) subpath += "/"u8;
                                if (PackageMappedPaths(package.Get("exports"u8), subpath, packageDirectory, pathOptions, result, true)) continue;
                            }
                        }
                    }
                    DirectoryPaths(fragment, CompilerPath.Combine(ancestor, "node_modules"u8), pathOptions, false, default, result);
                    Imports(ancestor);
                }
            return result.Values.ToList();

            void Imports(Utf8String ancestor)
            {
                if (options.ResolvePackageJsonImports == false || seenPackage || PathPackages.Get(ancestor).Contents is not { } package) return;
                seenPackage = true;
                PackageMappedPaths(package.Get("imports"u8), fragment, ancestor, pathOptions, result, false);
            }
        }

        private void TypingPaths(Utf8String directory, Utf8String fragmentDirectory, PathOptions pathOptions, Dictionary<Utf8String, PathCompletion> result)
        {
            HashSet<Utf8String> seen = [];
            var defaults = PackageJsonCache.Ancestors(program.Configuration.FileName.IsEmpty ? program.CurrentDirectory : CompilerPath.DirectoryName(program.Configuration.FileName))
                .Select(ancestor => CompilerPath.Combine(ancestor, "node_modules/@types"u8));
            var roots = (options.TypeRoots ?? defaults.ToArray()).Concat(PathAncestors(directory).Select(ancestor => CompilerPath.Combine(ancestor, "node_modules/@types"u8)));
            foreach (var root in roots)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!FileSystem.DirectoryExists(root)) continue;
                foreach (var typeDirectory in FileSystem.GetAccessibleEntries(root).Directories)
                {
                    int separator = typeDirectory.IndexOf("__"u8, StringComparison.Ordinal);
                    var name = separator < 0 ? typeDirectory : "@"u8 + typeDirectory[..separator] + "/"u8 + typeDirectory[(separator + 2)..];
                    if (options.Types is { Length: > 0 } types && !types.Contains(name)) continue;
                    if (fragmentDirectory.IsEmpty) { if (seen.Add(name)) AddPath(result, new(name, 2)); }
                    else if (TrimDirectoryPrefix(fragmentDirectory, name) is { } remaining)
                        DirectoryPaths(remaining, CompilerPath.Combine(root, typeDirectory), pathOptions, false, default, result);
                }
            }
        }

        private void DirectoryPaths(Utf8String fragment, Utf8String directory, PathOptions pathOptions, bool relative, Utf8String exclude,
            Dictionary<Utf8String, PathCompletion> result)
        {
            cancellation.ThrowIfCancellationRequested();
            fragment = CompilerPath.NormalizeSlashes(fragment);
            if (!CompilerPath.HasTrailingSeparator(fragment)) fragment = CompilerPath.DirectoryName(fragment);
            var root = CompilerPath.Resolve(directory, fragment.IsEmpty ? (Utf8String)"."u8 : fragment);
            if (!relative && PathPackages.Scope(root) is { } package && package.VersionPaths(CompletionCompilerVersion) is { ValueKind: System.Text.Json.JsonValueKind.Object } versions)
            {
                var remaining = TrimDirectoryPrefix(CompilerPath.EnsureTrailingSeparator(root), package.Directory);
                if (remaining is not null && AddPathMappings(result, remaining.Value, package.Directory, pathOptions, JsonPathMappings(versions))) return;
            }
            if (!FileSystem.DirectoryExists(root)) return;
            foreach (var file in FileMatcher.ReadDirectory(FileSystem, root, program.CurrentDirectory, pathOptions.Extensions, includes: ["./*"u8], cancellation: cancellation))
            {
                if (CompilerPath.Resolve(program.CurrentDirectory, file).Equals(exclude, PathComparison)) continue;
                var (name, extension) = PathFileName(CompilerPath.BaseName(file), pathOptions);
                AddPath(result, new(name, 1, extension));
            }
            foreach (var child in FileSystem.GetAccessibleEntries(root).Directories)
                if (child != "@types"u8) AddPath(result, new(child, 0));
        }

        private (Utf8String Name, Utf8String Extension) PathFileName(Utf8String name, PathOptions pathOptions, bool wildcard = false)
        {
            var nonJavaScript = ModuleSpecifierPaths.RealNonJavaScriptFileName(name);
            if (!nonJavaScript.IsEmpty) return (nonJavaScript, CompilerPath.Extension(nonJavaScript));
            var extension = ModuleResolver.Extension(name);
            if (pathOptions.FileName) return (name, extension);
            var endings = ModuleSpecifierPaths.AllowedEndings(options, File, program.ResolutionModeForUsage(File, null), pathOptions.Mode, preferences.ImportModuleSpecifierEnding, cancellation: cancellation);
            if (wildcard) endings = endings.Where(ending => ending is not (ModuleSpecifierEnding.Minimal or ModuleSpecifierEnding.Index)).ToArray();
            if (endings.Length > 0 && endings[0] == ModuleSpecifierEnding.TypeScript
                && (extension == ".ts"u8 || extension == ".tsx"u8 || extension == ".mts"u8 || extension == ".cts"u8)) return (name, extension);
            if (!wildcard && endings.Length > 0 && endings[0] is ModuleSpecifierEnding.Minimal or ModuleSpecifierEnding.Index
                && (extension == ".js"u8 || extension == ".jsx"u8 || extension == ".ts"u8 || extension == ".tsx"u8 || extension == ".d.ts"u8)) return (name[..^extension.Length], extension);
            if (extension == ".ts"u8 || extension == ".tsx"u8 || extension == ".d.ts"u8 || extension == ".js"u8 || extension == ".jsx"u8 || extension == ".json"u8
                || extension == ".mts"u8 || extension == ".d.mts"u8 || extension == ".mjs"u8 || extension == ".cts"u8 || extension == ".d.cts"u8 || extension == ".cjs"u8)
            {
                var output = ModuleSpecifierPaths.JavaScriptFileExtension(name, options);
                return (name[..^extension.Length] + output, output);
            }
            return (name, CompilerPath.Extension(name));
        }

        private async ValueTask<CompletionList?> PathItemsAsync(IReadOnlyList<PathCompletion> paths, Utf8String fragment, int start)
        {
            int offset = Math.Max(fragment.LastIndexOf((byte)'/'), fragment.LastIndexOf((byte)'\\')) + 1;
            DocumentRange? replacement = null;
            if (offset != fragment.Length)
            {
                var mapped = projection.ToRange(start + offset, start + fragment.Length);
                if (mapped.Fidelity != MappingFidelity.Exact) return null;
                replacement = mapped.Range;
            }
            List<CompletionItem> items = [];
            foreach (var path in paths)
            {
                var filter = FilterText(default, path.Name);
                items.Add(new(path.Name, path.Kind switch { 0 => 19, 1 => 17, _ => 9 }, Detail: path.Name.EndsWith(path.Extension) ? path.Name : path.Name + path.Extension,
                    SortText: "11"u8, TextEdit: replacement is null ? null : new(path.Name, replacement.Value), FilterText: filter.IsEmpty ? null : filter));
            }
            return await ApplyDefaultsAsync(items, [], null);
        }

        private IEnumerable<Utf8String> PathAncestors(Utf8String directory)
        {
            foreach (var ancestor in PackageJsonCache.Ancestors(directory))
            {
                cancellation.ThrowIfCancellationRequested();
                yield return ancestor;
                if (!program.GlobalTypingsCache.IsEmpty && ancestor.Equals(program.GlobalTypingsCache, PathComparison)) yield break;
            }
        }
        private static Utf8String FragmentDirectory(Utf8String fragment) => fragment.Contains("/"u8)
            ? CompilerPath.HasTrailingSeparator(fragment) ? fragment : CompilerPath.DirectoryName(fragment) : default;
        private Utf8String? TrimDirectoryPrefix(Utf8String path, Utf8String prefix)
            => path.Equals(prefix, PathComparison) ? Utf8String.Empty : path.StartsWith(CompilerPath.EnsureTrailingSeparator(prefix), PathComparison)
                ? path[(prefix.Length + (CompilerPath.HasTrailingSeparator(prefix) ? 0 : 1))..] : null;
        private static void AddPath(Dictionary<Utf8String, PathCompletion> paths, PathCompletion entry)
        { if (!paths.TryGetValue(entry.Name, out var previous) || previous.Kind < entry.Kind) paths[entry.Name] = entry; }
    }
}
