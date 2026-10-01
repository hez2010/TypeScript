using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;
using ModuleOptionKind = TypeScript.Compiler.Configuration.ModuleKind;
using ModuleResolutionOptionKind = TypeScript.Compiler.Configuration.ModuleResolutionKind;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Programs;

public sealed partial class CompilerProgram
{
    internal static Utf8String DefaultLibrary(CompilerOptions options) => options.Target switch
    {
        ScriptTarget.ESNext => Utf8Literals.LibEsnextFullDTs,
        ScriptTarget.ES2015 => Utf8Literals.LibEs6DTs,
        ScriptTarget.ES5 => Utf8Literals.LibDTs,
        >= ScriptTarget.ES2016 and <= ScriptTarget.ES2025 => Utf8String.Concat(Utf8Literals.LibEs, Utf8String.Format(options.EmitTargetYear), Utf8Literals.FullDTs),
        _ => Utf8Literals.LibEs2025FullDTs
    };

    internal ModuleOptionKind EmitModuleFormat(SourceFileNode source)
    {
        var file = GetFile(source.FileName) ?? throw new ArgumentException("Source belongs to another program", nameof(source));
        var options = ProjectReferences.Find(source.FileName)?.Project.Options ?? Configuration.Options;
        return Builder.ImpliedMode(source.FileName, options, file.ImpliedFormat, file.PackageType) switch
        {
            ReferenceResolutionMode.Require => ModuleOptionKind.CommonJS,
            ReferenceResolutionMode.Import => ModuleOptionKind.ESNext,
            _ => options.EmitModule
        };
    }

    internal ReferenceResolutionMode ResolutionModeForUsage(SourceFileNode source, SyntaxNode? specifier)
    {
        var file = GetFile(source.FileName) ?? throw new ArgumentException("Source belongs to another program", nameof(source));
        var options = ProjectReferences.Find(source.FileName)?.Project.Options ?? Configuration.Options;
        return specifier is null
            ? Builder.DefaultMode(source.FileName, options, file.ImpliedFormat, file.PackageType)
            : Builder.UsageMode(specifier, source.FileName, options, file.ImpliedFormat, file.PackageType);
    }

    private sealed partial class Builder
    {
        private bool SupportedSource(Utf8String path, ParsedConfig project)
        {
            if (!fs.CaseSensitive)
                path = path.ToLowerInvariant();
            Utf8String extension = ModuleResolver.Extension(path);
            if (extension == ".ts"u8 || extension == ".tsx"u8 || extension == ".mts"u8 || extension == ".cts"u8 || extension == ".d.ts"u8 || extension == ".d.mts"u8 || extension == ".d.cts"u8)
                return true;
            if (extension == ".js"u8 || extension == ".jsx"u8 || extension == ".mjs"u8 || extension == ".cjs"u8)
                return project.Options.AllowJs ?? project.Options.CheckJs == true;
            if (extension == Utf8Literals.Json)
                return project.Options.ResolveJsonModule ?? Resolver(project).ResolutionKind == Utf8Literals.Bundler
                || project.Options.Module is ModuleOptionKind.Node20 or ModuleOptionKind.NodeNext;
            return config.ContentMappers.Any(m => m.Extensions.Any(e => path.EndsWith(e, StringComparison.Ordinal)));
        }

        private Utf8String RootPath(Utf8String path)
        {
            path = CompilerPath.Resolve(cwd, path);
            if (CompilerPath.Extension(path).Length != 0)
                return path;
            if (config.Options.AllowNonTsExtensions == true && resolutionFs.FileExists(path))
                return path;
            bool allowJs = config.Options.AllowJs ?? config.Options.CheckJs == true;
            foreach (Utf8String extension in allowJs ? new Utf8String[] { Utf8Literals.Ts, Utf8Literals.Tsx, Utf8Literals.DTs, Utf8Literals.Js, Utf8Literals.Jsx } : [Utf8Literals.Ts, Utf8Literals.Tsx, Utf8Literals.DTs])
                if (resolutionFs.FileExists(path + extension))
                    return path + extension;
            return path;
        }

