using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;
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
                return project.Options.Boolean("allowJs") ?? project.Options.Boolean("checkJs") == true;
            if (extension == ".json")
                return project.Options.Boolean("resolveJsonModule") ?? Resolver(project).ResolutionKind == "bundler"
                || project.Options.String("module") is "node20" or "nodenext";
            return config.ContentMappers.Any(m => m.Extensions.Any(e => path.EndsWith(e, StringComparison.Ordinal)));
        }

        private string RootPath(string path)
        {
            path = CompilerPath.Resolve(cwd, path);
            if (CompilerPath.Extension(path).Length != 0)
                return path;
            foreach (string extension in new[] { ".ts", ".tsx", ".d.ts" })
                if (resolutionFs.FileExists(path + extension))
                    return path + extension;
            return path;
        }

        private static string DefaultLibrary(CompilerOptions options) => options.String("target") switch
        {
            "esnext" => "lib.esnext.full.d.ts",
            "es6" or "es2015" => "lib.es6.d.ts",
            "es5" => "lib.d.ts",
            { } target => "lib." + target + ".full.d.ts",
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
            if (config.Options.Boolean("libReplacement") != true || name == "lib.d.ts")
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

        private static string ImportText(SyntaxNode node) => node switch
        {
            StringLiteralNode n => n.Text,
            NoSubstitutionTemplateLiteralNode n => n.Text,
            _ => ""
        };

        private static string ModuleKind(CompilerOptions options) => options.String("module") ?? options.String("target") switch
        {
            "esnext" => "esnext",
            "es5" => "commonjs",
            "es6" or "es2015" or "es2016" or "es2017" or "es2018" or "es2019" => "es2015",
            "es2020" or "es2021" => "es2020",
            _ => "es2022"
        };

        private static bool SyntaxAffectsResolution(CompilerOptions options) => options.String("moduleResolution") is "node16" or "nodenext"
            || options.String("moduleResolution") is not "bundler" && ModuleKind(options) is "node16" or "node18" or "node20" or "nodenext"
            || options.Boolean("resolvePackageJsonExports") != false || options.Boolean("resolvePackageJsonImports") != false;

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
            string module = ModuleKind(options);
            if (module is "node16" or "node18" or "node20" or "nodenext")
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
                    if (attribute.Name is StringLiteralNode { Text: "resolution-mode" } && attribute.Value is StringLiteralNode mode)
                        return mode.Text == "import"
                            ? ReferenceResolutionMode.Import
                            : mode.Text == "require" ? ReferenceResolutionMode.Require : 0;
            if (!SyntaxAffectsResolution(options))
                return 0;
            if (node?.Parent is ExternalModuleReferenceNode
                || node?.Parent is CallExpressionNode { Expression: IdentifierNode { Text: "require" } })
                return ReferenceResolutionMode.Require;
            string module = ModuleKind(options);
            ReferenceResolutionMode format = ImpliedMode(path, options, implied, packageType);
            if (node?.Parent is CallExpressionNode call && (call.Expression?.Kind == K.ImportKeyword
                || call.Expression is MetaPropertyNode { KeywordToken: K.ImportKeyword, Name.Text: "defer" }))
                return module is "node16" or "node18" or "node20" or "nodenext" or "preserve" ? ReferenceResolutionMode.Import
                    : format == ReferenceResolutionMode.Require || format == 0 && module is "commonjs" or "amd" or "system" or "umd"
                        ? ReferenceResolutionMode.Require
                        : ReferenceResolutionMode.Import;
            return format != 0 ? format : module == "commonjs" ? ReferenceResolutionMode.Require : ReferenceResolutionMode.Import;
        }
    }
}
