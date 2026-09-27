using System.Globalization;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private async ValueTask ReportRelationMessageAsync(
        SyntaxNode node,
        int code,
        Type source,
        Type target,
        RelationKind kind,
        CancellationToken cancellation,
        Diagnostic? head = null)
    {
        if (code is not (1270 or 1271 or 1360 or 2322 or 2344 or 2345 or 2352 or 2375 or 2379 or 2412 or 2415 or 2417 or 2418 or 2430
            or 2420 or 2636 or 2678 or 2684
            or 2720 or 2739
            or 2740
            or 2741
            or 2787 or 2788
            or 2789 or 18033 or 18053 or 2850 or 2851 or 2861))
        {
            RelationError(node, code);
            return;
        }
        (source, target) = await RelationErrorTypesAsync(source, target, cancellation);
        if (code == 2345 && context.ExactOptionalPropertyTypes
            && (await RelationDiagnostics.ExactOptionalPropertiesAsync(source, target, cancellation)).Count != 0)
            code = 2379;
        var originalSource = source;
        var (sourceText, targetText) = await RelationTypeNamesAsync(source, target, cancellation);
        if ((target.Flags & TypeFlags.Never) == 0 && source.IsLiteral && !await CouldHaveSingletonTypesAsync(target, cancellation))
        {
            source = await Widening.LiteralBaseAsync(source, cancellation);
            sourceText = await TypeDisplay.GetAsync(source, NodeBuilderFlags.UseFullyQualifiedType, cancellation);
        }
        string[] arguments;
        if (code is 2739 or 2740 or 2741 && RequiredPropertyDeclarations.TryGetValue(node, out var missing))
        {
            if (code == 2741)
                arguments = [TypeDisplay.SymbolName(missing[0]), sourceText, targetText];
            else if (code == 2740)
                arguments = [sourceText, targetText, string.Join(", ", missing.Take(4).Select(TypeDisplay.SymbolName)),
                    (missing.Count - 4).ToString(CultureInfo.InvariantCulture)];
            else
                arguments = [sourceText, targetText, string.Join(", ", missing.Select(TypeDisplay.SymbolName))];
        }
        else
            arguments = [sourceText, targetText];
        var diagnostic = CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code), arguments);
        diagnostic = await PrimitiveWrapperNoteAsync(diagnostic, originalSource, target, cancellation);
        if (code == 2741 && RequiredPropertyDeclarations.TryGetValue(node, out var required)
            && required[0].Declarations.FirstOrDefault() is { } declaration)
            diagnostic = diagnostic with
            {
                RelatedInformation = [CheckerDiagnostic.Create(declaration, Messages.X_0_is_declared_here,
                TypeDisplay.SymbolName(required[0]))]
            };
        if (code is not (2739 or 2740 or 2741))
        {
            var explanation = await Relations.ExplainAsync(originalSource, target, kind, cancellation);
            if (await RelationChainAsync(explanation?.Next, diagnostic, cancellation) is { } chain)
            {
                diagnostic = diagnostic with { MessageChain = [chain], RelatedInformation = chain.RelatedInformation };
                if ((originalSource.ObjectFlags & ObjectFlags.JsxAttributes) != 0 && target is IntersectionType intersection)
                {
                    var intrinsic = await JsxTypeAsync("IntrinsicAttributes", node, cancellation);
                    var classIntrinsic = await JsxTypeAsync("IntrinsicClassAttributes", node, cancellation);
                    if (intrinsic != context.ErrorType && classIntrinsic != context.ErrorType
                        && (intersection.Types.Contains(intrinsic) || intersection.Types.Contains(classIntrinsic)))
                    {
                        Report(StripRelationMarkers(chain));
                        return;
                    }
                }
            }
            diagnostic = await ConstraintReasonAsync(diagnostic, originalSource, source, target, sourceText, targetText, cancellation);
        }
        diagnostic = await RelationMessageKindAsync(diagnostic, originalSource, target, sourceText, targetText, cancellation);
        diagnostic = SelectRelationDiagnostic(diagnostic, originalSource, target, sourceText, targetText);
        diagnostic = await SourceConstraintNoteAsync(diagnostic, originalSource, target, cancellation);
        Report(StripRelationMarkers(diagnostic));

        void Report(Diagnostic detail) => RelationError(node, head is null ? detail
            : head with { MessageChain = [detail], RelatedInformation = detail.RelatedInformation });
    }

    private bool ReadonlyAssignment(Type source, Type target) =>
        (source is TypeReference { Target: TupleType { IsReadonly: true } } || IsReadonlyArray(source))
        && (target is TypeReference { Target: TupleType { IsReadonly: false } } || IsArray(target) && !IsReadonlyArray(target));

    private Diagnostic SelectRelationDiagnostic(Diagnostic diagnostic, Type source, Type target, string sourceText, string targetText)
    {
        if (ReadonlyAssignment(source, target))
            return diagnostic with
            {
                Message = DiagnosticLocalization.GetMessage(4104),
                Arguments = [sourceText, targetText],
                MessageChain = diagnostic.Code is 2739 or 2740 or 2741 ? [diagnostic] : diagnostic.MessageChain
            };
        if (source == GlobalObject && (target.Flags & TypeFlags.Primitive) == 0)
            return diagnostic with
            {
                MessageChain = [diagnostic with
            {
                Message = DiagnosticLocalization.GetMessage(2696),
                Arguments = []
            }]
            };
        if (diagnostic.MessageChain is not [var next])
            return diagnostic;
        bool matches = next.Code switch
        {
            2353 or 2561 => true,
            4104 => next.Arguments.SequenceEqual(new[] { sourceText, targetText }),
            2559 or 2560 => true,
            2741 when diagnostic.Code is not (2415 or 2417 or 2430 or 2420 or 2720 or 2352 or 2787 or 2788 or 2789) => next.Arguments is [_, var s, var t]
                && s == sourceText
                && t == targetText,
            2739 or 2740 when diagnostic.Code is not (2415 or 2417 or 2430 or 2420 or 2720 or 2352 or 2787 or 2788 or 2789) => next.Arguments.Length >= 2
                && next.Arguments[0] == sourceText
                && next.Arguments[1] == targetText,
            _ => false
        };
        return matches ? next : diagnostic;
    }

    private static Diagnostic StripRelationMarkers(Diagnostic diagnostic)
    {
        var children = new List<Diagnostic>();
        foreach (var child in diagnostic.MessageChain)
        {
            var current = child;
            while (current.Message.ElidedInCompatibilityPyramid && current.MessageChain.Count == 1)
                current = current.MessageChain[0];
            if (!current.Message.ElidedInCompatibilityPyramid)
                children.Add(StripRelationMarkers(current));
        }
        return diagnostic with { MessageChain = children };
    }

    private async ValueTask<Diagnostic?> RelationChainAsync(
        RelationExplanation? explanation,
        Diagnostic location,
        CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        if (explanation is null)
            return null;
        var next = await RelationChainAsync(explanation.Next, location, cancellation);
        if (explanation.Code == 2326 && next?.Code is 2353 or 2561)
            return next;
        if (explanation.Code == 2353)
        {
            var property = explanation.Property!;
            var name = SemanticSyntax.Name(property.ValueDeclaration);
            var target = explanation.Target!;
            var suggestion = name is IdentifierNode identifier
                ? await SymbolSuggestions.FindAsync(identifier.Text, await Properties.GetAsync(target, cancellation),
                    SymbolFlags.Value, cancellation) : null;
            var excessDiagnostic = name is not null && SemanticSyntax.Source(name)?.FileName == location.FileName
                ? CheckerDiagnostic.Create(name, Messages.Object_literal_may_only_specify_known_properties_and_0_does_not_exist_in_type_1)
                : location;
            string propertyText = TypeDisplay.SymbolName(property), targetText = await TypeDisplay.GetAsync(target, cancellation);
            return excessDiagnostic with
            {
                Message = DiagnosticLocalization.GetMessage(suggestion is null ? 2353 : 2561),
                Arguments = suggestion is null ? [propertyText, targetText] : [propertyText, targetText, suggestion.Name],
                MessageChain = [],
                RelatedInformation = []
            };
        }
        var diagnostic = location with
        {
            Message = DiagnosticLocalization.GetMessage(explanation.Code),
            Arguments = [],
            MessageChain = next is null ? [] : [next],
            RelatedInformation = next?.RelatedInformation ?? []
        };
        if (explanation.Code is 2322 or 2678)
        {
            var (source, target) = await RelationErrorTypesAsync(explanation.Source!, explanation.Target!, cancellation);
            var originalSource = source;
            var (sourceText, targetText) = await RelationTypeNamesAsync(source, target, cancellation);
            if ((target.Flags & TypeFlags.Never) == 0 && source.IsLiteral && !await CouldHaveSingletonTypesAsync(target, cancellation))
            {
                source = await Widening.LiteralBaseAsync(source, cancellation);
                sourceText = await TypeDisplay.GetAsync(source, NodeBuilderFlags.UseFullyQualifiedType, cancellation);
            }
            diagnostic = diagnostic with { Arguments = [sourceText, targetText] };
            diagnostic = await PrimitiveWrapperNoteAsync(diagnostic, originalSource, target, cancellation);
            diagnostic = await ConstraintReasonAsync(diagnostic, originalSource, source, target, sourceText, targetText, cancellation);
            diagnostic = await RelationMessageKindAsync(diagnostic, originalSource, target, sourceText, targetText, cancellation);
            return await SourceConstraintNoteAsync(SelectRelationDiagnostic(diagnostic, originalSource, target, sourceText, targetText),
                originalSource, target, cancellation);
        }
        if (explanation.Arguments is { } supplied)
        {
            var arguments = new string[supplied.Count];
            for (int i = 0; i < arguments.Length; i++)
                arguments[i] = supplied[i] switch
                {
                    string text => text,
                    int count => count.ToString(CultureInfo.InvariantCulture),
                    Type type => await TypeDisplay.GetAsync(type, cancellation),
                    Symbol symbol => TypeDisplay.SymbolName(symbol),
                    IReadOnlyList<Symbol> symbols => string.Join(", ", symbols.Select(TypeDisplay.SymbolName)),
                    Signature signature => await TypeDisplay.GetSignatureAsync(signature, cancellation),
                    TypePredicate predicate => await TypeDisplay.GetPredicateAsync(predicate, cancellation),
                    _ => throw new InvalidOperationException("Unsupported relation argument")
                };
            diagnostic = diagnostic with { Arguments = arguments };
        }
        else
            diagnostic = diagnostic with
            {
                Arguments = explanation.Code switch
                {
                    2741 => [TypeDisplay.SymbolName(explanation.Property!), await TypeDisplay.GetAsync(explanation.Source!, cancellation),
                        await TypeDisplay.GetAsync(explanation.Target!, cancellation)],
                    2326 or 2530 => [TypeDisplay.SymbolName(explanation.Property!)],
                    2634 => [await TypeDisplay.GetAsync(explanation.Source!, cancellation)],
                    2517 or 2518 or 2685 => [],
                    2330 or 2329 or 2202 or 2203 or 2204 or 2205 =>
                        [
                            await TypeDisplay.GetAsync(explanation.Source!, cancellation),
                        await TypeDisplay.GetAsync(explanation.Target!, cancellation)
                        ],
                    _ => throw new InvalidOperationException($"Unsupported relation explanation {explanation.Code}")
                }
            };
        if (explanation.Code == 2741 && explanation.Property!.Declarations.FirstOrDefault() is { } declaration)
            diagnostic = diagnostic with
            {
                RelatedInformation = [CheckerDiagnostic.Create(declaration, Messages.X_0_is_declared_here,
                TypeDisplay.SymbolName(explanation.Property))]
            };
        if (explanation.Code == 2326 && next is { MessageChain.Count: 1 } && next.MessageChain[0] is { Code: >= 2202 and <= 2205 } marker)
        {
            string name = PropertyPath(diagnostic.Arguments[0]);
            string path = marker.Code switch
            {
                2202 => name + "(...)",
                2203 => "new " + name + "(...)",
                2204 => name + "()",
                _ => "new " + name + "()"
            };
            return diagnostic with
            {
                Message = DiagnosticLocalization.GetMessage(2201),
                Arguments = [path],
                MessageChain = marker.MessageChain
            };
        }
        if (explanation.Code == 2326 && next is { MessageChain.Count: 1 } && next.MessageChain[0] is { Code: 2326 or 2200 } inner)
        {
            string head = PropertyPath(diagnostic.Arguments[0]), tail = PropertyPath(inner.Arguments[0]);
            diagnostic = diagnostic with
            {
                Message = DiagnosticLocalization.GetMessage(2200),
                Arguments = [head + (tail.StartsWith('[') ? "" : ".") + tail],
                MessageChain = inner.MessageChain
            };
        }
        return diagnostic;
    }

    private static string PropertyPath(string name) => name.Length != 0 && name[0] is '\'' or '"' or '`' ? "[" + name + "]" : name;

    private async ValueTask<Diagnostic> PrimitiveWrapperNoteAsync(
        Diagnostic diagnostic,
        Type source,
        Type target,
        CancellationToken cancellation)
    {
        string? name = target == context.StringType ? "String" : target == context.NumberType ? "Number"
            : target == context.BooleanType ? "Boolean" : target == context.ESSymbolType ? "Symbol" : null;
        if (name is null || source is not ObjectType || source != await program.Globals.GetAsync(name, 0, false, cancellation))
            return diagnostic;
        return diagnostic with
        {
            MessageChain = [diagnostic with
            {
                Message = Messages.X_0_is_a_primitive_but_1_is_a_wrapper_object_Prefer_using_0_when_possible,
                Arguments = [await TypeDisplay.GetAsync(target, cancellation), await TypeDisplay.GetAsync(source, cancellation)]
            }]
        };
    }

    private async ValueTask<(Type Source, Type Target)> RelationErrorTypesAsync(Type source, Type target, CancellationToken cancellation)
    {
        var normalizedSource = await Normalization.GetAsync(source, false, cancellation);
        var normalizedTarget = await Normalization.RelationTargetAsync(normalizedSource,
            await Normalization.GetAsync(target, true, cancellation), cancellation);
        bool preserveSource = source.Alias is not null
            || source is TypeReference sr && await Normalization.SingleBaseAsync(sr, cancellation) is not null;
        bool preserveTarget = target.Alias is not null
            || target is TypeReference tr && await Normalization.SingleBaseAsync(tr, cancellation) is not null;
        return (preserveSource ? source : normalizedSource, preserveTarget ? target : normalizedTarget);
    }

    private async ValueTask<Diagnostic> SourceConstraintNoteAsync(Diagnostic diagnostic, Type source, Type target,
        CancellationToken cancellation)
    {
        if (source is not TypeParameter parameter || parameter.Symbol?.Declarations.FirstOrDefault() is not { } declaration
            || await Instantiation.Constraints.ConstraintAsync(parameter, cancellation) is not null)
            return diagnostic;
        var synthetic = Instantiation.Engine.CloneParameter(parameter);
        synthetic.Constraint = await Instantiation.Engine.InstantiateAsync(target, TypeMapper.Create([parameter], [synthetic]),
            cancellation: cancellation);
        if (!await Instantiation.Constraints.HasNonCircularConstraintAsync(synthetic, cancellation))
            return diagnostic;
        var note = CheckerDiagnostic.Create(declaration, Messages.This_type_parameter_might_need_an_extends_0_constraint,
            await TypeDisplay.GetAsync(target, cancellation));
        var copies = new Dictionary<Diagnostic, Diagnostic>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(Diagnostic Node, bool Visited)>();
        pending.Push((diagnostic, false));
        while (pending.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested();
            if (copies.ContainsKey(item.Node))
                continue;
            if (!item.Visited)
            {
                pending.Push((item.Node, true));
                foreach (var child in item.Node.MessageChain)
                    pending.Push((child, false));
                continue;
            }
            copies[item.Node] = item.Node with
            {
                RelatedInformation = item.Node.RelatedInformation.Contains(note, DiagnosticEqualityComparer.Instance)
                    ? item.Node.RelatedInformation : [.. item.Node.RelatedInformation, note],
                MessageChain = item.Node.MessageChain.Select(child => copies[child]).ToArray()
            };
        }
        return copies[diagnostic];
    }

    private async ValueTask<Diagnostic> RelationMessageKindAsync(Diagnostic diagnostic, Type source, Type target,
        string sourceText, string targetText, CancellationToken cancellation)
    {
        if (diagnostic.Code != 2322)
            return diagnostic;
        if (sourceText == targetText)
            return diagnostic with { Message = DiagnosticLocalization.GetMessage(2719) };
        if (source is LiteralType { Value: string text } && target is UnionType union
            && await SymbolSuggestions.StringLiteralAsync(text, union, cancellation) is { } suggestion)
            return diagnostic with
            {
                Message = DiagnosticLocalization.GetMessage(2820),
                Arguments = [sourceText, targetText, await TypeDisplay.GetAsync(suggestion, cancellation)]
            };
        return diagnostic;
    }

    private async ValueTask<(string Source, string Target)> RelationTypeNamesAsync(Type source, Type target, CancellationToken cancellation)
    {
        async ValueTask<string> Name(Type type)
        {
            var enclosing = type.Symbol?.ValueDeclaration;
            if (enclosing is null || !QuerySyntax.Expression(enclosing) || program.IsContextSensitive(enclosing))
                enclosing = null;
            return await TypeDisplay.GetAsync(type, enclosing,
                TypeFormatFlags.AllowUniqueESSymbolType | TypeFormatFlags.UseAliasDefinedOutsideCurrentScope, cancellation);
        }
        string sourceText = await Name(source), targetText = await Name(target);
        if (sourceText == targetText)
        {
            sourceText = await TypeDisplay.GetAsync(source, NodeBuilderFlags.UseFullyQualifiedType, cancellation);
            targetText = await TypeDisplay.GetAsync(target, NodeBuilderFlags.UseFullyQualifiedType, cancellation);
        }
        return (sourceText, targetText);
    }

    private async ValueTask<Diagnostic> ConstraintReasonAsync(Diagnostic diagnostic, Type originalSource, Type source, Type target,
        string sourceText, string targetText, CancellationToken cancellation)
    {
        var targetFlags = target is IndexedAccessType indexed && originalSource is not IndexedAccessType
            ? indexed.ObjectType.Flags
            : target.Flags;
        if ((targetFlags & TypeFlags.TypeParameter) == 0 || target == context.VarianceCheckSuper || target == context.VarianceCheckSub)
            return diagnostic;
        var constraint = await Instantiation.Constraints.BaseConstraintAsync(target, cancellation);
        int code;
        string[] arguments;
        if (target is TypeParameter { IsDistributed: true, Constraint: { } distributed }
            && await Relations.RelatedAsync(source, distributed, RelationKind.Assignable, cancellation))
        {
            code = 5113;
            arguments = [sourceText, targetText];
        }
        else if (constraint is not null && await Relations.RelatedAsync(source, constraint, RelationKind.Assignable, cancellation))
        {
            code = 5075;
            arguments = [sourceText, targetText, await TypeDisplay.GetAsync(constraint, cancellation)];
        }
        else if (constraint is not null && await Relations.RelatedAsync(originalSource, constraint, RelationKind.Assignable, cancellation))
        {
            code = 5075;
            arguments =
                [
                    await TypeDisplay.GetAsync(originalSource, cancellation),
                    targetText,
                    await TypeDisplay.GetAsync(constraint, cancellation)
                ];
        }
        else
        {
            code = 5082;
            arguments = [targetText, sourceText];
        }
        var reason = diagnostic with
        {
            Message = DiagnosticLocalization.GetMessage(code),
            Arguments = arguments,
            MessageChain = code == 5082 ? [] : diagnostic.MessageChain,
            RelatedInformation = diagnostic.RelatedInformation
        };
        return diagnostic with { MessageChain = [reason] };
    }

    private async ValueTask<bool> CouldHaveSingletonTypesAsync(Type type, CancellationToken cancellation)
    {
        var pending = new Stack<Type>();
        var seen = new HashSet<Type>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current) || (current.Flags & TypeFlags.Boolean) != 0)
                continue;
            if (current is UnionOrIntersectionType composite)
            {
                foreach (var part in composite.Types)
                    pending.Push(part);
            }
            else if ((current.Flags & TypeFlags.Instantiable) != 0
                && await Instantiation.Constraints.ConstraintAsync(current, cancellation) is { } constraint && constraint != current)
                pending.Push(constraint);
            else if ((current.Flags & (TypeFlags.Unit | TypeFlags.TemplateLiteral | TypeFlags.StringMapping)) != 0)
                return true;
        }
        return false;
    }
}
