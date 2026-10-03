using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using S = TypeScript.Compiler.Binding.SymbolFlags;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compiler.LanguageServices;

internal static class SymbolDisplay
{
    internal const TypeFormatFlags TypeFlags = TypeFormatFlags.UseAliasDefinedOutsideCurrentScope | TypeFormatFlags.UseInstantiationExpressions;
    internal const SymbolFormatFlags SymbolFlags = SymbolFormatFlags.WriteTypeParametersOrArguments | SymbolFormatFlags.UseOnlyExternalAliasing
        | SymbolFormatFlags.AllowAnyNodeKind | SymbolFormatFlags.UseAliasDefinedOutsideCurrentScope;

    internal static async ValueTask<(Utf8String Text, SyntaxNode? Declaration, IReadOnlyList<ClassifiedTextRun> Runs)> QuickInfoAsync(Checker checker, Symbol? symbol,
        SyntaxNode node, HoverVerbosity verbosity, bool classified, CancellationToken cancellation, S? symbolMeaning = null)
    {
        var container = Container(node);
        var text = new DisplayParts(classified);
        SyntaxNode? declaration = null;
        var aliases = new HashSet<Symbol>();
        int aliasLevel = 0;
        bool symbolExpanded = false;
        async ValueTask<bool> ExpandSymbol(Symbol value, S flags)
        {
            if (symbolExpanded) return true;
            if ((value.Flags & (S.Class | S.Interface | S.Namespace)) == 0) return false;
            var type = (value.Flags & (S.Class | S.Interface)) != 0 ? await checker.Declared.GetAsync(value, cancellation)
                : await checker.GetTypeOfSymbolAtLocationAsync(value, node, cancellation);
            if (checker.IsLibraryTypeForHover(type)) return false;
            if (verbosity.Level <= 0) { verbosity.CanIncrease = true; return false; }
            var expanded = new HoverVerbosity(verbosity.Level - 1, verbosity.MaximumLength);
            var display = await checker.ExpandHoverSymbolAsync(value, flags, expanded, cancellation);
            if (display.IsEmpty) return false;
            verbosity.Include(expanded); text.Append(display); symbolExpanded = true; return true;
        }
        void SetDeclaration(SyntaxNode? value) => declaration ??= value;
        void NewLine()
        {
            if (text.Length != 0) text.Append("\n"u8);
            if (aliasLevel != 0) text.Label("alias"u8);
        }
        async ValueTask WriteType(Type type, SyntaxNode? enclosing, TypeFormatFlags flags = TypeFlags)
        {
            flags |= TypeFormatFlags.MultilineObjectLiterals;
            if (classified) text.Copy(await checker.GetClassifiedTypeDisplayAsync(type, enclosing, flags, cancellation));
            else text.Append(await checker.GetHoverTypeDisplayAsync(type, enclosing, flags, verbosity, cancellation));
        }
        async ValueTask WriteSymbolName(Symbol value, SyntaxNode? enclosing) =>
            text.Symbol(await checker.GetSymbolDisplayNameAsync(value, enclosing, S.None, SymbolFlags, cancellation), value);
        async ValueTask WriteSignature(Signature signature, TypeFormatFlags flags)
        {
            flags |= TypeFormatFlags.MultilineObjectLiterals;
            if (classified) text.Copy(await checker.GetClassifiedSignatureDisplayAsync(signature, container, flags, cancellation));
            else text.Append(await checker.GetHoverSignatureDisplayAsync(signature, container, flags, verbosity, cancellation));
        }
        async ValueTask WriteParameters(IReadOnlyList<Type> parameters)
        {
            if (parameters.Count == 0) return;
            text.Punctuation("<"u8);
            for (int i = 0; i < parameters.Count; i++)
            {
                if (i != 0) text.Punctuation(", "u8);
                var parameter = (TypeParameter)parameters[i];
                await WriteSymbolName(parameter.Symbol!, null);
                if (await checker.Instantiation.Constraints.ConstraintAsync(parameter, cancellation) is { } constraint)
                { text.Keyword(" extends "u8); await WriteType(constraint, null); }
                if (await checker.Instantiation.Constraints.DefaultAsync(parameter, cancellation) is { } value)
                { text.Operator(" = "u8); await WriteType(value, null); }
            }
            text.Punctuation(">"u8);
        }
        async ValueTask WriteSignatures(IReadOnlyList<Signature> signatures, Utf8String prefix, bool parenthesized, Symbol value)
        {
            for (int i = 0; i < signatures.Count; i++)
            {
                NewLine();
                if (i == 3 && signatures.Count >= 5)
                { text.Append("// +"u8).Append(Utf8String.Format(signatures.Count - 3)).Append(" more overloads"u8); break; }
                if (parenthesized) text.Label(prefix); else text.Keyword(prefix);
                await WriteSymbolName(value, container);
                if ((value.Flags & S.Optional) != 0) text.Punctuation("?"u8);
                await WriteSignature(signatures[i], TypeFlags | TypeFormatFlags.WriteCallStyleSignature | TypeFormatFlags.WriteTypeArgumentsOfSignature);
            }
        }
        if (node.Kind == K.ThisKeyword && QuerySyntax.Expression(node) || IsThisInTypeQuery(node))
        {
            text.Keyword("this"u8).Punctuation(": "u8); await WriteType(await checker.GetTypeAtLocationAsync(node, cancellation), container);
            return (text.ToUtf8String(), null, text.Runs);
        }
        if (symbol is null)
        {
            if (ShouldGetType(node)) await WriteType(await checker.GetTypeAtLocationAsync(node, cancellation), container);
            return (text.ToUtf8String(), null, text.Runs);
        }
        var meaning = symbolMeaning ?? Meaning(node);
        async ValueTask WriteSymbol(Symbol value)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!RuntimeHelpers.TryEnsureSufficientExecutionStack()) await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            if ((value.Flags & S.Alias) != 0 && aliases.Add(value) && await checker.GetAliasedSymbolAsync(value, cancellation) is { } target
                && target != checker.UnknownSymbol)
            { aliasLevel++; await WriteSymbol(target); aliasLevel--; }
            var flags = value.Flags & meaning;
            if (flags == 0)
            {
                if (aliasLevel != 0 || text.Length != 0) return;
                flags = value.Flags & (S.Value | S.Signature | S.Type | S.Namespace);
                if (flags == 0) return;
            }
            if ((flags & S.Property) != 0 && value.ValueDeclaration is MethodDeclarationNode) flags = S.Method;
            if ((flags & (S.Variable | S.Property | S.Accessor)) != 0)
            {
                NewLine();
                if ((value.CheckFlags & CheckFlags.IndexSymbol) == 0)
                {
                    if ((flags & S.Property) != 0) text.Label("property"u8);
                    else if ((flags & S.Accessor) != 0) text.Label("accessor"u8);
                    else if (value.ValueDeclaration is { } owner)
                    {
                        owner = SemanticSyntax.RootDeclaration(owner);
                        var variableFlags = owner is VariableDeclarationNode ? owner.Parent?.Flags ?? 0 : owner.Flags;
                        if (owner is ParameterDeclarationNode) text.Label("parameter"u8);
                        else if ((variableFlags & NodeFlags.AwaitUsing) == NodeFlags.AwaitUsing) text.Keyword("await "u8).Keyword("using "u8);
                        else text.Keyword((variableFlags & NodeFlags.Let) != 0 ? "let "u8 : (variableFlags & NodeFlags.Const) != 0 ? "const "u8
                            : (variableFlags & NodeFlags.Using) != 0 ? "using "u8 : "var "u8);
                    }
                    if (value.Name == "export="u8 && value.Parent is { } parent && (parent.Flags & S.Module) != 0) text.Append("exports"u8);
                    else await WriteSymbolName(value, container);
                    if ((value.Flags & S.Optional) != 0) text.Punctuation("?"u8);
                    text.Punctuation(": "u8);
                }
                if (CallOrNew(node) is { } call)
                    await WriteSignature(await checker.GetResolvedSignatureAsync(call, cancellation), TypeFlags | TypeFormatFlags.WriteTypeArgumentsOfSignature
                        | TypeFormatFlags.WriteArrowStyleSignature | (call is CallExpressionNode ? TypeFormatFlags.WriteCallStyleSignature : 0));
                else
                {
                    var type = await checker.GetTypeOfSymbolAtLocationAsync(value, node, cancellation);
                    if (type is TypeParameter parameter && await checker.Instantiation.Constraints.ConstraintAsync(type, cancellation) is not null)
                    {
                        if (verbosity.Level > 0)
                        {
                            var expanded = new HoverVerbosity(verbosity.Level - 1, verbosity.MaximumLength);
                            text.Append(await checker.GetHoverTypeParameterDisplayAsync(parameter, container, expanded, cancellation));
                            verbosity.Include(expanded);
                        }
                        else { await WriteType(type, container); verbosity.CanIncrease = true; }
                    }
                    else await WriteType(type, container);
                }
                SetDeclaration(value.ValueDeclaration ?? value.Declarations.FirstOrDefault());
            }
            if ((flags & S.EnumMember) != 0)
            {
                NewLine(); text.Label("enum member"u8);
                var type = await checker.Values.GetAsync(value, cancellation);
                await WriteType(type, container);
                if (type is LiteralType literal)
                {
                    text.Operator(" = "u8).Literal(literal.Value switch
                    {
                        Utf8String str => Checker.QuoteSymbolText(str, '"', false), double number => TokenFacts.NumberText(number),
                        System.Numerics.BigInteger number => Utf8String.Format(number) + "n"u8, _ => default,
                    });
                }
                SetDeclaration(value.ValueDeclaration);
            }
            if ((flags & (S.Function | S.Method)) != 0)
            {
                bool method = (flags & S.Method) != 0;
                IReadOnlyList<Signature> signatures;
                if (node is IdentifierNode && node.Parent is { HasFunctionSignature: true } owner && owner.DeclarationName == node && value.Declarations.Contains(owner))
                { SetDeclaration(owner); signatures = [await checker.Signatures.FromDeclarationAsync(owner, cancellation)]; }
                else
                {
                    signatures = await SignaturesAtLocationAsync(checker, value, false, node, cancellation);
                    if (signatures is [var signature] && signature.Declaration is { } d && (d.Flags & NodeFlags.JSDoc) == 0) SetDeclaration(d);
                }
                await WriteSignatures(signatures, method ? "method"u8 : "function "u8, method, value);
                SetDeclaration(value.ValueDeclaration);
            }
            if ((flags & (S.Class | S.Interface)) != 0)
            {
                if (node.Kind == K.ThisKeyword || IsThisInTypeQuery(node)) { NewLine(); text.Keyword("this"u8); }
                else if (node.Kind == K.ConstructorKeyword && node.Parent is ConstructorDeclarationNode or ConstructSignatureDeclarationNode)
                {
                    SetDeclaration(node.Parent);
                    await WriteSignatures([await checker.Signatures.FromDeclarationAsync(node.Parent!, cancellation)], "constructor "u8, false, value);
                }
                else
                {
                    var signatures = (flags & S.Class) != 0 && CallOrNew(node) is not null
                        ? await SignaturesAtLocationAsync(checker, value, true, node, cancellation) : [];
                    if (signatures is [var signature])
                    {
                        if (signature.Declaration is { } d && (d.Flags & NodeFlags.JSDoc) == 0) SetDeclaration(d);
                        await WriteSignatures(signatures, "constructor "u8, false, value);
                    }
                    else
                    {
                        NewLine();
                        if (value.Declarations.Any(d => d is ClassExpressionNode)) text.Label("local class"u8);
                        if (!await ExpandSymbol(value, flags))
                        {
                            if ((flags & S.Class) != 0)
                            {
                                if (!value.Declarations.Any(d => d is ClassExpressionNode))
                                {
                                    if (value.Declarations.Any(d => d is ClassDeclarationNode && SemanticSyntax.HasModifier(d, K.AbstractKeyword))) text.Keyword("abstract "u8);
                                    text.Keyword("class "u8);
                                }
                            }
                            else text.Keyword("interface "u8);
                            await WriteSymbolName(value, container);
                            await WriteParameters(checker.LocalTypeParameters(value, cancellation));
                        }
                    }
                }
                SetDeclaration((flags & S.Class) != 0 ? value.ValueDeclaration : value.Declarations.FirstOrDefault(d => d is InterfaceDeclarationNode));
            }
            if ((flags & S.Enum) != 0)
            {
                NewLine();
                if (!await ExpandSymbol(value, flags))
                {
                    if (value.Declarations.Any(d => d is EnumDeclarationNode && SemanticSyntax.HasModifier(d, K.ConstKeyword))) text.Keyword("const "u8);
                    text.Keyword("enum "u8); await WriteSymbolName(value, container);
                }
                SetDeclaration(value.Declarations.FirstOrDefault(d => d is EnumDeclarationNode));
            }
            if ((flags & S.Module) != 0)
            {
                NewLine();
                if (!await ExpandSymbol(value, flags))
                {
                    text.Keyword(value.ValueDeclaration is SourceFileNode or ModuleDeclarationNode { Name: StringLiteralNode } ? "module "u8 : "namespace "u8);
                    await WriteSymbolName(value, container);
                    if (value.Declarations.OfType<ModuleDeclarationNode>().FirstOrDefault(d => d.Attributes is not null) is { Attributes: { } attributes } module)
                    {
                        var emit = new EmitContext(); emit.SetFlags(attributes, EmitFlags.SingleLine);
                        text.Keyword(" with "u8);
                        if (classified) text.Copy(SyntaxPrinter.PrintDisplay(attributes, SemanticSyntax.Source(module), new Dictionary<SyntaxNode, Symbol>(), emit, cancellation));
                        else text.Append(new SyntaxPrinter(context: emit).Print(attributes, SemanticSyntax.Source(module)));
                    }
                }
                SetDeclaration(value.Declarations.FirstOrDefault(d => d is ModuleDeclarationNode));
            }
            if ((flags & S.TypeParameter) != 0)
            {
                NewLine(); text.Label("type parameter"u8);
                if (node is IdentifierNode && node.Parent is TypeReferenceNode && await checker.GetTypeAtLocationAsync(node.Parent, cancellation) is TypeParameter { IsDistributed: true })
                    text.Label("distributed"u8);
                var type = await checker.Declared.GetAsync(value, cancellation);
                await WriteSymbolName(value, container);
                if (await checker.Instantiation.Constraints.ConstraintAsync(type, cancellation) is { } constraint)
                { text.Keyword(" extends "u8); await WriteType(constraint, container); }
                if (value.Parent is { } parent)
                {
                    text.Keyword(" in "u8); await WriteSymbolName(parent, container);
                    if (await checker.Declared.GetAsync(parent, cancellation) is InterfaceType)
                        await WriteParameters(checker.LocalTypeParameters(parent, cancellation));
                }
                else if (value.Declarations.OfType<TypeParameterDeclarationNode>().FirstOrDefault()?.Parent is { } owner)
                {
                    if (owner.HasFunctionSignature)
                    {
                        text.Keyword(" in "u8);
                        if (owner is ConstructSignatureDeclarationNode) text.Keyword("new "u8);
                        else if (owner is not CallSignatureDeclarationNode && owner.DeclarationName is not null && owner.BindingSymbol is { } ownerSymbol) await WriteSymbolName(ownerSymbol, container);
                        await WriteSignature(await checker.Signatures.FromDeclarationAsync(owner, cancellation), TypeFlags | TypeFormatFlags.WriteTypeArgumentsOfSignature);
                    }
                    else if (owner is TypeAliasDeclarationNode && owner.BindingSymbol is { } ownerSymbol)
                    { text.Keyword(" in "u8).Keyword("type "u8); await WriteSymbolName(ownerSymbol, container); await WriteParameters(checker.LocalTypeParameters(ownerSymbol, cancellation)); }
                }
                SetDeclaration(value.Declarations.FirstOrDefault(d => d is TypeParameterDeclarationNode));
            }
            if ((flags & S.TypeAlias) != 0)
            {
                NewLine(); text.Keyword("type "u8); await WriteSymbolName(value, container);
                await WriteParameters(checker.LocalTypeParameters(value, cancellation)); text.Operator(" = "u8);
                var type = node.Parent is TypeReferenceNode { TypeName: IdentifierNode { Text: var name } } && name == "const"u8
                    ? await checker.GetTypeAtLocationAsync(node.Parent, cancellation) : await checker.Declared.GetAsync(value, cancellation);
                await WriteType(type, container, TypeFlags | TypeFormatFlags.InTypeAlias);
                SetDeclaration(value.Declarations.FirstOrDefault(d => d is TypeAliasDeclarationNode));
            }
            if ((flags & S.Signature) != 0) { NewLine(); await WriteType(await checker.Values.GetAsync(value, cancellation), container); }
        }
        await WriteSymbol(symbol);
        return (text.ToUtf8String(), declaration, text.Runs);
    }

    internal static SyntaxNode? Container(SyntaxNode node)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            if (parent.Kind is K.SourceFile or K.MethodDeclaration or K.MethodSignature or K.FunctionDeclaration or K.FunctionExpression
                or K.GetAccessor or K.SetAccessor or K.ClassDeclaration or K.InterfaceDeclaration or K.EnumDeclaration or K.ModuleDeclaration) return parent;
        return null;
    }
    internal static SyntaxNode? CallOrNew(SyntaxNode node)
    {
        if (node.Parent is PropertyAccessExpressionNode access && access.Name == node) node = access;
        return node.Parent is CallExpressionNode { Expression: var expression } && expression == node
            || node.Parent is NewExpressionNode { Expression: var target } && target == node ? node.Parent : null;
    }
    internal static bool IsThisInTypeQuery(SyntaxNode node) => FlowReferences.ThisInQuery(node);
    private static bool ShouldGetType(SyntaxNode node) => node.Kind switch
    {
        K.Identifier => !((node.Flags & NodeFlags.JSDoc) != 0 && QuerySyntax.DeclarationName(node))
            && !(node.Parent is LabeledStatementNode or BreakStatementNode or ContinueStatementNode)
            && !(node.Parent is { Kind: >= K.JSDocUnknownTag and <= K.JSDocImportTag, ChildCount: > 0 } tag && tag.GetChild(0) == node)
            && !(node.Parent is TypeReferenceNode { TypeName: IdentifierNode { Text: var text } } && text == "const"u8),
        K.ThisKeyword or K.ThisType or K.SuperKeyword or K.NamedTupleMember => true,
        K.MetaProperty => node is MetaPropertyNode { KeywordToken: K.ImportKeyword }, _ => false,
    };
    internal static async ValueTask<IReadOnlyList<Signature>> SignaturesAtLocationAsync(Checker checker, Symbol symbol, bool construct,
        SyntaxNode node, CancellationToken cancellation)
    {
        var type = await checker.Values.GetAsync(symbol, cancellation);
        type = checker.Algebra.Filter(type, t => (t.Flags & Checking.TypeFlags.Undefined) == 0);
        var signatures = await checker.SignaturesAsync(type, construct, cancellation);
        return (signatures.Count > 1 || signatures is [{ TypeParameters.Count: > 0 }]) && CallOrNew(node) is { } call
            ? [await checker.GetResolvedSignatureAsync(call, cancellation)] : signatures;
    }

    internal static S Meaning(SyntaxNode node)
    {
        node = QuerySyntax.Reparsed(node);
        var parent = node.Parent;
        const S all = S.Value | S.Signature | S.Type | S.Namespace;
        if (parent is null) return S.Value | S.Signature;
        if (parent.Kind is K.ExportAssignment or K.ExportSpecifier or K.ExternalModuleReference or K.ImportSpecifier or K.ImportClause
            || parent is ImportEqualsDeclarationNode import && import.Name == node) return all;
        var outer = node;
        while (outer.Parent is QualifiedNameNode) outer = outer.Parent;
        if (outer.Parent is ImportEqualsDeclarationNode { ModuleReference: not ExternalModuleReferenceNode } assignment && assignment.ModuleReference == outer)
        {
            var name = node is QualifiedNameNode ? node : node.Parent is QualifiedNameNode q && q.Right == node ? q : null;
            return name?.Parent is ImportEqualsDeclarationNode ? all : S.Namespace;
        }
        if (QuerySyntax.DeclarationName(node)) return parent.Kind switch
        {
            K.VariableDeclaration or K.Parameter or K.BindingElement or K.PropertyDeclaration or K.PropertySignature or K.PropertyAssignment
                or K.ShorthandPropertyAssignment or K.MethodDeclaration or K.MethodSignature or K.Constructor or K.GetAccessor or K.SetAccessor
                or K.FunctionDeclaration or K.FunctionExpression or K.ArrowFunction or K.CatchClause or K.JsxAttribute => S.Value | S.Signature,
            K.TypeParameter or K.InterfaceDeclaration or K.TypeAliasDeclaration or K.JSTypeAliasDeclaration or K.TypeLiteral => S.Type,
            K.ModuleDeclaration => parent is ModuleDeclarationNode { Name: not StringLiteralNode } module && Binder.ModuleState(module) != 2 ? S.Namespace : all,
            _ => all,
        };
        if ((node.Flags & NodeFlags.JSDoc) != 0)
            for (var ancestor = node; ancestor is not null; ancestor = ancestor.Parent)
                if (ancestor.Kind is K.JSDocNameReference or K.JSDocLink or K.JSDocLinkCode or K.JSDocLinkPlain) return all;
        var typeNode = node.Parent is QualifiedNameNode qualified && qualified.Right == node
            || node.Parent is PropertyAccessExpressionNode property && property.Name == node ? node.Parent! : node;
        if (typeNode.Parent is TypeReferenceNode or ImportTypeNode { IsTypeOf: false }
            || typeNode.Parent is ExpressionWithTypeArgumentsNode expression && QuerySyntax.PartOfType(expression)) return S.Type;
        if (node.Parent is QualifiedNameNode || node.Parent is PropertyAccessExpressionNode && QuerySyntax.PartOfType(node.Parent)) return S.Namespace;
        return parent is TypeParameterDeclarationNode ? S.Type : parent is LiteralTypeNode ? all : S.Value | S.Signature;
    }
}
