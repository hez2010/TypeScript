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
        private bool SupportedSource(string path, ParsedConfig project)
        {
            if (!fs.CaseSensitive)
                path = path.ToLowerInvariant();
            string extension = ModuleResolver.Extension(path);
            if (extension is ".ts" or ".tsx" or ".mts" or ".cts" or ".d.ts" or ".d.mts" or ".d.cts")
                return true;
            if (extension is ".js" or ".jsx" or ".mjs" or ".cjs")
                return project.Options.AllowJs ?? project.Options.CheckJs == true;
            if (extension == ".json")
                return project.Options.ResolveJsonModule ?? Resolver(project).ResolutionKind == "bundler"
                || project.Options.Module is ModuleOptionKind.Node20 or ModuleOptionKind.NodeNext;
            return config.ContentMappers.Any(m => m.Extensions.Any(e => path.EndsWith(e, StringComparison.Ordinal)));
        }

        private string RootPath(string path)
        {
            path = CompilerPath.Resolve(cwd, path);
            if (CompilerPath.Extension(path).Length != 0)
                return path;
            if (config.Options.AllowNonTsExtensions == true && resolutionFs.FileExists(path))
                return path;
            bool allowJs = config.Options.AllowJs ?? config.Options.CheckJs == true;
            foreach (string extension in allowJs ? new[] { ".ts", ".tsx", ".d.ts", ".js", ".jsx" } : [".ts", ".tsx", ".d.ts"])
                if (resolutionFs.FileExists(path + extension))
                    return path + extension;
            return path;
        }

        private string SupportedExtensionsText(ParsedConfig project)
        {
            bool allowJs = project.Options.AllowJs ?? project.Options.CheckJs == true;
            var extensions = new List<string>(allowJs
                ? [".ts", ".tsx", ".d.ts", ".js", ".jsx", ".cts", ".d.cts", ".cjs", ".mts", ".d.mts", ".mjs"]
                : [".ts", ".tsx", ".d.ts", ".cts", ".d.cts", ".mts", ".d.mts"]);
            foreach (var mapper in config.ContentMappers)
                foreach (string extension in mapper.Extensions)
                    if (!extensions.Contains(extension))
                        extensions.Add(extension);
            return string.Join(", ", extensions.Select(extension => "'" + extension + "'"));
        }

        private static string DefaultLibrary(CompilerOptions options) => options.Target switch
        {
            ScriptTarget.ESNext => "lib.esnext.full.d.ts",
            ScriptTarget.ES2015 => "lib.es6.d.ts",
            ScriptTarget.ES5 => "lib.d.ts",
            >= ScriptTarget.ES2016 and <= ScriptTarget.ES2025 => "lib.es" + options.EmitTargetYear + ".full.d.ts",
            _ => "lib.es2025.full.d.ts"
        };

        private async ValueTask<string> LibraryPath(string name)
        {
            if (!name.StartsWith("lib.", StringComparison.Ordinal))
            {
                var definition = OptionDefinitions.All.First(o => o.Name == "lib");
                int index = Array.IndexOf(definition.Values, name.ToLowerInvariant());
                if (index < 0)
                {
                    diagnostics.Add(new(Messages.Cannot_find_lib_definition_for_0, 0, 0, [name]));
                    return CompilerPath.Combine(libraryDirectory, "lib." + name + ".d.ts");
                }
                name = definition.ValueIdentities[index].Trim('"');
            }
            if (config.Options.LibReplacement != true || name == "lib.d.ts")
                return CompilerPath.Combine(libraryDirectory, name);
            string[] components = name[4..^5].Split('.');
            string package = "@typescript/lib-" + components[0];
            if (components.Length > 1)
                package += "/" + string.Join('-', components.Skip(1));
            var result = await Resolver(config).ResolveAsync(package,
                CompilerPath.Combine(
                    config.FileName.Length == 0 ? cwd : CompilerPath.DirectoryName(config.FileName),
                    "__lib_node_modules_lookup_" + name + "__.ts"),
                ReferenceResolutionMode.Require, cancellation: cancellation).ConfigureAwait(false);
            return result.IsResolved ? result.FileName : CompilerPath.Combine(libraryDirectory, name);
        }

        private static TextSlice ImportText(SyntaxNode node) => node switch
        {
            StringLiteralNode n => n.Text,
            NoSubstitutionTemplateLiteralNode n => n.Text,
            _ => ""
        };

        private static ModuleOptionKind Module(CompilerOptions options) => options.EmitModule;

        private static bool SyntaxAffectsResolution(CompilerOptions options) => options.EmitModuleResolutionKind is ModuleResolutionOptionKind.Node16 or ModuleResolutionOptionKind.NodeNext
            || options.ResolvePackageJsonExports != false || options.ResolvePackageJsonImports != false;

        internal static ReferenceResolutionMode DefaultMode(
            string path,
            CompilerOptions options,
            ReferenceResolutionMode implied,
            string packageType) =>
            SyntaxAffectsResolution(options) ? ImpliedMode(path, options, implied, packageType) : 0;

        private static ReferenceResolutionMode ImpliedMode(
            string path,
            CompilerOptions options,
            ReferenceResolutionMode implied,
            string packageType)
        {
            ModuleOptionKind module = Module(options);
            if (module is ModuleOptionKind.Node16 or ModuleOptionKind.Node18 or ModuleOptionKind.Node20 or ModuleOptionKind.NodeNext)
                return implied;
            if (implied == ReferenceResolutionMode.Require
                && (packageType == "commonjs"
                    || path.EndsWith(".cts", StringComparison.Ordinal)
                    || path.EndsWith(".cjs", StringComparison.Ordinal)))
                return ReferenceResolutionMode.Require;
            if (implied == ReferenceResolutionMode.Import
                && (packageType == "module"
                    || path.EndsWith(".mts", StringComparison.Ordinal)
                    || path.EndsWith(".mjs", StringComparison.Ordinal)))
                return ReferenceResolutionMode.Import;
            return 0;
        }

        internal static ReferenceResolutionMode UsageMode(
            SyntaxNode? node,
            string path,
            CompilerOptions options,
            ReferenceResolutionMode implied,
            string packageType)
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
                    if (attribute.Name is StringLiteralNode { Text.Span: "resolution-mode" } && attribute.Value is StringLiteralNode mode)
                        return mode.Text == "import"
                            ? ReferenceResolutionMode.Import
                            : mode.Text == "require" ? ReferenceResolutionMode.Require : 0;
            if (!SyntaxAffectsResolution(options))
                return 0;
            if (node?.Parent is ExternalModuleReferenceNode
                || node?.Parent is CallExpressionNode { Expression: IdentifierNode { Text.Span: "require" } })
                return ReferenceResolutionMode.Require;
            ModuleOptionKind module = Module(options);
            ReferenceResolutionMode format = ImpliedMode(path, options, implied, packageType);
            if (node?.Parent is CallExpressionNode call && (call.Expression?.Kind == K.ImportKeyword
                || call.Expression is MetaPropertyNode { KeywordToken: K.ImportKeyword, Name.Text.Span: "defer" }))
                return module is ModuleOptionKind.Node16 or ModuleOptionKind.Node18 or ModuleOptionKind.Node20 or ModuleOptionKind.NodeNext or ModuleOptionKind.Preserve ? ReferenceResolutionMode.Import
                    : format == ReferenceResolutionMode.Require || format == 0 && module is ModuleOptionKind.CommonJS or ModuleOptionKind.AMD or ModuleOptionKind.System or ModuleOptionKind.UMD
                        ? ReferenceResolutionMode.Require
                        : ReferenceResolutionMode.Import;
            return format != 0 ? format : module == ModuleOptionKind.CommonJS ? ReferenceResolutionMode.Require : ReferenceResolutionMode.Import;
        }
    }
}