        private Utf8String SupportedExtensionsText(ParsedConfig project)
        {
            bool allowJs = project.Options.AllowJs ?? project.Options.CheckJs == true;
            var extensions = new List<Utf8String>(allowJs
                ? [Utf8Literals.Ts, Utf8Literals.Tsx, Utf8Literals.DTs, Utf8Literals.Js, Utf8Literals.Jsx, Utf8Literals.Cts, Utf8Literals.DCts, Utf8Literals.Cjs, Utf8Literals.Mts, Utf8Literals.DMts, Utf8Literals.Mjs]
                : [Utf8Literals.Ts, Utf8Literals.Tsx, Utf8Literals.DTs, Utf8Literals.Cts, Utf8Literals.DCts, Utf8Literals.Mts, Utf8Literals.DMts]);
            foreach (var mapper in config.ContentMappers)
                foreach (Utf8String extension in mapper.Extensions)
                    if (!extensions.Contains(extension))
                        extensions.Add(extension);
            return Utf8String.Join(", "u8, extensions.Select(extension => "'"u8 + extension + "'"u8));
        }

        private readonly Dictionary<Utf8String, (Utf8String Path, IReadOnlyList<Diagnostic> Trace)> resolvedLibraries = [];

        private async ValueTask<Utf8String> LibraryPath(Utf8String name)
        {
            if (!name.StartsWith("lib."u8, StringComparison.Ordinal))
            {
                var definition = OptionDefinitions.All.First(o => o.Name == Utf8Literals.Lib);
                int index = Array.IndexOf(definition.Values, name.ToLowerInvariant());
                if (index < 0)
                {
                    diagnostics.Add(new(Messages.Cannot_find_lib_definition_for_0, 0, 0, [name]));
                    return CompilerPath.Combine(libraryDirectory, Utf8Literals.LibPrefix + name + Utf8Literals.DTs);
                }
                name = definition.ValueIdentities[index].Trim((byte)'"');
            }
            if (skipModuleResolution || config.Options.LibReplacement != true || name == Utf8Literals.LibDTs)
                return CompilerPath.Combine(libraryDirectory, name);
            if (resolvedLibraries.TryGetValue(name, out var cached)) return cached.Path;
            Utf8String[] components = name[4..^5].Split((byte)'.');
            Utf8String package = Utf8Literals.TypescriptLib + components[0];
            if (components.Length > 1)
                package += Utf8Literals.Slash + Utf8String.Join((byte)'-', components.Skip(1));
            var result = await Resolver(config).ResolveAsync(package,
                CompilerPath.Combine(
                    config.FileName.Length == 0 ? cwd : CompilerPath.DirectoryName(config.FileName),
                    Utf8Literals.LibNodeModulesLookup + name + Utf8Literals.SyntheticTsFile),
                ReferenceResolutionMode.Require, cancellation: cancellation).ConfigureAwait(false);
            Utf8String resolved = result.IsResolved ? result.FileName : CompilerPath.Combine(libraryDirectory, name);
            resolvedLibraries[name] = (resolved, result.TraceMessages);
            return resolved;
        }

        private static Utf8String ImportText(SyntaxNode node) => node switch
        {
            StringLiteralNode n => n.Text,
            NoSubstitutionTemplateLiteralNode n => n.Text,
            _ => Utf8String.Empty
        };

        private static ModuleOptionKind Module(CompilerOptions options) => options.EmitModule;

        private static bool SyntaxAffectsResolution(CompilerOptions options) => options.EmitModuleResolutionKind is ModuleResolutionOptionKind.Node16 or ModuleResolutionOptionKind.NodeNext
            || options.ResolvePackageJsonExports != false || options.ResolvePackageJsonImports != false;

