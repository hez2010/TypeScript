using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using S = TypeScript.Compiler.Binding.SymbolFlags;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial class LanguageServiceDocument
{
    private sealed partial class CompletionQuery
    {
        private readonly HashSet<Symbol> seenProperties = [];
        private async ValueTask<bool> MembersAsync()
        {
            kind = CompletionKind.PropertyAccess;
            var parent = contextToken!.Parent;
            SyntaxNode? node = parent switch
            {
                PropertyAccessExpressionNode property => (propertyAccess = property).Expression,
                QualifiedNameNode qualified => qualified.Left,
                ModuleDeclarationNode module => module.Name,
                ImportTypeNode => parent,
                MetaPropertyNode => await SyntaxNavigation.FirstTokenAsync(parent, File, cancellation), _ => null,
            };
            if (node is null) return false;
            if (propertyAccess is not null)
            {
                var left = node;
                while (left is PropertyAccessExpressionNode property) left = property.Expression!;
                if (left.Pos == left.End || (node is CallExpressionNode || Signatures.FunctionLike(node)) && node.End == contextToken.Pos
                    && (await SyntaxNavigation.FindPrecedingTokenAsync(File, node.End, node, cancellation: cancellation))?.Kind != K.CloseParenToken) return false;
            }
            bool importType = node is ImportTypeNode;
            bool typeLocation = node is ImportTypeNode { IsTypeOf: false } || node.Parent is { } owner && QuerySyntax.PartOfType(owner) || await PossiblyTypeArgumentAsync(contextToken);
            bool internalImport = InternalImportRightSide(node);
            if (node is IdentifierNode or QualifiedNameNode or PropertyAccessExpressionNode or ImportTypeNode)
            {
                bool namespaceName = node.Parent is ModuleDeclarationNode;
                if (namespaceName) { newIdentifier = true; commitCharacters = []; }
                if (await checker.GetSymbolAtLocationAsync(node, cancellation) is { } original)
                {
                    var symbol = await SkipAliasAsync(original);
                    if ((symbol.Flags & (S.Module | S.Enum)) != 0)
                    {
                        var access = importType ? node : node.Parent!;
                        Type? receiver = null;
                        foreach (var exported in await checker.GetExportsOfModuleAsync(symbol, cancellation))
                        {
                            bool valid;
                            if (namespaceName) valid = (exported.Flags & S.Namespace) != 0 && !exported.Declarations.All(declaration => declaration.Parent == node.Parent);
                            else if (typeLocation || insideJSDocType) valid = await TypeReferenceableAsync(exported);
                            else
                            {
                                valid = internalImport && await TypeReferenceableAsync(exported);
                                if (!valid)
                                {
                                    receiver ??= await checker.GetCompletionAccessTypeAsync(access, cancellation);
                                    valid = (receiver.Flags & TypeFlags.Any) != 0 || await checker.Properties.PropertyAsync(receiver, exported.Name, cancellation: cancellation) is { } property
                                        && await checker.IsCompletionPropertyAccessibleAsync(access, receiver, property, cancellation);
                                }
                            }
                            if (valid) symbols.Add(new(exported));
                        }
                        if (!typeLocation && !insideJSDocType && symbol.Declarations.Any(declaration => declaration is not (SourceFileNode or ModuleDeclarationNode or EnumDeclarationNode)))
                            await AddReceiverAsync(checker.Optional.RemoveMarker(await checker.GetTypeOfSymbolAtLocationAsync(symbol, node, cancellation)), node, false);
                        return true;
                    }
                }
            }
            if (!typeLocation || Ancestor(node, candidate => candidate is TypeQueryNode) is not null)
            {
                await checker.GetCompletionThisTypeAsync(node, null, cancellation);
                await AddReceiverAsync(checker.Optional.RemoveMarker(await checker.GetTypeAtLocationAsync(node, cancellation)), node, typeLocation);
            }
            return true;
        }

        private async ValueTask AddReceiverAsync(Type type, SyntaxNode node, bool typeLocation)
        {
            bool nullable = false;
            if (typeLocation) type = await checker.NonNullableAsync(type, cancellation);
            else if (await checker.Facts.GetAsync(type, TypeFacts.IsUndefinedOrNull, cancellation) != 0)
            {
                bool correct = contextToken!.Kind == K.DotToken && preferences.IncludeAutomaticOptionalChainCompletions != false;
                if (correct || contextToken.Kind == K.QuestionDotToken)
                { type = await checker.NonNullableAsync(type, cancellation); nullable = correct; }
            }
            if ((await checker.IndexesAsync(type, cancellation)).Any(index => (index.KeyType.Flags & TypeFlags.String) != 0))
            { newIdentifier = true; commitCharacters = []; }
            if (contextToken!.Kind == K.QuestionDotToken && (await checker.SignaturesAsync(type, false, cancellation)).Count > 0)
            { newIdentifier = true; commitCharacters ??= AllCommitCharacters; }
            foreach (var symbol in Checked ? await checker.GetApparentPropertiesAsync(type, cancellation) : await checker.GetPossibleCompletionPropertiesAsync(type, cancellation))
                if (await checker.IsCompletionPropertyAccessibleAsync(node is ImportTypeNode ? node : node.Parent!, type, symbol, cancellation))
                {
                    if (Checked) await AddPropertyAsync(symbol, false, nullable);
                    else symbols.Add(new(symbol));
                }
            if (!typeLocation && (node.Flags & NodeFlags.AwaitContext) != 0 && await checker.GetCompletionPromiseTypeAsync(type, cancellation) is { } promise)
                foreach (var symbol in await checker.GetApparentPropertiesAsync(promise, cancellation))
                    if (await checker.IsCompletionPropertyAccessibleAsync(node is ImportTypeNode ? node : node.Parent!, promise, symbol, cancellation))
                        await AddPropertyAsync(symbol, true, nullable);
        }

        private async ValueTask AddPropertyAsync(Symbol symbol, bool awaited, bool nullable)
        {
            var computed = symbol.Declarations.Select(declaration => declaration.DeclarationName).OfType<ComputedPropertyNameNode>().FirstOrDefault();
            if (computed is not null)
            {
                var left = computed.Expression;
                while (left is PropertyAccessExpressionNode access) left = access.Expression;
                var nameSymbol = left is IdentifierNode ? await checker.GetSymbolAtLocationAsync(left, cancellation) : null;
                var accessible = nameSymbol is null ? null : await FirstSymbolInChainAsync(nameSymbol, contextToken!);
                if (accessible is not null)
                {
                    if (!seenProperties.Add(accessible)) return;
                    bool external = accessible.Parent is { } module && module.Declarations.Any(declaration => declaration is SourceFileNode)
                        && (await checker.GetCompletionModuleExportsAsync(module, cancellation)).FirstOrDefault(member => member.Name == accessible.Name) == accessible;
                    symbols.Add(new(accessible, "15"u8, Nullable: !external && nullable, SymbolMember: !external));
                    return;
                }
            }
            bool insertAwait = awaited && seenProperties.Add(symbol);
            bool staticProperty = SemanticSyntax.IsStatic(symbol.ValueDeclaration ?? File) && SemanticSyntax.ClassLike(symbol.ValueDeclaration?.Parent);
            if (staticProperty)
                for (int i = 0; i < symbols.Count; i++) if (symbols[i].Symbol == symbol) symbols[i] = symbols[i] with { SortText = "10"u8 };
            symbols.Add(new(symbol, staticProperty ? "10"u8 : "11"u8,
                Nullable: nullable, Await: insertAwait));
        }

        private async ValueTask<bool> TypeReferenceableAsync(Symbol symbol)
        {
            Stack<Symbol> pending = []; pending.Push(symbol);
            HashSet<Symbol> seen = [];
            while (pending.TryPop(out var currentSymbol))
            {
                cancellation.ThrowIfCancellationRequested();
                if ((currentSymbol.Flags & S.Type) != 0 || checker.IsUnknownSymbol(currentSymbol)) return true;
                if (!seen.Add(currentSymbol)) continue;
                var target = await SkipAliasAsync(currentSymbol.ExportSymbol ?? currentSymbol);
                if (target != currentSymbol) pending.Push(target);
                if ((currentSymbol.Flags & S.Module) != 0)
                    foreach (var exported in await checker.GetExportsOfModuleAsync(currentSymbol, cancellation)) pending.Push(exported);
            }
            return false;
        }

        private async ValueTask<bool> IncludeAsync(Symbol symbol, SyntaxNode? closest)
        {
            if (location.Parent is ExportAssignmentNode) return true;
            if (closest is VariableDeclarationNode && symbol.ValueDeclaration == closest) return false;
            var declaration = symbol.ValueDeclaration ?? symbol.Declarations.FirstOrDefault();
            if (closest is ParameterDeclarationNode && declaration is ParameterDeclarationNode && closest.Parent is IFunctionSignature { Parameters: { } parameters }
                && declaration.Pos >= closest.Pos && declaration.Pos < parameters.End) return false;
            if (closest is TypeParameterDeclarationNode && declaration is TypeParameterDeclarationNode)
            {
                if (closest == declaration && contextToken?.Kind == K.ExtendsKeyword) return false;
                if (InTypeParameterDefault(contextToken) && closest.Parent is not InferTypeNode
                    && TypeParameters(closest.Parent) is { } typeParameters && declaration.Pos >= closest.Pos && declaration.Pos < typeParameters.End) return false;
            }
            var origin = await SkipAliasAsync(symbol);
            if (File.ExternalModuleIndicator is not null && options.AllowUmdGlobalAccess != true && symbol != origin
                && !symbol.Declarations.Any(d => SemanticSyntax.Source(d) == File) && symbol.Parent is { } parent
                && parent.Declarations.Any(d => d is SourceFileNode)) return false;
            var flags = symbol.Flags | origin.Flags | (origin.ExportSymbol?.Flags ?? 0);
            if ((symbol.Flags & S.Alias) != 0) flags |= await checker.GetCompletionSymbolFlagsAsync(symbol, cancellation);
            return InternalImportRightSide(location) ? (flags & S.Namespace) != 0 : typeOnly ? await TypeReferenceableAsync(symbol) : (flags & S.Value) != 0;
        }

        private ValueTask<Symbol> SkipAliasAsync(Symbol symbol) => (symbol.Flags & S.Alias) != 0 ? checker.GetAliasedSymbolAsync(symbol, cancellation) : new(symbol);

        private static SyntaxNode? ClosestDeclaration(SyntaxNode? context, SyntaxNode location)
        {
            for (var node = context; node is not null && !Stops(node); node = node.Parent)
                if (node is ParameterDeclarationNode or TypeParameterDeclarationNode && node.Parent is not IndexSignatureDeclarationNode) return node;
            for (var node = location; node is not null && !Stops(node); node = node.Parent)
                if (node is VariableDeclarationNode) return node;
            return null;

            static bool Stops(SyntaxNode node) => node is BindingPatternNode || node is BlockNode && node.Parent is { } function && SemanticSyntax.Body(function) == node
                || node.Parent is ArrowFunctionNode arrow && (arrow.Body == node || node.Kind == K.EqualsGreaterThanToken);
        }

        private static bool InTypeParameterDefault(SyntaxNode? token)
        {
            for (var node = token; node?.Parent is { } parent; node = parent)
                if (parent is TypeParameterDeclarationNode parameter) return parameter.DefaultType == node || node.Kind == K.EqualsToken;
            return false;
        }

        private static bool InternalImportRightSide(SyntaxNode node)
        {
            while (node.Parent is QualifiedNameNode) node = node.Parent;
            return node.Parent is ImportEqualsDeclarationNode declaration && declaration.ModuleReference == node;
        }

        private static NodeList? TypeParameters(SyntaxNode? node) => node switch
        {
            IFunctionSignature function => function.TypeParameters, ClassDeclarationNode declaration => declaration.TypeParameters,
            ClassExpressionNode declaration => declaration.TypeParameters, InterfaceDeclarationNode declaration => declaration.TypeParameters,
            TypeAliasDeclarationNode declaration => declaration.TypeParameters, _ => null,
        };
    }
}
