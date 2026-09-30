using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IEnumValueHost
{
    bool IsolatedModules { get; }

    ValueTask<bool> DeclaredBeforeUseAsync(SyntaxNode declaration, SyntaxNode use, CancellationToken cancellation);

    ValueTask CheckComputedEnumAsync(EnumMemberNode member, CancellationToken cancellation);

    void EnumError(SyntaxNode node, DiagnosticCode code);
}

internal sealed class EnumValues
{
    private readonly CheckerSymbols symbols;
    private readonly EntityNames names;
    private readonly CheckerLinks links;
    private readonly IEnumValueHost host;
    private readonly Dictionary<EnumMemberNode, ConstantResult> values = [];
    internal ConstantEvaluator Evaluator { get; }

    internal EnumValues(CheckerSymbols symbols, EntityNames names, CheckerLinks links, IEnumValueHost host)
    {
        this.symbols = symbols;
        this.names = names;
        this.links = links;
        this.host = host;
        Evaluator = new(EntityAsync);
    }

    internal async ValueTask<ConstantResult> GetAsync(EnumMemberNode member, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        var declaration = (EnumDeclarationNode)member.Parent!;
        var data = links.Nodes.Get(declaration);
        if ((data.Flags & NodeCheckFlags.EnumValuesComputed) == 0)
        {
            data.Flags |= NodeCheckFlags.EnumValuesComputed;
            var published = new List<EnumMemberNode>();
            try
            {
                double? automatic = 0;
                EnumMemberNode? previous = null;
                foreach (var item in declaration.Members!.OfType<EnumMemberNode>())
                {
                    var result = await MemberAsync(item, automatic, previous, cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    values[item] = result;
                    published.Add(item);
                    automatic = result.Value is double number ? number + 1 : null;
                    previous = item;
                }
            }
            catch
            {
                data.Flags &= ~NodeCheckFlags.EnumValuesComputed;
                foreach (var item in published)
                    values.Remove(item);
                throw;
            }
        }
        return values.GetValueOrDefault(member);
    }

    private async ValueTask<ConstantResult> MemberAsync(
        EnumMemberNode member,
        double? automatic,
        EnumMemberNode? previous,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var declaration = (EnumDeclarationNode)member.Parent!;
        if (DynamicName(member.Name!))
            host.EnumError(member.Name!, DiagnosticCode.ComputedPropertyNamesAreNotAllowedInEnums);
        else if (member.Name is BigIntLiteralNode)
            host.EnumError(member.Name, DiagnosticCode.AnEnumMemberCannotHaveANumericName);
        else
        {
            Utf8String text = NameText(member.Name!);
            if (IndexSignatures.NumericName(text) && !(text.Span.SequenceEqual("Infinity"u8) || text.Span.SequenceEqual("-Infinity"u8) || text.Span.SequenceEqual("NaN"u8)))
                host.EnumError(member.Name!, DiagnosticCode.AnEnumMemberCannotHaveANumericName);
        }
        bool constant = SemanticSyntax.HasModifier(declaration, SyntaxKind.ConstKeyword);
        if (member.Initializer is { } initializer)
        {
            var result = await Evaluator.EvaluateAsync(initializer, member, cancellation).ConfigureAwait(false);
            if (result.Value is not null)
            {
                if (constant && result.Value is double number && !double.IsFinite(number))
                    host.EnumError(
                        initializer,
                        double.IsNaN(number)
                            ? DiagnosticCode.XConstEnumMemberInitializerWasEvaluatedToDisallowedValueNaN
                            : DiagnosticCode.XConstEnumMemberInitializerWasEvaluatedToANonFiniteValue);
                if (host.IsolatedModules && result.Value is Utf8String && !result.IsSyntacticallyString)
                    host.EnumError(
                        initializer,
                        DiagnosticCode.X0HasAStringTypeButMustHaveSyntacticallyRecognizableStringSyntaxWhenIsolatedModulesIsEnabled);
            }
            else if (constant)
                host.EnumError(initializer, DiagnosticCode.XConstEnumMemberInitializersMustBeConstantExpressions);
            else if ((declaration.Flags & NodeFlags.Ambient) != 0)
                host.EnumError(initializer, DiagnosticCode.InAmbientEnumDeclarationsMemberInitializerMustBeConstantExpression);
            else
                await host.CheckComputedEnumAsync(member, cancellation).ConfigureAwait(false);
            return result;
        }
        if ((declaration.Flags & NodeFlags.Ambient) != 0 && !constant)
            return default;
        if (automatic is null)
        {
            host.EnumError(member.Name!, DiagnosticCode.EnumMemberMustHaveInitializer);
            return default;
        }
        if (host.IsolatedModules && previous?.Initializer is not null)
        {
            var result = await GetAsync(previous, cancellation).ConfigureAwait(false);
            if (result.Value is not double || result.ResolvedOtherFiles)
                host.EnumError(
                    member.Name!,
                    DiagnosticCode.EnumMemberFollowingANonLiteralNumericMemberMustHaveAnInitializerWhenIsolatedModulesIsEnabled);
        }
        return new(automatic.Value);
    }

    private async ValueTask<ConstantResult> EntityAsync(SyntaxNode expression, SyntaxNode? location, CancellationToken cancellation)
    {
        if (expression is IdentifierNode or PropertyAccessExpressionNode)
        {
            var symbol = await names.ResolveAsync(expression, SymbolFlags.Value, true, cancellation: cancellation).ConfigureAwait(false);
            if (symbol is null)
                return default;
            if (expression is IdentifierNode { Text.Span: var matchedText } identifier && (matchedText.SequenceEqual("Infinity"u8) || matchedText.SequenceEqual("NaN"u8))
                && symbols.Globals.GetValueOrDefault(identifier.Text) == symbol)
                return new(JsNumber.FromString(identifier.Text));
            if ((symbol.Flags & SymbolFlags.EnumMember) != 0)
                return location is null ? await GetAsync((EnumMemberNode)symbol.ValueDeclaration!, cancellation).ConfigureAwait(false)
                    : await ReferenceAsync(expression, symbol, location, cancellation).ConfigureAwait(false);
            if (ConstantVariable(symbol)
                && symbol.ValueDeclaration is VariableDeclarationNode { Type: null, Initializer: { } initializer } declaration
                && (location is null
                    || declaration != location
                        && await host.DeclaredBeforeUseAsync(declaration, location, cancellation).ConfigureAwait(false)))
            {
                var result = await Evaluator.EvaluateAsync(initializer, declaration, cancellation).ConfigureAwait(false);
                return location is not null && SemanticSyntax.Source(location) != SemanticSyntax.Source(declaration)
                    ? new(result.Value, false, true, true) : result with { HasExternalReferences = true };
            }
            return default;
        }
        if (expression is ElementAccessExpressionNode access && ConstantEvaluator.EntityName(access.Expression!)
            && access.ArgumentExpression is StringLiteralNode or NoSubstitutionTemplateLiteralNode)
        {
            var symbol = await names.ResolveAsync(
                access.Expression,
                SymbolFlags.Value,
                true,
                cancellation: cancellation).ConfigureAwait(false);
            Utf8String name = NameText(access.ArgumentExpression);
            if (symbol is not null && (symbol.Flags & SymbolFlags.Enum) != 0 && symbol.Exports.TryGetValue(name, out var member))
                return location is null ? await GetAsync((EnumMemberNode)member.ValueDeclaration!, cancellation).ConfigureAwait(false)
                    : await ReferenceAsync(expression, member, location, cancellation).ConfigureAwait(false);
        }
        return default;
    }

    private async ValueTask<ConstantResult> ReferenceAsync(
        SyntaxNode expression,
        Symbol symbol,
        SyntaxNode location,
        CancellationToken cancellation)
    {
        var declaration = symbol.ValueDeclaration as EnumMemberNode;
        if (declaration is null || declaration == location)
        {
            host.EnumError(expression, DiagnosticCode.Property0IsUsedBeforeBeingAssigned);
            return default;
        }
        if (!await host.DeclaredBeforeUseAsync(declaration, location, cancellation).ConfigureAwait(false))
        {
            host.EnumError(
                expression,
                DiagnosticCode.AMemberInitializerInAEnumDeclarationCannotReferenceMembersDeclaredAfterItIncludingMembersDefinedInOtherEnums);
            return new(0d);
        }
        var value = await GetAsync(declaration, cancellation).ConfigureAwait(false);
        return location.Parent != declaration.Parent ? value with { HasExternalReferences = true } : value;
    }

    internal static bool DynamicName(SyntaxNode node) => node is ComputedPropertyNameNode computed
        && computed.Expression is not (StringLiteralNode or NumericLiteralNode or NoSubstitutionTemplateLiteralNode);

    private static bool ConstantVariable(Symbol symbol) => (symbol.Flags & SymbolFlags.Variable) != 0
        && symbol.ValueDeclaration is VariableDeclarationNode { Parent: VariableDeclarationListNode list } && (list.Flags & NodeFlags.Constant) != 0;

    internal static Utf8String NameText(SyntaxNode node) => node switch
    {
        IdentifierNode identifier => identifier.Text,
        StringLiteralNode literal => literal.Text,
        NumericLiteralNode literal => literal.Text,
        NoSubstitutionTemplateLiteralNode literal => literal.Text,
        ComputedPropertyNameNode computed => NameText(computed.Expression!),
        _ => Utf8String.Empty
    };

}
