using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly Dictionary<(Type Type, bool DefaultOnly), Type> syntheticModuleTypes = [];

    private ReferenceResolutionMode ModuleUsageMode(SyntaxNode specifier)
    {
        var file = SemanticSyntax.Source(specifier);
        if (file is not null
            && program.Symbols.Program.GetFile(file.FileName)!.Resolutions.FirstOrDefault(r => r.Node == specifier) is { Mode: not ReferenceResolutionMode.Unspecified } reference)
            return reference.Mode;
        if (specifier.Parent is ExternalModuleReferenceNode || SemanticSyntax.RequireCall(specifier.Parent))
            return ReferenceResolutionMode.Require;
        int mode = EmitModuleKind(specifier);
        if (specifier.Parent is CallExpressionNode { Expression.Kind: SyntaxKind.ImportKeyword })
            return ModuleKind < 100 && mode is 1 or 2 or 3 or 4 ? ReferenceResolutionMode.Require : ReferenceResolutionMode.Import;
        return mode == 1
            ? ReferenceResolutionMode.Require
            : mode is >= 5 and <= 99 or 200 ? ReferenceResolutionMode.Import : ReferenceResolutionMode.Unspecified;
    }

    private ReferenceResolutionMode ModuleTargetMode(SourceFileNode file)
    {
        var metadata = program.Symbols.Program.GetFile(file.FileName)!;
        if (ModuleKind is >= 100 and <= 199)
            return metadata.ImpliedFormat;
        if (metadata.ImpliedFormat == ReferenceResolutionMode.Require
            && (metadata.PackageType == "commonjs"
                || file.FileName.EndsWith(".cts", StringComparison.OrdinalIgnoreCase)
                || file.FileName.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase)))
            return ReferenceResolutionMode.Require;
        if (metadata.ImpliedFormat == ReferenceResolutionMode.Import
            && (metadata.PackageType == "module"
                || file.FileName.EndsWith(".mts", StringComparison.OrdinalIgnoreCase)
                || file.FileName.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)))
            return ReferenceResolutionMode.Import;
        return ReferenceResolutionMode.Unspecified;
    }

    private bool DefaultOnlyModule(Symbol module, SyntaxNode specifier) => ModuleKind is >= 100 and <= 199
        && ModuleUsageMode(specifier) == ReferenceResolutionMode.Import && module.Declarations.OfType<SourceFileNode>().Any(
            f => f.ScriptKind == ScriptKind.JSON || f.FileName.EndsWith(".d.json.ts", StringComparison.OrdinalIgnoreCase));

    private async ValueTask<Symbol?> ResolveModuleExportAsync(
        Symbol module,
        string name,
        bool dontResolveAlias,
        CancellationToken cancellation)
    {
        Symbol? symbol;
        if (module.Exports.TryGetValue("export=", out var assignment))
            symbol = await Properties.PropertyAsync(
                await Values.GetAsync(assignment, cancellation).ConfigureAwait(false),
                name,
                true,
                cancellation: cancellation).ConfigureAwait(false);
        else
            symbol = module.Exports.GetValueOrDefault(name);
        return await program.Aliases.SymbolAsync(symbol, dontResolveAlias, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<bool> SyntheticDefaultAsync(
        Symbol module,
        SyntaxNode usage,
        bool dontResolveAlias,
        CancellationToken cancellation)
    {
        var file = module.Declarations.OfType<SourceFileNode>().FirstOrDefault();
        if (file is not null)
        {
            var mode = ModuleUsageMode(usage);
            var target = ModuleTargetMode(file);
            if (mode == ReferenceResolutionMode.Import && target == ReferenceResolutionMode.Require && ModuleKind is >= 100 and <= 199)
                return true;
            if (mode == ReferenceResolutionMode.Import && target == ReferenceResolutionMode.Import)
                return false;
            if (target == ReferenceResolutionMode.Unspecified && file.IsDeclarationFile
                && program.Symbols.Program.ProjectReferences.Find(file.FileName) is { } redirect && mode == ReferenceResolutionMode.Import)
            {
                string? declaredMode = redirect.Project.Options.String("module");
                if (declaredMode is "es6" or "es2015" or "es2020" or "es2022" or "esnext")
                    return false;
            }
        }
        if (file is null || file.IsDeclarationFile)
        {
            var declaredDefault = await ResolveModuleExportAsync(module, "default", true, cancellation).ConfigureAwait(false);
            if (declaredDefault?.Declarations.Any(
                d => d is ExportAssignmentNode { IsExportEquals: false } || SemanticSyntax.HasModifier(d, SyntaxKind.DefaultKeyword)
                || d is ExportSpecifierNode or NamespaceExportNode) == true)
                return false;
            return await ResolveModuleExportAsync(module, "__esModule", dontResolveAlias, cancellation).ConfigureAwait(false) is null;
        }
        if ((file.Flags & NodeFlags.JavaScriptFile) == 0)
            return module.Exports.ContainsKey("export=");
        return (file.ExternalModuleIndicator is null || file.ExternalModuleIndicator == file)
            && await ResolveModuleExportAsync(module, "__esModule", dontResolveAlias, cancellation).ConfigureAwait(false) is null;
    }

    internal async ValueTask<Symbol?> ModuleDefaultAsync(
        Symbol module,
        SyntaxNode node,
        bool dontResolveAlias,
        CancellationToken cancellation)
    {
        var specifier = AliasTargets.ModuleSpecifier(node);
        var file = module.Declarations.OfType<SourceFileNode>().FirstOrDefault();
        if (specifier is not null
            && file is not null
            && ModuleKind is >= 102 and <= 199
            && ModuleUsageMode(specifier) == ReferenceResolutionMode.Require
            && ModuleTargetMode(file) == ReferenceResolutionMode.Import && await ResolveModuleExportAsync(
                module,
                "module.exports",
                dontResolveAlias,
                cancellation).ConfigureAwait(false) is { } moduleExports)
            return moduleExports;
        var result = await ResolveModuleExportAsync(module, "default", dontResolveAlias, cancellation).ConfigureAwait(false);
        if (specifier is null)
            return result;
        bool synthetic = await SyntheticDefaultAsync(
            module,
            specifier,
            dontResolveAlias,
            cancellation).ConfigureAwait(false), only = DefaultOnlyModule(module, specifier);
        if (result is null && !synthetic && !only)
        {
            if (node is ImportClauseNode clause)
            {
                bool named = module.Exports.ContainsKey(clause.Name!.Text);
                var diagnostic = CheckerDiagnostic.Create(
                    clause.Name!,
                    DiagnosticLocalization.GetMessage(
                        named
                            ? DiagnosticCode.Module0HasNoDefaultExportDidYouMeanToUseImport1From0Instead
                            : DiagnosticCode.Module0HasNoDefaultExport),
                    named ? [TypeDisplay.SymbolName(module), clause.Name.Text] : [TypeDisplay.SymbolName(module)]);
                if (!named && module.Exports.TryGetValue(Symbol.InternalPrefix + "export", out var stars))
                    foreach (var export in stars.Declarations.OfType<ExportDeclarationNode>())
                        if (export.ModuleSpecifier is not null
                            && await program.ExportStarModuleAsync(export, cancellation) is { } exported
                            && exported.Exports.ContainsKey("default"))
                        {
                            diagnostic = diagnostic with
                            {
                                RelatedInformation = [CheckerDiagnostic.Create(
                                export,
                                Messages.X_export_Asterisk_does_not_re_export_a_default)]
                            };
                            break;
                        }
                Error(clause.Name!, diagnostic);
            }
            else
                await program.MissingModuleMemberAsync(
                    module,
                    module,
                    node,
                    (node as ExportSpecifierNode)?.PropertyName ?? (node as ImportSpecifierNode)?.PropertyName ?? SemanticSyntax.Name(node)!,
                    cancellation).ConfigureAwait(false);
        }
        else if (synthetic || only)
            return await program.AliasTargets.ExternalModuleAsync(module, dontResolveAlias, cancellation).ConfigureAwait(false)
            ?? await program.Aliases.SymbolAsync(module, dontResolveAlias, cancellation).ConfigureAwait(false);
        return result;
    }

    internal async ValueTask<Symbol?> AdjustModuleAsync(
        Symbol module,
        Symbol target,
        SyntaxNode node,
        SyntaxNode specifier,
        CancellationToken cancellation)
    {
        var parent = specifier.Parent;
        bool namespaceImport = parent is ImportDeclarationNode { ImportClause.NamedBindings: NamespaceImportNode };
        if (!namespaceImport && parent is not CallExpressionNode)
            return target;
        var type = await Values.GetAsync(target, cancellation).ConfigureAwait(false);
        bool only = DefaultOnlyModule(module, specifier);
        if (only && type != context.ErrorType)
        {
            if (!syntheticModuleTypes.TryGetValue((type, true), out var wrapped))
                syntheticModuleTypes[(type, true)] = wrapped = await DefaultWrapperAsync(
                    target,
                    module,
                    null,
                    cancellation).ConfigureAwait(false);
            return await program.ModuleTypes.CloneAsync(
                target,
                await Members.ResolveAsync((StructuredType)wrapped, cancellation).ConfigureAwait(false),
                parent!,
                cancellation).ConfigureAwait(false);
        }
        var file = module.Declarations.OfType<SourceFileNode>().FirstOrDefault();
        var mode = ModuleUsageMode(specifier);
        bool signatures = (await SignaturesAsync(type, false, cancellation).ConfigureAwait(false)).Count != 0
            || (await SignaturesAsync(type, true, cancellation).ConfigureAwait(false)).Count != 0;
        if (namespaceImport
            && file is not null
            && ModuleKind is >= 102 and <= 199
            && mode == ReferenceResolutionMode.Require
            && ModuleTargetMode(file) == ReferenceResolutionMode.Import
            && await program.ModuleExports.ExportAsync(
                target,
                "module.exports",
                node,
                true,
                cancellation).ConfigureAwait(false) is { } moduleExports)
            return signatures
                ? await program.ModuleTypes.CloneAsync(
                    moduleExports,
                    await Members.ResolveAsync((StructuredType)type, cancellation).ConfigureAwait(false),
                    parent!,
                    cancellation).ConfigureAwait(false)
                : moduleExports;
        bool esmCommonJs = file is not null
            && mode == ReferenceResolutionMode.Import
            && ModuleTargetMode(file) == ReferenceResolutionMode.Require;
        if (signatures
            || await Properties.PropertyAsync(type, "default", true, cancellation: cancellation).ConfigureAwait(false) is not null
            || esmCommonJs)
        {
            var adjusted = type is StructuredType ? await SyntheticModuleTypeAsync(
                type,
                target,
                module,
                specifier,
                cancellation).ConfigureAwait(false)
                : await DefaultWrapperAsync(target, target.Parent, null, cancellation).ConfigureAwait(false);
            return await program.ModuleTypes.CloneAsync(
                target,
                await Members.ResolveAsync((StructuredType)adjusted, cancellation).ConfigureAwait(false),
                parent!,
                cancellation).ConfigureAwait(false);
        }
        return target;
    }

    private async ValueTask<Type> SyntheticModuleTypeAsync(
        Type type,
        Symbol target,
        Symbol original,
        SyntaxNode specifier,
        CancellationToken cancellation)
    {
        if (type == context.ErrorType || (type.Flags & TypeFlags.Any) != 0 && type.Alias is not null)
            return type;
        if (syntheticModuleTypes.TryGetValue((type, false), out var cached))
            return cached;
        Type result = type;
        if (await SyntheticDefaultAsync(original, specifier, false, cancellation).ConfigureAwait(false))
        {
            var symbol = new Symbol(SymbolFlags.TypeLiteral | SymbolFlags.Transient, Symbol.InternalPrefix + "type");
            symbol.DeclarationList.AddRange(original.Declarations);
            var wrapper = await DefaultWrapperAsync(target, original, symbol, cancellation).ConfigureAwait(false);
            links.Values.Get(symbol).ResolvedType = wrapper;
            result = await Bindings.ValidSpreadAsync(type, cancellation).ConfigureAwait(false)
                ? await ObjectSpreads.GetAsync(type, wrapper, symbol, 0, false, cancellation).ConfigureAwait(false) : wrapper;
        }
        cancellation.ThrowIfCancellationRequested();
        return syntheticModuleTypes[(type, false)] = result;
    }

    private async ValueTask<Type> DefaultWrapperAsync(Symbol target, Symbol? original, Symbol? anonymous, CancellationToken cancellation)
    {
        var property = new Symbol(SymbolFlags.Alias | SymbolFlags.Transient, "default") { Parent = original };
        links.Values.Get(property).NameType = context.GetStringLiteralType("default");
        links.Aliases.Get(property).AliasTarget = await program.Aliases.SymbolAsync(
            target,
            cancellation: cancellation).ConfigureAwait(false);
        if (anonymous is null && original is not null)
        {
            anonymous = new Symbol(SymbolFlags.ObjectLiteral | SymbolFlags.Transient, Symbol.InternalPrefix + "object");
            anonymous.DeclarationList.AddRange(original.Declarations);
        }
        var result = context.NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved, anonymous);
        result.Members = new Dictionary<string, Symbol> { ["default"] = property }.AsReadOnly();
        result.Properties = [property];
        return result;
    }

    internal async ValueTask<Symbol?> ModuleMemberAsync(
        Symbol module,
        Symbol target,
        SyntaxNode specifier,
        SyntaxNode nameNode,
        bool dontResolveAlias,
        CancellationToken cancellation)
    {
        if (nameNode is not (IdentifierNode or StringLiteralNode) || nameNode is IdentifierNode { Text.Length: 0 })
            return null;
        string name = AliasTargets.Text(nameNode) ?? SyntaxNameText.Get(nameNode);
        if (module.ValueDeclaration is ModuleDeclarationNode { Body: null })
            return module;
        Symbol? value = null;
        bool exportEquals = module.Exports.ContainsKey("export=");
        if (exportEquals)
            value = await Properties.PropertyAsync(
                await Values.GetAsync(target, cancellation).ConfigureAwait(false),
                name,
                true,
                cancellation: cancellation).ConfigureAwait(false);
        else if ((target.Flags & SymbolFlags.Variable) != 0 && target.ValueDeclaration is ITypedNode { Type: { } annotation })
            value = await program.Aliases.SymbolAsync(
                await Properties.PropertyAsync(
                    await Nodes.FromNodeAsync(annotation, cancellation).ConfigureAwait(false),
                    name,
                    cancellation: cancellation).ConfigureAwait(false),
                cancellation: cancellation).ConfigureAwait(false);
        value = await program.Aliases.SymbolAsync(value, dontResolveAlias, cancellation).ConfigureAwait(false);
        var exported = await program.ModuleExports.ExportAsync(
            exportEquals ? module : target,
            name,
            specifier,
            dontResolveAlias,
            cancellation).ConfigureAwait(false);
        var result = exported is null ? value : value is null ? exported : CombineModuleSymbols(value, exported);
        if (result is null)
            await program.MissingModuleMemberAsync(module, target, specifier, nameNode, cancellation).ConfigureAwait(false);
        return result;
    }

    private static Symbol CombineModuleSymbols(Symbol value, Symbol type)
    {
        if ((type.Flags & SymbolFlags.Value) != 0)
            return type;
        if ((value.Flags & (SymbolFlags.Type | SymbolFlags.Namespace)) != 0)
            return value;
        var result = new Symbol(
            value.Flags | type.Flags | SymbolFlags.Transient,
            value.Name)
        { Parent = value.Parent ?? type.Parent, ValueDeclaration = value.ValueDeclaration };
        foreach (var declaration in value.Declarations.Concat(type.Declarations))
            if (result.DeclarationList.LastOrDefault() != declaration)
                result.DeclarationList.Add(declaration);
        foreach (var (name, member) in type.Members)
            result.MemberTable[name] = member;
        foreach (var (name, member) in value.Exports)
            result.ExportTable[name] = member;
        return result;
    }
}
