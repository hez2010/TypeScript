using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using static TypeScript.Compiler.Binding.SemanticSyntax;

namespace TypeScript.Compiler.Checking;

internal interface IValueUseHost
{
    Symbol LateBoundSymbol(Symbol symbol);

    bool ValidTypeOnlyUse(SyntaxNode node);

    bool MissingPrefix(SyntaxNode node, Utf8String name);

    void ValueUseError(SyntaxNode? node, DiagnosticMessage message, params Utf8String[] arguments);

    void ValueUseSuggestion(SyntaxNode node, DiagnosticMessage message, Utf8String name);

    void DeclarationRelatedInfo(SyntaxNode? location, DiagnosticCode code, SyntaxNode declaration, bool typeOnly, Utf8String name);
}

internal sealed class ValueUseChecks(CheckerSymbols symbols, AliasResolver aliases, DeclarationOrder order, IValueUseHost host)
{
    internal bool InvalidInitializer(SyntaxNode? location, Utf8String name, SyntaxNode declaration, Symbol? result)
    {
        if (order.StandardClassFields)
            return false;
        if (location is not null && result is null && host.MissingPrefix(location, name))
            return true;
        var property = (PropertyDeclarationNode)declaration;
        host.ValueUseError(
            location,
            location is not null && property.Type is { } type && type.Pos <= location.Pos && location.Pos <= type.End
            ? Messages.Type_of_instance_member_variable_0_cannot_reference_identifier_1_declared_in_the_constructor
            : Messages.Initializer_of_instance_member_variable_0_cannot_reference_identifier_1_declared_in_the_constructor,
            NameText(property), name);
        return true;
    }

    internal async ValueTask ResolvedAsync(SyntaxNode? location, Symbol symbol, SymbolFlags meaning, SyntaxNode? last,
        SyntaxNode? associatedDeclaration, bool deferred, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        Utf8String name = symbol.Name;
        bool inModule = last is SourceFileNode && symbols.Binding(last)?.IsModule == true;
        bool valueMeaning = (meaning & SymbolFlags.Value) == SymbolFlags.Value;
        if (location is not null && ((meaning & SymbolFlags.BlockScopedVariable) != 0
            || (meaning & (SymbolFlags.Class | SymbolFlags.Enum)) != 0 && valueMeaning))
        {
            var exported = symbols.ExportedValue(symbol)!;
            if ((exported.Flags & (SymbolFlags.BlockScopedVariable | SymbolFlags.Class | SymbolFlags.Enum)) != 0)
                await BlockScopedAsync(exported, location, cancellation).ConfigureAwait(false);
        }
        if (inModule && valueMeaning && location is not null && (location.Flags & NodeFlags.JSDoc) == 0)
        {
            var merged = symbols.Merger.GetMergedSymbol(symbol)!;
            if (merged.Declarations.Length != 0 && merged.Declarations.All(d => d is NamespaceExportDeclarationNode
                || d is SourceFileNode && symbols.Binding(d)?.GlobalExports.Count > 0))
            {
                if (symbols.Program.Configuration.Options.AllowUmdGlobalAccess == true)
                    host.ValueUseSuggestion(
                        location,
                        Messages.X_0_refers_to_a_UMD_global_but_the_current_file_is_a_module_Consider_adding_an_import_instead,
                        name);
                else
                    host.ValueUseError(
                        location,
                        Messages.X_0_refers_to_a_UMD_global_but_the_current_file_is_a_module_Consider_adding_an_import_instead,
                        name);
            }
        }
        if (associatedDeclaration is not null && !deferred && valueMeaning)
        {
            var candidate = symbols.Merger.GetMergedSymbol(host.LateBoundSymbol(symbol))!;
            var root = RootDeclaration(associatedDeclaration);
            if (candidate == symbols.Declaration(associatedDeclaration))
                host.ValueUseError(location, Messages.Parameter_0_cannot_reference_itself, NameText(associatedDeclaration));
            else if (candidate.ValueDeclaration is { } declaration && declaration.Pos > associatedDeclaration.Pos
                && root.Parent is { } parent && symbols.Lookup(
                    symbols.Binding(parent)?.Get(parent)?.Locals,
                    candidate.Name,
                    meaning) == candidate)
                host.ValueUseError(location, Messages.Parameter_0_cannot_reference_identifier_1_declared_after_it,
                    NameText(associatedDeclaration), location is null ? Utf8String.Empty : NameText(location));
        }
        if (location is not null && (meaning & SymbolFlags.Value) != 0 && (symbol.Flags & SymbolFlags.Alias) != 0
            && (symbol.Flags & SymbolFlags.Value) == 0 && !host.ValidTypeOnlyUse(location)
            && await aliases.TypeOnlyAsync(symbol, SymbolFlags.Value, cancellation).ConfigureAwait(false) is { } typeOnly)
        {
            var message = typeOnly is ExportSpecifierNode or ExportDeclarationNode or NamespaceExportNode
                ? Messages.X_0_cannot_be_used_as_a_value_because_it_was_exported_using_export_type
                : Messages.X_0_cannot_be_used_as_a_value_because_it_was_imported_using_import_type;
            host.ValueUseError(location, message, name);
            host.DeclarationRelatedInfo(location, message.Code, typeOnly, true, name);
        }
        if (symbols.Program.Configuration.Options.IsolatedModules == true && inModule && valueMeaning
            && symbols.Lookup(symbols.Globals, name, meaning) == symbol
            && symbols.Lookup(symbols.Binding(last!)?.Get(last!)?.Locals, name, ~SymbolFlags.Value) is { } nonValue)
        {
            var import = nonValue.Declarations.FirstOrDefault(
                d => d is ImportSpecifierNode or ImportClauseNode or NamespaceImportNode or ImportEqualsDeclarationNode);
            if (import is not null && !AliasResolver.IsTypeOnly(import))
                host.ValueUseError(
                    import,
                    Messages.Import_0_conflicts_with_global_value_used_in_this_file_so_must_be_declared_with_a_type_only_import_when_isolatedModules_is_enabled,
                    name);
        }
    }

