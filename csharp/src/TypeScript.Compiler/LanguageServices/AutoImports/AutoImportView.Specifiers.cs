using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.LanguageServices;

internal sealed partial class AutoImportView
{
    private FilePattern[]? fileExcludes;
    private GoRegex[]? specifierExcludes;

    private bool ExcludedFile(Utf8String path)
        => (fileExcludes ??= preferences.AutoImportFileExcludePatterns.Select(pattern => new FilePattern(pattern, program.UseCaseSensitiveFileNames, true)).ToArray())
            .Any(pattern => pattern.Matches(path));

    private bool ExcludedSpecifier(Utf8String specifier)
    {
        if (specifierExcludes is null)
        {
            List<GoRegex> patterns = [];
            foreach (var raw in preferences.AutoImportSpecifierExcludeRegexes)
                if (GoRegex.FromSpecifierPattern(raw, cancellation) is { } pattern) patterns.Add(pattern);
            specifierExcludes = [.. patterns];
        }
        return specifierExcludes.Any(pattern => pattern.IsMatch(specifier, cancellation));
    }

    private (Utf8String Specifier, ModuleSpecifierKind Kind) ModuleSpecifier(AutoImportExport export)
    {
        if (!CompilerPath.IsAbsolute(export.Id.Module) && !ImportSorter.Relative(export.Id.Module))
            return ExcludedSpecifier(export.Id.Module) ? default : (export.Id.Module, ModuleSpecifierKind.Ambient);
        if (!export.PackageName.IsEmpty && exportData?.Entrypoints.TryGetValue(export.Path, out var entrypoints) == true)
        {
            var mode = program.ResolutionModeForUsage(File, null);
            bool bundler = Options.EmitModuleResolutionKind == ModuleResolutionKind.Bundler;
            HashSet<Utf8String> conditions = [mode == ReferenceResolutionMode.Import || mode == 0 && bundler ? "import"u8 : "require"u8,
                .. Options.NoDtsResolution != true ? new Utf8String[] { "types"u8 } : [], .. !bundler ? new Utf8String[] { "node"u8 } : [], .. Options.CustomConditions ?? []];
            foreach (var entrypoint in entrypoints)
                if ((entrypoint.IncludeConditions is null || entrypoint.IncludeConditions.All(conditions.Contains))
                    && (entrypoint.ExcludeConditions is null || !entrypoint.ExcludeConditions.Any(conditions.Contains)))
                {
                    var specifier = entrypoint.Format(Options, File, mode, preferences.ImportModuleSpecifierEnding, cancellation);
                    if (!ExcludedSpecifier(specifier)) return (specifier, ModuleSpecifierKind.NodeModules);
                }
            return default;
        }
        if (export.PackageName.IsEmpty && cache?.TryGetSpecifier(Canonical(File.FileName), export.Path, preferences, out var cached) == true)
            return cached.IsEmpty ? default : (cached, ModuleSpecifierKind.Relative);
        var result = program.GetModuleSpecifiers(File, export.FileName, new(preferences.ImportModuleSpecifierPreference,
            preferences.ImportModuleSpecifierEnding, ExcludedSpecifier), cancellation: cancellation, forAutoImport: true);
        foreach (var specifier in result.Specifiers)
            if (!specifier.Contains("/node_modules/"u8))
            {
                cache?.StoreSpecifier(Canonical(File.FileName), export.Path, specifier, preferences);
                return (specifier, result.Kind);
            }
        cache?.StoreSpecifier(Canonical(File.FileName), export.Path, default, preferences);
        return default;
    }

