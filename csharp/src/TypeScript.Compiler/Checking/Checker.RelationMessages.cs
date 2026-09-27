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
        DiagnosticCode code,
        Type source,
        Type target,
        RelationKind kind,
        CancellationToken cancellation,
        Diagnostic? head = null,
        RelationExplanation? preparedExplanation = null)
    {
        if (code is not (DiagnosticCode.DecoratorFunctionReturnType0IsNotAssignableToType1
            or DiagnosticCode.DecoratorFunctionReturnTypeIs0ButIsExpectedToBeVoidOrAny
            or DiagnosticCode.Type0DoesNotSatisfyTheExpectedType1 or DiagnosticCode.Type0IsNotAssignableToType1
            or DiagnosticCode.Type0DoesNotSatisfyTheConstraint1 or DiagnosticCode.ArgumentOfType0IsNotAssignableToParameterOfType1
            or DiagnosticCode.ConversionOfType0ToType1MayBeAMistakeBecauseNeitherTypeSufficientlyOverlapsWithTheOtherIfThisWasIntentionalConvertTheExpressionToUnknownFirst
            or DiagnosticCode.Type0IsNotAssignableToType1WithExactOptionalPropertyTypesColonTrueConsiderAddingUndefinedToTheTypesOfTheTargetSProperties
            or DiagnosticCode.ArgumentOfType0IsNotAssignableToParameterOfType1WithExactOptionalPropertyTypesColonTrueConsiderAddingUndefinedToTheTypesOfTheTargetSProperties
            or DiagnosticCode.Type0IsNotAssignableToType1WithExactOptionalPropertyTypesColonTrueConsiderAddingUndefinedToTheTypeOfTheTarget
            or DiagnosticCode.Class0IncorrectlyExtendsBaseClass1 or DiagnosticCode.ClassStaticSide0IncorrectlyExtendsBaseClassStaticSide1
            or DiagnosticCode.TypeOfComputedPropertySValueIs0WhichIsNotAssignableToType1
            or DiagnosticCode.Interface0IncorrectlyExtendsInterface1
            or DiagnosticCode.Class0IncorrectlyImplementsInterface1 or DiagnosticCode.Type0IsNotAssignableToType1AsImpliedByVarianceAnnotation or DiagnosticCode.Type0IsNotComparableToType1 or DiagnosticCode.TheThisContextOfType0IsNotAssignableToMethodSThisOfType1
            or DiagnosticCode.Class0IncorrectlyImplementsClass1DidYouMeanToExtend1AndInheritItsMembersAsASubclass or DiagnosticCode.Type0IsMissingTheFollowingPropertiesFromType1Colon2
            or DiagnosticCode.Type0IsMissingTheFollowingPropertiesFromType1Colon2And3More
            or DiagnosticCode.Property0IsMissingInType1ButRequiredInType2
            or DiagnosticCode.ItsReturnType0IsNotAValidJSXElement or DiagnosticCode.ItsInstanceType0IsNotAValidJSXElement
            or DiagnosticCode.ItsElementType0IsNotAValidJSXElement or DiagnosticCode.Type0IsNotAssignableToType1AsRequiredForComputedEnumMemberValues or DiagnosticCode.ItsType0IsNotAValidJSXElementType or DiagnosticCode.TheInitializerOfAUsingDeclarationMustBeEitherAnObjectWithASymbolDisposeMethodOrBeNullOrUndefined or DiagnosticCode.TheInitializerOfAnAwaitUsingDeclarationMustBeEitherAnObjectWithASymbolAsyncDisposeOrSymbolDisposeMethodOrBeNullOrUndefined or DiagnosticCode.AnObjectSSymbolHasInstanceMethodMustReturnABooleanValueForItToBeUsedOnTheRightHandSideOfAnInstanceofExpression))
        {
            RelationError(node, code);
            return;
        }
        var originalTarget = target;
        (source, target) = await RelationErrorTypesAsync(source, target, cancellation);
        if (code == DiagnosticCode.ArgumentOfType0IsNotAssignableToParameterOfType1 && context.ExactOptionalPropertyTypes
            && (await RelationDiagnostics.ExactOptionalPropertiesAsync(source, target, cancellation)).Count != 0)
            code = DiagnosticCode.ArgumentOfType0IsNotAssignableToParameterOfType1WithExactOptionalPropertyTypesColonTrueConsiderAddingUndefinedToTheTypesOfTheTargetSProperties;
        var originalSource = source;
        var (sourceText, targetText) = await RelationTypeNamesAsync(source, target, cancellation);
        if ((target.Flags & TypeFlags.Never) == 0 && source.IsLiteral && !await CouldHaveSingletonTypesAsync(target, cancellation))
        {
            source = await Widening.LiteralBaseAsync(source, cancellation);
            sourceText = await TypeDisplay.GetAsync(source, NodeBuilderFlags.UseFullyQualifiedType, cancellation);
        }
        string[] arguments;
        if (code is DiagnosticCode.Type0IsMissingTheFollowingPropertiesFromType1Colon2
            or DiagnosticCode.Type0IsMissingTheFollowingPropertiesFromType1Colon2And3More
            or DiagnosticCode.Property0IsMissingInType1ButRequiredInType2
            && RequiredPropertyDeclarations.TryGetValue(node, out var missing))
        {
            if (code == DiagnosticCode.Property0IsMissingInType1ButRequiredInType2)
                arguments = [TypeDisplay.SymbolName(missing[0]), sourceText, targetText];
            else if (code == DiagnosticCode.Type0IsMissingTheFollowingPropertiesFromType1Colon2And3More)
                arguments = [sourceText, targetText, string.Join(", ", missing.Take(4).Select(TypeDisplay.SymbolName)),
                    (missing.Count - 4).ToString(CultureInfo.InvariantCulture)];
            else
                arguments = [sourceText, targetText, string.Join(", ", missing.Select(TypeDisplay.SymbolName))];
        }
        else
            arguments = [sourceText, targetText];
        var diagnostic = CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code), arguments);
        diagnostic = await PrimitiveWrapperNoteAsync(diagnostic, originalSource, target, cancellation);
        if (code == DiagnosticCode.Property0IsMissingInType1ButRequiredInType2
            && RequiredPropertyDeclarations.TryGetValue(node, out var required)
            && required[0].Declarations.FirstOrDefault() is { } declaration)
            diagnostic = diagnostic with
            {
                RelatedInformation = [CheckerDiagnostic.Create(declaration, Messages.X_0_is_declared_here,
                TypeDisplay.SymbolName(required[0]))]
            };
        if (code is not (DiagnosticCode.Type0IsMissingTheFollowingPropertiesFromType1Colon2
            or DiagnosticCode.Type0IsMissingTheFollowingPropertiesFromType1Colon2And3More
            or DiagnosticCode.Property0IsMissingInType1ButRequiredInType2))
        {
            var explanation = preparedExplanation ?? await Relations.ExplainAsync(originalSource, target, kind, cancellation);
            if (await RelationChainAsync(explanation?.Next, diagnostic, cancellation) is { } chain)
            {
                diagnostic = diagnostic with { MessageChain = [chain], RelatedInformation = chain.RelatedInformation };
                if (await OmitJsxRelationHeadAsync(originalSource, target, node, cancellation))
                {
                    Report(StripRelationMarkers(chain));
                    return;
                }
            }
            diagnostic = ObjectRelationNote(diagnostic, originalSource, target);
            diagnostic = await NeverIntersectionNoteAsync(diagnostic, originalTarget, cancellation);
            diagnostic = await ConstraintReasonAsync(diagnostic, originalSource, source, target, sourceText, targetText, cancellation);
        }
        else
            diagnostic = ObjectRelationNote(diagnostic, originalSource, target);
        diagnostic = await RelationMessageKindAsync(diagnostic, originalSource, target, sourceText, targetText, cancellation);
        diagnostic = SelectRelationDiagnostic(diagnostic, originalSource, target, sourceText, targetText);
        diagnostic = await SourceConstraintNoteAsync(diagnostic, originalSource, target, cancellation);
        if (code is not (DiagnosticCode.Type0IsNotAssignableToType1 or DiagnosticCode.Type0IsNotComparableToType1)
            && originalSource.Symbol is { } symbol
            && links.ExportTypes.TryGet(symbol) is { OriginatingImport: { } import, Target: { } imported }
            && import is not CallExpressionNode
            && await Relations.RelatedAsync(await Values.GetAsync(imported, cancellation), target, kind, cancellation))
        {
            var related = diagnostic.RelatedInformation.Append(CheckerDiagnostic.Create(import,
                Messages.Type_originates_at_this_import_A_namespace_style_import_cannot_be_called_or_constructed_and_will_cause_a_failure_at_runtime_Consider_using_a_default_import_or_import_require_here_instead)).ToArray();
            diagnostic = WithRelatedInformation(diagnostic, related);
        }
        Report(StripRelationMarkers(diagnostic));

        void Report(Diagnostic detail) => RelationError(node, head is null ? detail
            : head with { MessageChain = [detail], RelatedInformation = detail.RelatedInformation });
    }

    private async ValueTask<bool> OmitJsxRelationHeadAsync(Type source, Type target, SyntaxNode node, CancellationToken cancellation)
    {
        if ((source.ObjectFlags & ObjectFlags.JsxAttributes) == 0 || target is not IntersectionType intersection)
            return false;
        var intrinsic = await JsxTypeAsync("IntrinsicAttributes", node, cancellation);
        var classIntrinsic = await JsxTypeAsync("IntrinsicClassAttributes", node, cancellation);
        return intrinsic != context.ErrorType && classIntrinsic != context.ErrorType
            && (intersection.Types.Contains(intrinsic) || intersection.Types.Contains(classIntrinsic));
    }

    private bool ReadonlyAssignment(Type source, Type target) =>
        (source is TypeReference { Target: TupleType { IsReadonly: true } } || IsReadonlyArray(source))
        && (target is TypeReference { Target: TupleType { IsReadonly: false } } || IsArray(target) && !IsReadonlyArray(target));

    private Diagnostic SelectRelationDiagnostic(Diagnostic diagnostic, Type source, Type target, string sourceText, string targetText)
    {
        if (ReadonlyAssignment(source, target))
            return diagnostic with
            {
                Message = DiagnosticLocalization.GetMessage(DiagnosticCode.TheType0IsReadonlyAndCannotBeAssignedToTheMutableType1),
                Arguments = [sourceText, targetText],
                MessageChain = diagnostic.Code is DiagnosticCode.Type0IsMissingTheFollowingPropertiesFromType1Colon2
                    or DiagnosticCode.Type0IsMissingTheFollowingPropertiesFromType1Colon2And3More
                    or DiagnosticCode.Property0IsMissingInType1ButRequiredInType2
                    ? [diagnostic]
                    : diagnostic.MessageChain
            };
        if (diagnostic.MessageChain is not [var next])
            return diagnostic;
        bool matches = next.Code switch
        {
            DiagnosticCode.ObjectLiteralMayOnlySpecifyKnownPropertiesAnd0DoesNotExistInType1
                or DiagnosticCode.ObjectLiteralMayOnlySpecifyKnownPropertiesBut0DoesNotExistInType1DidYouMeanToWrite2 => true,
            DiagnosticCode.TheType0IsReadonlyAndCannotBeAssignedToTheMutableType1 => next.Arguments.SequenceEqual(new[]
            {
                sourceText,
                targetText
            }),
            DiagnosticCode.Type0HasNoPropertiesInCommonWithType1
                or DiagnosticCode.ValueOfType0HasNoPropertiesInCommonWithType1DidYouMeanToCallIt => true,
            DiagnosticCode.Property0IsMissingInType1ButRequiredInType2 when diagnostic.Code is not (DiagnosticCode.Class0IncorrectlyExtendsBaseClass1
                or DiagnosticCode.ClassStaticSide0IncorrectlyExtendsBaseClassStaticSide1
                or DiagnosticCode.Interface0IncorrectlyExtendsInterface1 or DiagnosticCode.Class0IncorrectlyImplementsInterface1
                or DiagnosticCode.Class0IncorrectlyImplementsClass1DidYouMeanToExtend1AndInheritItsMembersAsASubclass
                or DiagnosticCode.ConversionOfType0ToType1MayBeAMistakeBecauseNeitherTypeSufficientlyOverlapsWithTheOtherIfThisWasIntentionalConvertTheExpressionToUnknownFirst
                or DiagnosticCode.ItsReturnType0IsNotAValidJSXElement or DiagnosticCode.ItsInstanceType0IsNotAValidJSXElement
                or DiagnosticCode.ItsElementType0IsNotAValidJSXElement) => next.Arguments is [_, var s, var t]
                && s == sourceText
                && t == targetText,
            DiagnosticCode.Type0IsMissingTheFollowingPropertiesFromType1Colon2
                or DiagnosticCode.Type0IsMissingTheFollowingPropertiesFromType1Colon2And3More when diagnostic.Code is not (DiagnosticCode.Class0IncorrectlyExtendsBaseClass1
                    or DiagnosticCode.ClassStaticSide0IncorrectlyExtendsBaseClassStaticSide1
                    or DiagnosticCode.Interface0IncorrectlyExtendsInterface1 or DiagnosticCode.Class0IncorrectlyImplementsInterface1
                    or DiagnosticCode.Class0IncorrectlyImplementsClass1DidYouMeanToExtend1AndInheritItsMembersAsASubclass
                    or DiagnosticCode.ConversionOfType0ToType1MayBeAMistakeBecauseNeitherTypeSufficientlyOverlapsWithTheOtherIfThisWasIntentionalConvertTheExpressionToUnknownFirst
                    or DiagnosticCode.ItsReturnType0IsNotAValidJSXElement or DiagnosticCode.ItsInstanceType0IsNotAValidJSXElement
                    or DiagnosticCode.ItsElementType0IsNotAValidJSXElement) => next.Arguments.Length >= 2
                && next.Arguments[0] == sourceText
                && next.Arguments[1] == targetText,
            _ => false
        };
        return matches ? next : diagnostic;
    }

    private Diagnostic ObjectRelationNote(Diagnostic diagnostic, Type source, Type target)
        => source == GlobalObject && (target.Flags & TypeFlags.Primitive) == 0 ? diagnostic with
        {
            MessageChain = [diagnostic with
        {
            Message = DiagnosticLocalization.GetMessage(
                DiagnosticCode.TheObjectTypeIsAssignableToVeryFewOtherTypesDidYouMeanToUseTheAnyTypeInstead),
            Arguments = []
        }]
        } : diagnostic;

    private async ValueTask<Diagnostic> NeverIntersectionNoteAsync(Diagnostic diagnostic, Type target, CancellationToken cancellation)
    {
        if (target is not IntersectionType intersection || (target.ObjectFlags & ObjectFlags.IsNeverIntersection) == 0)
            return diagnostic;
        Symbol? conflict = null;
        DiagnosticCode code = DiagnosticCode.TheIntersection0WasReducedToNeverBecauseProperty1HasConflictingTypesInSomeConstituents;
        var properties = await Properties.CompositePropertiesAsync(intersection, cancellation);
        foreach (var property in properties)
            if (property.ValueDeclaration is not null && await Views.NeverPropertyAsync(property, cancellation))
            {
                conflict = property;
                break;
            }
        if (conflict is null)
        {
            conflict = properties.FirstOrDefault(
                p => p.ValueDeclaration is null && (p.CheckFlags & Binding.CheckFlags.ContainsPrivate) != 0);
            code = DiagnosticCode.TheIntersection0WasReducedToNeverBecauseProperty1ExistsInMultipleConstituentsAndIsPrivateInSome;
        }
        return conflict is null ? diagnostic : diagnostic with
        {
            MessageChain = [diagnostic with { Message = DiagnosticLocalization.GetMessage(code),
            Arguments =
                [
                    await TypeDisplay.GetAsync(target, NodeBuilderFlags.NoTypeReduction, cancellation),
                    TypeDisplay.SymbolName(conflict)
                ] }]
        };
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
        CancellationToken cancellation,
        bool suppressRelatedInformation = false)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        if (explanation is null)
            return null;
        suppressRelatedInformation |= explanation.SuppressRelatedInformation;
        var next = await RelationChainAsync(explanation.Next, location, cancellation, suppressRelatedInformation);
        if (explanation.Code == DiagnosticCode.TypesOfProperty0AreIncompatible
            && next?.Code is DiagnosticCode.ObjectLiteralMayOnlySpecifyKnownPropertiesAnd0DoesNotExistInType1
                or DiagnosticCode.ObjectLiteralMayOnlySpecifyKnownPropertiesBut0DoesNotExistInType1DidYouMeanToWrite2)
            return next;
        if (explanation.Code == DiagnosticCode.ObjectLiteralMayOnlySpecifyKnownPropertiesAnd0DoesNotExistInType1)
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
                Message = DiagnosticLocalization.GetMessage(
                    suggestion is null
                        ? DiagnosticCode.ObjectLiteralMayOnlySpecifyKnownPropertiesAnd0DoesNotExistInType1
                        : DiagnosticCode.ObjectLiteralMayOnlySpecifyKnownPropertiesBut0DoesNotExistInType1DidYouMeanToWrite2),
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
        if (explanation.Code is DiagnosticCode.Type0IsNotAssignableToType1 or DiagnosticCode.Type0IsNotComparableToType1)
        {
            var (source, target) = await RelationErrorTypesAsync(explanation.Source!, explanation.Target!, cancellation);
            if (next is not null && DiagnosticNode is { } node && await OmitJsxRelationHeadAsync(source, target, node, cancellation))
                return next;
            var originalSource = source;
            var (sourceText, targetText) = await RelationTypeNamesAsync(source, target, cancellation);
            if ((target.Flags & TypeFlags.Never) == 0 && source.IsLiteral && !await CouldHaveSingletonTypesAsync(target, cancellation))
            {
                source = await Widening.LiteralBaseAsync(source, cancellation);
                sourceText = await TypeDisplay.GetAsync(source, NodeBuilderFlags.UseFullyQualifiedType, cancellation);
            }
            diagnostic = diagnostic with { Arguments = [sourceText, targetText] };
            diagnostic = await PrimitiveWrapperNoteAsync(diagnostic, originalSource, target, cancellation);
            diagnostic = ObjectRelationNote(diagnostic, originalSource, target);
            diagnostic = await NeverIntersectionNoteAsync(diagnostic, explanation.Target!, cancellation);
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
                    DiagnosticCode.Property0IsMissingInType1ButRequiredInType2 => [TypeDisplay.SymbolName(explanation.Property!), await TypeDisplay.GetAsync(
                        explanation.Source!,
                        cancellation),
                        await TypeDisplay.GetAsync(explanation.Target!, cancellation)],
                    DiagnosticCode.TypesOfProperty0AreIncompatible or DiagnosticCode.Property0IsIncompatibleWithIndexSignature => [TypeDisplay.SymbolName(explanation.Property!)],
                    DiagnosticCode.X0IndexSignaturesAreIncompatible => [await TypeDisplay.GetAsync(explanation.Source!, cancellation)],
                    DiagnosticCode.CannotAssignAnAbstractConstructorTypeToANonAbstractConstructorType
                        or DiagnosticCode.AThisBasedTypeGuardIsNotCompatibleWithAParameterBasedTypeGuard
                        or DiagnosticCode.TheThisTypesOfEachSignatureAreIncompatible => [],
                    DiagnosticCode.X0And1IndexSignaturesAreIncompatible or DiagnosticCode.IndexSignatureForType0IsMissingInType1
                        or DiagnosticCode.CallSignatureReturnTypes0And1AreIncompatible
                        or DiagnosticCode.ConstructSignatureReturnTypes0And1AreIncompatible
                        or DiagnosticCode.CallSignaturesWithNoArgumentsHaveIncompatibleReturnTypes0And1
                        or DiagnosticCode.ConstructSignaturesWithNoArgumentsHaveIncompatibleReturnTypes0And1 =>
                        [
                            await TypeDisplay.GetAsync(explanation.Source!, cancellation),
                        await TypeDisplay.GetAsync(explanation.Target!, cancellation)
                        ],
                    _ => throw new InvalidOperationException($"Unsupported relation explanation {explanation.Code}")
                }
            };
        if (explanation.Code == DiagnosticCode.Property0IsMissingInType1ButRequiredInType2)
        {
            var (sourceText, targetText) = await RelationTypeNamesAsync(explanation.Source!, explanation.Target!, cancellation);
            diagnostic = diagnostic with { Arguments = [TypeDisplay.SymbolName(explanation.Property!), sourceText, targetText] };
        }
        if (!suppressRelatedInformation
            && explanation.Code == DiagnosticCode.Property0IsMissingInType1ButRequiredInType2
            && explanation.Property!.Declarations.FirstOrDefault() is { } declaration)
            diagnostic = diagnostic with
            {
                RelatedInformation = [CheckerDiagnostic.Create(declaration, Messages.X_0_is_declared_here,
                TypeDisplay.SymbolName(explanation.Property))]
            };
        if (explanation.Code == DiagnosticCode.TypesOfProperty0AreIncompatible
            && next is { MessageChain.Count: 1 }
            && next.MessageChain[0] is
            {
                Code: >= DiagnosticCode.CallSignatureReturnTypes0And1AreIncompatible
                and <= DiagnosticCode.ConstructSignaturesWithNoArgumentsHaveIncompatibleReturnTypes0And1
            } marker)
        {
            string name = PropertyPath(diagnostic.Arguments[0]);
            string path = marker.Code switch
            {
                DiagnosticCode.CallSignatureReturnTypes0And1AreIncompatible => name + "(...)",
                DiagnosticCode.ConstructSignatureReturnTypes0And1AreIncompatible => "new " + name + "(...)",
                DiagnosticCode.CallSignaturesWithNoArgumentsHaveIncompatibleReturnTypes0And1 => name + "()",
                _ => "new " + name + "()"
            };
            diagnostic = diagnostic with
            {
                Message = DiagnosticLocalization.GetMessage(DiagnosticCode.TheTypesReturnedBy0AreIncompatibleBetweenTheseTypes),
                Arguments = [path],
                MessageChain = marker.MessageChain
            };
            next = diagnostic.MessageChain.Count == 1 ? diagnostic.MessageChain[0] : null;
        }
        if (explanation.Code == DiagnosticCode.TypesOfProperty0AreIncompatible
            && next is { MessageChain.Count: 1 }
            && next.MessageChain[0] is
            {
                Code: DiagnosticCode.TypesOfProperty0AreIncompatible
                or DiagnosticCode.TheTypesOf0AreIncompatibleBetweenTheseTypes
                or DiagnosticCode.TheTypesReturnedBy0AreIncompatibleBetweenTheseTypes
            } inner)
        {
            string head = PropertyPath(diagnostic.Arguments[0]), tail = PropertyPath(inner.Arguments[0]);
            if (head.StartsWith("new ", StringComparison.Ordinal))
                head = "(" + head + ")";
            int pos = 0;
            while (pos < tail.Length)
            {
                if (tail[pos] == '(')
                    pos++;
                else if (tail.AsSpan(pos).StartsWith("new ", StringComparison.Ordinal))
                    pos += 4;
                else
                    break;
            }
            diagnostic = diagnostic with
            {
                Message = DiagnosticLocalization.GetMessage(
                    diagnostic.Code == DiagnosticCode.TypesOfProperty0AreIncompatible
                        ? DiagnosticCode.TheTypesOf0AreIncompatibleBetweenTheseTypes
                        : diagnostic.Code),
                Arguments = [tail[..pos] + head + (tail.AsSpan(pos).StartsWith("[", StringComparison.Ordinal) ? "" : ".") + tail[pos..]],
                MessageChain = inner.MessageChain
            };
        }
        return diagnostic;
    }

    private static string PropertyPath(string name) => name.Length != 0 && name[0] is '\'' or '"' or '`' ? "[" + name + "]" : name;

    private static Diagnostic WithRelatedInformation(Diagnostic diagnostic, IReadOnlyList<Diagnostic> related)
    {
        var chain = new Stack<Diagnostic>();
        while (diagnostic.MessageChain is [var child])
        {
            chain.Push(diagnostic);
            diagnostic = child;
        }
        diagnostic = diagnostic with { RelatedInformation = related };
        while (chain.TryPop(out var parent))
            diagnostic = parent with { RelatedInformation = related, MessageChain = [diagnostic] };
        return diagnostic;
    }

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
        if (diagnostic.Code != DiagnosticCode.Type0IsNotAssignableToType1)
            return diagnostic;
        if (sourceText == targetText)
            return diagnostic with
            {
                Message = DiagnosticLocalization.GetMessage(
                DiagnosticCode.Type0IsNotAssignableToType1TwoDifferentTypesWithThisNameExistButTheyAreUnrelated)
            };
        if (source is LiteralType { Value: string text } && target is UnionType union
            && await SymbolSuggestions.StringLiteralAsync(text, union, cancellation) is { } suggestion)
            return diagnostic with
            {
                Message = DiagnosticLocalization.GetMessage(DiagnosticCode.Type0IsNotAssignableToType1DidYouMean2),
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
        DiagnosticCode code;
        string[] arguments;
        if (target is TypeParameter { IsDistributed: true, Constraint: { } distributed }
            && await Relations.RelatedAsync(source, distributed, RelationKind.Assignable, cancellation))
        {
            code = DiagnosticCode.X0IsOnlyAssignableToTheNonDistributed1But1HasBeenDistributedHere;
            arguments = [sourceText, targetText];
        }
        else if (constraint is not null && await Relations.RelatedAsync(source, constraint, RelationKind.Assignable, cancellation))
        {
            code = DiagnosticCode.X0IsAssignableToTheConstraintOfType1But1CouldBeInstantiatedWithADifferentSubtypeOfConstraint2;
            arguments = [sourceText, targetText, await TypeDisplay.GetAsync(constraint, cancellation)];
        }
        else if (constraint is not null && await Relations.RelatedAsync(originalSource, constraint, RelationKind.Assignable, cancellation))
        {
            code = DiagnosticCode.X0IsAssignableToTheConstraintOfType1But1CouldBeInstantiatedWithADifferentSubtypeOfConstraint2;
            arguments =
                [
                    await TypeDisplay.GetAsync(originalSource, cancellation),
                    targetText,
                    await TypeDisplay.GetAsync(constraint, cancellation)
                ];
        }
        else
        {
            code = DiagnosticCode.X0CouldBeInstantiatedWithAnArbitraryTypeWhichCouldBeUnrelatedTo1;
            arguments = [targetText, sourceText];
        }
        var reason = diagnostic with
        {
            Message = DiagnosticLocalization.GetMessage(code),
            Arguments = arguments,
            MessageChain = code == DiagnosticCode.X0CouldBeInstantiatedWithAnArbitraryTypeWhichCouldBeUnrelatedTo1
                ? []
                : diagnostic.MessageChain,
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