    private async ValueTask BlockScopedAsync(Symbol symbol, SyntaxNode location, CancellationToken cancellation)
    {
        if ((symbol.Flags & (SymbolFlags.Function | SymbolFlags.FunctionScopedVariable | SymbolFlags.Assignment)) != 0
            && (symbol.Flags & SymbolFlags.Class) != 0)
            return;
        var declaration = symbol.Declarations.FirstOrDefault(
            d => DeclarationOrder.BlockScoped(d) || ClassLike(d) || d is EnumDeclarationNode)
            ?? throw new InvalidOperationException("Block-scoped symbol has no declaration");
        if ((declaration.Flags & NodeFlags.Ambient) != 0
            || await order.BeforeUseAsync(declaration, location, cancellation).ConfigureAwait(false))
            return;
        var options = symbols.Program.Configuration.Options;
        var message = (symbol.Flags & SymbolFlags.BlockScopedVariable) != 0 ? Messages.Block_scoped_variable_0_used_before_its_declaration
            : (symbol.Flags & SymbolFlags.Class) != 0 ? Messages.Class_0_used_before_its_declaration
            : (symbol.Flags & SymbolFlags.RegularEnum) != 0
                || options.IsolatedModules == true
                || options.VerbatimModuleSyntax == true
                ? Messages.Enum_0_used_before_its_declaration : null;
        if (message is not null)
        {
            host.ValueUseError(location, message, NameText(declaration));
            host.DeclarationRelatedInfo(location, message.Code, declaration, false, NameText(declaration));
        }
    }

    private static Utf8String NameText(SyntaxNode node)
    {
        node = Name(node) ?? node;
        if (node.Pos == node.End)
            return Utf8Literals.MissingDisplay;
        if (Source(node) is not { Source: var source })
            return node is IdentifierNode identifier ? identifier.Text : Utf8String.Empty;
        var scanner = new Scanner(source);
        scanner.ResetPosition(node.Pos);
        scanner.Scan();
        return source.Text[scanner.TokenStart..node.End];
    }
}