    private bool ShouldUseRequire()
    {
        if (useRequire is { } value) return value;
        return (useRequire = Compute()).Value;

        bool Compute()
        {
            if (File.ScriptKind is not (ScriptKind.JS or ScriptKind.JSX)) return false;
            if (SyntaxChoice(File) is { } choice) return choice;
            var implied = program.ImpliedFormatForEmit(File);
            if (implied != 0) return implied == ReferenceResolutionMode.Require;
            if (!program.Configuration.FileName.IsEmpty) return Options.EmitModule < ModuleKind.ES2015;
            foreach (var other in program.SourceFiles)
                if (other.Syntax != File && other.Syntax.ScriptKind is ScriptKind.JS or ScriptKind.JSX && !program.IsFromExternalLibrary(other.Syntax)
                    && SyntaxChoice(other.Syntax) is { } match) return match;
            return true;
        }
        bool? SyntaxChoice(SourceFileNode file)
        {
            bool cjs = checker.Symbols.Binding(file)?.CommonJSModuleIndicator is not null;
            bool esm = file.ExternalModuleIndicator is not null;
            if (Options.EmitModuleDetectionKind == ModuleDetectionKind.Force)
                esm = file.ExternalModuleIndicator is not null && file.ExternalModuleIndicator != file || file.Imports.Any(import => import.Pos >= 0
                    && import.Parent is ImportDeclarationNode or ExportDeclarationNode or ExternalModuleReferenceNode);
            return cjs == esm ? null : cjs;
        }
    }

    internal int CompareRanking(ImportFix left, ImportFix right)
    {
        int result = left.Data.Kind.CompareTo(right.Data.Kind);
        if (result != 0) return result;
        if (preferences.ImportModuleSpecifierPreference == "non-relative"u8 || preferences.ImportModuleSpecifierPreference == "project-relative"u8)
        {
            result = (left.ModuleSpecifierKind == ModuleSpecifierKind.Relative).CompareTo(right.ModuleSpecifierKind == ModuleSpecifierKind.Relative);
            if (result != 0) return result;
        }
        if (left.ModuleSpecifierKind == ModuleSpecifierKind.Ambient && right.ModuleSpecifierKind == ModuleSpecifierKind.Ambient)
        {
            var preference = UriStyleNodeModules();
            bool a = left.Data.ModuleSpecifier.StartsWith("node:"u8), b = right.Data.ModuleSpecifier.StartsWith("node:"u8);
            if (preference is not null && a != b) return preference == true ? b.CompareTo(a) : a.CompareTo(b);
        }
        if (left.ModuleSpecifierKind == ModuleSpecifierKind.Relative && right.ModuleSpecifierKind == ModuleSpecifierKind.Relative)
        {
            result = ReexportsContainingFile(left).CompareTo(ReexportsContainingFile(right));
            if (result != 0) return result;
        }
        return left.Data.ModuleSpecifier.Span.Count((byte)'/').CompareTo(right.Data.ModuleSpecifier.Span.Count((byte)'/'));
    }

    internal int CompareSorting(ImportFix left, ImportFix right)
    {
        int result = CompareRanking(left, right);
        if (result != 0) return result;
        result = right.Data.ModuleSpecifier.StartsWith("./"u8).CompareTo(left.Data.ModuleSpecifier.StartsWith("./"u8));
        if (result != 0) return result;
        result = left.Data.ModuleSpecifier.CompareTo(right.Data.ModuleSpecifier);
        return result != 0 ? result : left.Data.ImportKind.CompareTo(right.Data.ImportKind);
    }

    private bool ReexportsContainingFile(ImportFix fix) => fix.IsReExport && CompilerPath.BaseName(fix.ModuleFileName).ToString()
        is "index.js" or "index.jsx" or "index.d.ts" or "index.ts" or "index.tsx"
        && File.FileName.StartsWith(CompilerPath.EnsureTrailingSeparator(CompilerPath.DirectoryName(fix.ModuleFileName)));

    private bool? UriStyleNodeModules()
    {
        foreach (var import in File.Imports)
            {
                var name = ModuleSpecifierPaths.ImportText(import);
                if (NodeCoreModules.Contains(name) && name != "node:quic"u8 && name != "node:sea"u8 && name != "node:sqlite"u8 && name != "node:test"u8 && name != "node:test/reporters"u8)
                    return name.StartsWith("node:"u8);
            }
        return null;
    }
}
