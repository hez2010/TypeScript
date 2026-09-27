using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal Dictionary<SyntaxNode, IReadOnlyList<Symbol>> RequiredPropertyDeclarations { get; } = [];

    private async ValueTask<DiagnosticCode?> MissingRequiredPropertyCodeAsync(Type source, Type target, RelationKind relation,
        SyntaxNode node, CancellationToken cancellation)
    {
        if (source is not (ObjectType or IntersectionType) || target is not ObjectType)
            return null;
        if (source is IntersectionType intersection && intersection.Types.Any(t => (t.Flags & TypeFlags.Primitive) != 0))
            return null;
        if (source == GlobalObject || source is MappedType mapped && await Instantiation.Mapped.IsGenericAsync(mapped, cancellation))
            return null;
        if (await Normalization.GetAsync(source, false, cancellation) != source
            || await Normalization.GetAsync(target, true, cancellation) != target)
            return null;
        if (target is TypeReference { Target: TupleType tuple }
            && (ObjectRelations.ArrayOrTuple(source) || (tuple.CombinedFlags & ElementFlags.Variable) != 0))
            return null;
        var calls = await SignaturesAsync(source, false, cancellation);
        var constructors = await SignaturesAsync(source, true, cancellation);
        if ((calls.Count != 0 || constructors.Count != 0) && (await Properties.GetAsync(source, cancellation)).Count == 0
            && !(calls.Count != 0 && (await SignaturesAsync(target, false, cancellation)).Count != 0)
            && !(constructors.Count != 0 && (await SignaturesAsync(target, true, cancellation)).Count != 0))
            return null;
        bool requireOptional = relation is RelationKind.Subtype or RelationKind.StrictSubtype
            && (source.ObjectFlags & ObjectFlags.ObjectLiteral) == 0
            && !await EmptyArrayAsync(source, cancellation) && source is not TypeReference { Target: TupleType };
        var missing = new List<Symbol>();
        foreach (var property in await Properties.GetAsync(target, cancellation))
        {
            if (property.ValueDeclaration is { } declaration && SemanticSyntax.IsStatic(declaration)
                && SemanticSyntax.Name(declaration) is PrivateIdentifierNode)
                continue;
            if ((requireOptional || (property.Flags & SymbolFlags.Optional) == 0 && (property.CheckFlags & CheckFlags.Partial) == 0)
                && await Properties.PropertyAsync(source, property.Name, cancellation: cancellation) is null)
                missing.Add(property);
        }
        if (missing.Count == 0)
            return null;
        if (SemanticSyntax.Name(missing[0].ValueDeclaration) is PrivateIdentifierNode privateName
            && source.Symbol is { } sourceSymbol && (sourceSymbol.Flags & SymbolFlags.Class) != 0
            && await Properties.PropertyAsync(
                source,
                PrivateAccess.Name(sourceSymbol, privateName.Text),
                cancellation: cancellation) is not null)
            return null;
        if (missing.Count > 1)
        {
            bool mutableTarget = target is TypeReference { Target: TupleType { IsReadonly: false } }
                || IsArray(target) && !IsReadonlyArray(target);
            if (source is TypeReference { Target: TupleType sourceTuple })
            {
                if (sourceTuple.IsReadonly && mutableTarget || !ObjectRelations.ArrayOrTuple(target))
                    return null;
            }
            else if (IsReadonlyArray(source) && mutableTarget || target is TypeReference { Target: TupleType } && !IsArray(source))
                return null;
        }
        RequiredPropertyDeclarations[node] = missing;
        return missing.Count == 1
            ? DiagnosticCode.Property0IsMissingInType1ButRequiredInType2
            : missing.Count > 5
                ? DiagnosticCode.Type0IsMissingTheFollowingPropertiesFromType1Colon2And3More
                : DiagnosticCode.Type0IsMissingTheFollowingPropertiesFromType1Colon2;
    }

    private async ValueTask CheckMissingPropertiesAsync(SourceFileNode file, CancellationToken cancellation)
    {
        for (int i = 0; i < DeferredMissingProperties.Count; i++)
        {
            var (node, type, suggestion) = DeferredMissingProperties[i];
            cancellation.ThrowIfCancellationRequested();
            if (SemanticSyntax.Source(node) != file || (links.Nodes.Get(node).Flags & NodeCheckFlags.TypeChecked) != 0)
                continue;
            links.Nodes.Get(node).Flags |= NodeCheckFlags.TypeChecked;
            TextSlice name = SyntaxNameText.Get(node);
            TextSlice displayedName = CheckerDiagnostic.DeclarationName(node);
            TextSlice receiver = await TypeDisplay.GetAsync(await Views.ReducedAsync(type, cancellation), cancellation);
            Diagnostic? chain = null;
            if (node is not PrivateIdentifierNode && type is UnionType union && (type.Flags & TypeFlags.Primitive) == 0)
                foreach (var part in union.Types)
                    if (await Properties.PropertyAsync(part, name, cancellation: cancellation) is null
                        && await ApplicableIndexAsync(part, name, cancellation) is null)
                    {
                        chain = CheckerDiagnostic.Create(node, Messages.Property_0_does_not_exist_on_type_1,
                            displayedName, await TypeDisplay.GetAsync(await Views.ReducedAsync(part, cancellation), cancellation));
                        break;
                    }
            Diagnostic Report(DiagnosticCode code, params TextSlice[] arguments) => CheckerDiagnostic.Create(
                node,
                DiagnosticLocalization.GetMessage(code),
                arguments)
                with
            { MessageChain = chain is null ? [] : [chain] };
            Diagnostic diagnostic;
            if (await StaticPropertyAsync(name, type, cancellation).ConfigureAwait(false))
                diagnostic = Report(
                    DiagnosticCode.Property0DoesNotExistOnType1DidYouMeanToAccessTheStaticMember2Instead,
                    displayedName,
                    receiver,
                    TextSlice.Concat(receiver, ".", displayedName));
            else if (await Awaited.OfPromiseAsync(type, cancellation: cancellation).ConfigureAwait(false) is { } promised
                && await Properties.PropertyAsync(promised, name, cancellation: cancellation).ConfigureAwait(false) is not null)
                diagnostic = Report(DiagnosticCode.Property0DoesNotExistOnType1, displayedName, receiver) with
                { RelatedInformation = [CheckerDiagnostic.Create(node, Messages.Did_you_forget_to_use_await)] };
            else if (LibraryFeatures.PropertyLibrary(
                (await ApparentAsync(type, cancellation).ConfigureAwait(false)).Symbol?.Name,
                displayedName) is { } library)
                diagnostic = Report(
                    DiagnosticCode.Property0DoesNotExistOnType1DoYouNeedToChangeYourTargetLibraryTryChangingTheLibCompilerOptionTo2OrLater,
                    displayedName,
                    receiver,
                    library);
            else
            {
                var candidates = new List<Symbol>();
                foreach (var property in await Properties.GetAsync(type, cancellation).ConfigureAwait(false))
                    if (node.Parent is not PropertyAccessExpressionNode access
                        || await MemberAccessibility.CheckAsync(
                            access,
                            access.Expression!.Kind == SyntaxKind.SuperKeyword,
                            false,
                            type,
                            property,
                            false,
                            cancellation).ConfigureAwait(false))
                        candidates.Add(property);
                var similar = await SymbolSuggestions.FindAsync(name, candidates, SymbolFlags.Value, cancellation).ConfigureAwait(false);
                if (similar is not null)
                {
                    diagnostic = Report(
                        suggestion
                            ? DiagnosticCode.Property0MayNotExistOnType1DidYouMean2
                            : DiagnosticCode.Property0DoesNotExistOnType1DidYouMean2,
                        displayedName,
                        receiver,
                        similar.Name);
                    if (similar.ValueDeclaration is { } declaration)
                        diagnostic = diagnostic with
                        {
                            RelatedInformation = [CheckerDiagnostic.Create(
                            declaration,
                            Messages.X_0_is_declared_here,
                            similar.Name)]
                        };
                }
                else
                {
                    if (type is IntersectionType intersection && (type.ObjectFlags & ObjectFlags.IsNeverIntersection) != 0)
                    {
                        Symbol? conflict = null;
                        DiagnosticCode code = DiagnosticCode.TheIntersection0WasReducedToNeverBecauseProperty1HasConflictingTypesInSomeConstituents;
                        foreach (var property in await Properties.CompositePropertiesAsync(intersection, cancellation))
                            if ((property.Flags & SymbolFlags.Optional) == 0
                                && (property.CheckFlags & (CheckFlags.NonUniformAndLiteral | CheckFlags.HasNeverType)) == CheckFlags.NonUniformAndLiteral
                                && ((await Values.GetAsync(property, cancellation)).Flags & TypeFlags.Never) != 0)
                            {
                                conflict = property;
                                break;
                            }
                        if (conflict is null)
                        {
                            code = DiagnosticCode.TheIntersection0WasReducedToNeverBecauseProperty1ExistsInMultipleConstituentsAndIsPrivateInSome;
                            conflict = (await Properties.CompositePropertiesAsync(intersection, cancellation))
                                .FirstOrDefault(p => p.ValueDeclaration is null && (p.CheckFlags & CheckFlags.ContainsPrivate) != 0);
                        }
                        if (conflict is not null)
                            chain = Report(code, await TypeDisplay.GetAsync(type, NodeBuilderFlags.NoTypeReduction, cancellation),
                                TypeDisplay.SymbolName(conflict));
                    }
                    diagnostic = Report(
                        await EmptyDomTypeAsync(type, cancellation).ConfigureAwait(false)
                            ? DiagnosticCode.Property0DoesNotExistOnType1TryChangingTheLibCompilerOptionToIncludeDom
                            : DiagnosticCode.Property0DoesNotExistOnType1,
                        displayedName,
                        receiver);
                }
            }
            if (suggestion && diagnostic.Code == DiagnosticCode.Property0MayNotExistOnType1DidYouMean2)
                ExpressionSuggestion(node, diagnostic.Code);
            else
                Error(node, diagnostic);
        }
        DeferredMissingProperties.RemoveAll(d => SemanticSyntax.Source(d.Node) == file);
    }

    private async ValueTask<bool> EmptyDomTypeAsync(Type type, CancellationToken cancellation)
    {
        if (program.Symbols.Program.Configuration.Options.Strings("lib")?.Any(l => l is "dom" or "lib.dom.d.ts") == true)
            return false;
        var pending = new Stack<Type>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            if (current is UnionOrIntersectionType composite)
            {
                foreach (var part in composite.Types)
                    pending.Push(part);
                continue;
            }
            if (current.Symbol is not { } symbol)
                return false;
            ReadOnlySpan<char> name = symbol.Name.Span;
            if (name is not ("EventTarget" or "Node" or "Element")
                && !(name.StartsWith("HTML", StringComparison.Ordinal) && name.EndsWith("Element", StringComparison.Ordinal)))
                return false;
        }
        return await Views.EmptyObjectAsync(type, cancellation).ConfigureAwait(false);
    }
}