        internal static ReferenceResolutionMode DefaultMode(
            Utf8String path,
            CompilerOptions options,
            ReferenceResolutionMode implied,
            Utf8String packageType) =>
            SyntaxAffectsResolution(options) ? ImpliedMode(path, options, implied, packageType) : 0;

        internal static ReferenceResolutionMode ImpliedMode(
            Utf8String path,
            CompilerOptions options,
            ReferenceResolutionMode implied,
            Utf8String packageType)
        {
            ModuleOptionKind module = Module(options);
            if (module is ModuleOptionKind.Node16 or ModuleOptionKind.Node18 or ModuleOptionKind.Node20 or ModuleOptionKind.NodeNext)
                return implied;
            if (implied == ReferenceResolutionMode.Require
                && (packageType == Utf8Literals.Commonjs
                    || path.EndsWith(".cts"u8, StringComparison.Ordinal)
                    || path.EndsWith(".cjs"u8, StringComparison.Ordinal)))
                return ReferenceResolutionMode.Require;
            if (implied == ReferenceResolutionMode.Import
                && (packageType == Utf8Literals.Module
                    || path.EndsWith(".mts"u8, StringComparison.Ordinal)
                    || path.EndsWith(".mjs"u8, StringComparison.Ordinal)))
                return ReferenceResolutionMode.Import;
            return 0;
        }

        internal static ReferenceResolutionMode UsageMode(
            SyntaxNode? node,
            Utf8String path,
            CompilerOptions options,
            ReferenceResolutionMode implied,
            Utf8String packageType)
        {
            SyntaxNode? attributes = node?.Parent switch
            {
                ImportDeclarationNode { ImportClause.PhaseModifier: K.TypeKeyword } n => n.Attributes,
                ExportDeclarationNode { IsTypeOnly: true } n => n.Attributes,
                JSDocImportTagNode { ImportClause.PhaseModifier: K.TypeKeyword } n => n.Attributes,
                LiteralTypeNode { Parent: ImportTypeNode n } => n.Attributes,
                _ => null
            };
            if (attributes is not null)
                foreach (var attribute in attributes.DescendantsAndSelf().OfType<ImportAttributeNode>())
                    if (attribute.Name is StringLiteralNode { Text.Span: var matchedText } && matchedText.SequenceEqual("resolution-mode"u8) && attribute.Value is StringLiteralNode mode)
                        return mode.Text == Utf8Literals.ImportKeyword
                            ? ReferenceResolutionMode.Import
                            : mode.Text == Utf8Literals.RequireKeyword ? ReferenceResolutionMode.Require : 0;
            if (!SyntaxAffectsResolution(options))
                return 0;
            if (node?.Parent is ExternalModuleReferenceNode
                || node?.Parent is CallExpressionNode { Expression: IdentifierNode { Text.Span: var matchedText2 } } && matchedText2.SequenceEqual("require"u8))
                return ReferenceResolutionMode.Require;
            ModuleOptionKind module = Module(options);
            ReferenceResolutionMode format = ImpliedMode(path, options, implied, packageType);
            if (node?.Parent is CallExpressionNode call && (call.Expression?.Kind == K.ImportKeyword
                || call.Expression is MetaPropertyNode { KeywordToken: K.ImportKeyword, Name.Text.Span: var matchedText3 } && matchedText3.SequenceEqual("defer"u8)))
                return module is ModuleOptionKind.Node16 or ModuleOptionKind.Node18 or ModuleOptionKind.Node20 or ModuleOptionKind.NodeNext or ModuleOptionKind.Preserve ? ReferenceResolutionMode.Import
                    : format == ReferenceResolutionMode.Require || format == 0 && module is ModuleOptionKind.CommonJS or ModuleOptionKind.AMD or ModuleOptionKind.System or ModuleOptionKind.UMD
                        ? ReferenceResolutionMode.Require
                        : ReferenceResolutionMode.Import;
            return format != 0 ? format : module == ModuleOptionKind.CommonJS ? ReferenceResolutionMode.Require : ReferenceResolutionMode.Import;
        }
    }
}
