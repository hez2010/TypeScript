using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed partial class LanguageServiceDocument
{
    private sealed partial class CompletionQuery
    {
        private enum KeywordFilter { None, All, Class, Interface, Constructor, FunctionBody, TypeAssertion, Type, TypeKeyword }

        private IEnumerable<CompletionItem> Keywords()
        {
            for (K token = K.FirstKeyword; token <= K.LastKeyword; token++)
            {
                if (JavaScript && !insideJSDocType && TypeScriptOnlyKeyword(token)) continue;
                bool include = keywords switch
                {
                    KeywordFilter.All => FunctionBodyKeyword(token) || token is K.DeclareKeyword or K.ModuleKeyword or K.TypeKeyword or K.NamespaceKeyword or K.AbstractKeyword
                        || TypeKeyword(token) && token != K.UndefinedKeyword,
                    KeywordFilter.FunctionBody => FunctionBodyKeyword(token), KeywordFilter.Class => ClassKeyword(token),
                    KeywordFilter.Interface => token == K.ReadonlyKeyword, KeywordFilter.Constructor => ParameterPropertyKeyword(token),
                    KeywordFilter.TypeAssertion => TypeKeyword(token) || token == K.ConstKeyword,
                    KeywordFilter.Type => TypeKeyword(token), KeywordFilter.TypeKeyword => token == K.TypeKeyword, _ => false,
                };
                if (include) yield return new(TokenFacts.Text(token), 14, SortText: "15"u8);
            }
        }

        private static bool TypeKeyword(K token) => token is K.AnyKeyword or K.AssertsKeyword or K.BigIntKeyword or K.BooleanKeyword or K.FalseKeyword
            or K.InferKeyword or K.KeyOfKeyword or K.NeverKeyword or K.NullKeyword or K.NumberKeyword or K.ObjectKeyword or K.ReadonlyKeyword
            or K.StringKeyword or K.SymbolKeyword or K.TypeOfKeyword or K.TrueKeyword or K.VoidKeyword or K.UndefinedKeyword or K.UniqueKeyword or K.UnknownKeyword;
        private static bool ParameterPropertyKeyword(K token) => token is K.PublicKeyword or K.PrivateKeyword or K.ProtectedKeyword or K.ReadonlyKeyword or K.OverrideKeyword;
        private static bool ClassKeyword(K token) => ParameterPropertyKeyword(token) || token is K.StaticKeyword or K.AbstractKeyword or K.AccessorKeyword
            or K.ConstructorKeyword or K.GetKeyword or K.SetKeyword or K.AsyncKeyword or K.DeclareKeyword;
        private static bool FunctionBodyKeyword(K token) => token is K.AsyncKeyword or K.AwaitKeyword or K.UsingKeyword or K.AsKeyword or K.SatisfiesKeyword or K.TypeKeyword
            || token is not (>= K.FirstContextualKeyword and <= K.LastContextualKeyword) && !ClassKeyword(token);
        private static bool TypeScriptOnlyKeyword(K token) => token is K.AbstractKeyword or K.AnyKeyword or K.BigIntKeyword or K.BooleanKeyword or K.DeclareKeyword
            or K.EnumKeyword or K.GlobalKeyword or K.ImplementsKeyword or K.InferKeyword or K.InterfaceKeyword or K.IsKeyword or K.KeyOfKeyword
            or K.ModuleKeyword or K.NamespaceKeyword or K.NeverKeyword or K.NumberKeyword or K.ObjectKeyword or K.OverrideKeyword or K.PrivateKeyword
            or K.ProtectedKeyword or K.PublicKeyword or K.ReadonlyKeyword or K.StringKeyword or K.SymbolKeyword or K.TypeKeyword or K.UniqueKeyword or K.UnknownKeyword;

        private static bool InFunctionBody(SyntaxNode? token)
        {
            SyntaxNode? child = null;
            for (var node = token; node is not null && !SemanticSyntax.ClassLike(node); child = node, node = node.Parent)
                if (SemanticSyntax.FunctionDeclarationLike(node) && child == SemanticSyntax.Body(node)) return true;
            return false;
        }

        private (bool NewIdentifier, Utf8String[] Characters) CommitCharacters()
        {
            if (contextToken is null) return (false, AllCommitCharacters);
            var parent = contextToken.Parent;
            var token = KeywordOf(contextToken);
            if (token is K.CommaToken or K.OpenParenToken && parent is CallExpressionNode or NewExpressionNode)
            {
                var expression = parent is CallExpressionNode call ? call.Expression! : ((NewExpressionNode)parent).Expression!;
                return (true, Line(expression.End) == Line(position) ? AllCommitCharacters : ["."u8, ";"u8]);
            }
            return token switch
            {
                K.CommaToken when parent is BinaryExpressionNode => (true, ["."u8, ";"u8]),
                K.CommaToken when parent is ConstructorDeclarationNode or FunctionTypeNode or ObjectLiteralExpressionNode => (true, []),
                K.CommaToken when parent is ArrayLiteralExpressionNode => (true, AllCommitCharacters),
                K.OpenParenToken when parent is ParenthesizedExpressionNode => (true, ["."u8, ";"u8]),
                K.OpenParenToken when parent is ConstructorDeclarationNode or ParenthesizedTypeNode => (true, []),
                K.OpenBracketToken when parent is ArrayLiteralExpressionNode or IndexSignatureDeclarationNode or TupleTypeNode or ComputedPropertyNameNode => (true, AllCommitCharacters),
                K.ModuleKeyword or K.NamespaceKeyword or K.ImportKeyword => (true, []),
                K.DotToken when parent is ModuleDeclarationNode => (true, []),
                K.OpenBraceToken when parent is ClassDeclarationNode or ObjectLiteralExpressionNode => (true, []),
                K.EqualsToken when parent is VariableDeclarationNode or BinaryExpressionNode => (true, AllCommitCharacters),
                K.TemplateHead => (parent is TemplateExpressionNode, AllCommitCharacters),
                K.TemplateMiddle => (parent is TemplateSpanNode, AllCommitCharacters),
                K.AsyncKeyword when parent is MethodDeclarationNode or ShorthandPropertyAssignmentNode => (true, []),
                K.AsteriskToken when parent is MethodDeclarationNode => (true, []),
                _ when ClassKeyword(token) => (true, []), _ => (false, AllCommitCharacters),
            };
        }

        private static bool ContextValueLocation(SyntaxNode? token) => token?.Kind == K.TypeOfKeyword && token.Parent is TypeQueryNode or TypeOfExpressionNode
            || token?.Kind == K.AssertsKeyword && token.Parent is TypePredicateNode;
        private static bool ContextTypeLocation(SyntaxNode? token) => token?.Kind switch
        {
            K.ColonToken => token.Parent is PropertyDeclarationNode or PropertySignatureDeclarationNode or ParameterDeclarationNode or VariableDeclarationNode
                || token.Parent is { } function && Signatures.FunctionLike(function),
            K.EqualsToken => token.Parent is TypeAliasDeclarationNode or TypeParameterDeclarationNode,
            K.AsKeyword => token.Parent is AsExpressionNode,
            K.LessThanToken => token.Parent is TypeReferenceNode or TypeAssertionNode,
            K.ExtendsKeyword => token.Parent is TypeParameterDeclarationNode,
            K.SatisfiesKeyword => token.Parent is SatisfiesExpressionNode,
            K.OpenBracketToken or K.CommaToken => token.Parent is TupleTypeNode, _ => false,
        };

        private async ValueTask<bool> DeclarationKeywordsAsync()
        {
            if (contextToken is not null && (contextToken.Kind is K.OpenParenToken or K.CommaToken && contextToken.Parent is ConstructorDeclarationNode || ConstructorParameter(contextToken)))
            { newIdentifier = true; keywords = KeywordFilter.Constructor; return true; }
            var declaration = await ObjectTypeDeclarationAsync();
            if (declaration is null) return false;
            kind = CompletionKind.Member; newIdentifier = true;
            keywords = contextToken?.Kind == K.AsteriskToken ? KeywordFilter.None : SemanticSyntax.ClassLike(declaration) ? KeywordFilter.Class : KeywordFilter.Interface;
            if (!SemanticSyntax.ClassLike(declaration)) return true;
            var element = contextToken?.Kind == K.SemicolonToken ? contextToken.Parent?.Parent : contextToken?.Parent;
            bool isPrivate = element is not null && SemanticSyntax.HasModifier(element, K.PrivateKeyword);
            bool isStatic = element is ClassStaticBlockDeclarationNode || element is not null && SemanticSyntax.IsStatic(element);
            bool isOverride = element is not null && SemanticSyntax.HasModifier(element, K.OverrideKeyword);
            if (contextToken is IdentifierNode && !await EditingAsync(contextToken))
            {
                isPrivate |= KeywordOf(contextToken) == K.PrivateKeyword; isStatic |= KeywordOf(contextToken) == K.StaticKeyword; isOverride |= KeywordOf(contextToken) == K.OverrideKeyword;
            }
            if (isPrivate) return true;
            var heritage = declaration is ClassDeclarationNode cls ? cls.HeritageClauses : ((ClassExpressionNode)declaration).HeritageClauses;
            var members = declaration is ClassDeclarationNode c ? c.Members : ((ClassExpressionNode)declaration).Members;
            HashSet<Utf8String> existing = [];
            foreach (var member in members ?? new([]))
                if (member is PropertyDeclarationNode or MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode && !await EditingAsync(member)
                    && !SemanticSyntax.HasModifier(member, K.PrivateKeyword) && SemanticSyntax.IsStatic(member) == isStatic && member.DeclarationName is { } name)
                    existing.Add(PropertyName(name));
            foreach (var clause in (heritage ?? new([])).OfType<HeritageClauseNode>())
            {
                if (isOverride && clause.Token != K.ExtendsKeyword) continue;
                foreach (var baseNode in clause.Types ?? new([]))
                {
                    var type = await checker.GetTypeAtLocationAsync(baseNode, cancellation);
                    if (isStatic) { if (type.Symbol is not { } baseSymbol) continue; type = await checker.GetTypeOfSymbolAtLocationAsync(baseSymbol, declaration, cancellation); }
                    foreach (var symbol in await checker.Properties.GetAsync(type, cancellation))
                        if (!existing.Contains(symbol.Name) && symbol.Declarations.Length > 0 && !symbol.Declarations.Any(d => SemanticSyntax.HasModifier(d, K.PrivateKeyword))
                            && symbol.ValueDeclaration?.DeclarationName is not PrivateIdentifierNode)
                        {
                            var name = symbol.ValueDeclaration is { } value && SemanticSyntax.ClassElement(value) && value.DeclarationName is ComputedPropertyNameNode
                                ? await checker.GetSymbolDisplayNameAsync(symbol, cancellation: cancellation) : default;
                            symbols.Add(new(symbol, DisplayName: name));
                        }
                }
            }
            return true;
        }

        private async ValueTask<SyntaxNode?> ObjectTypeDeclarationAsync()
        {
            if (location.Kind == K.SyntaxList) return ObjectType(location.Parent) ? location.Parent : null;
            if (location.Kind == K.EndOfFile && File.Statements is { Count: > 0 } statements && ObjectType(statements[^1])
                && await SyntaxNavigation.FindChildOfKindAsync(statements[^1], K.CloseBraceToken, File, cancellation) is null) return statements[^1];
            if (location is PrivateIdentifierNode && location.Parent is PropertyDeclarationNode) return Ancestor(location, SemanticSyntax.ClassLike);
            if (location is IdentifierNode)
            {
                if (KeywordOf(location) != K.Unknown || location.Parent is PropertyDeclarationNode property && property.Initializer == location) return null;
                if (FromObjectType(location)) return Ancestor(location, ObjectType);
            }
            if (contextToken is null) return null;
            if (location.Kind == K.ConstructorKeyword || contextToken is IdentifierNode && contextToken.Parent is PropertyDeclarationNode && SemanticSyntax.ClassLike(location))
                return Ancestor(contextToken, SemanticSyntax.ClassLike);
            if (contextToken.Kind == K.EqualsToken) return null;
            if (contextToken.Kind is K.SemicolonToken or K.CloseBraceToken)
                return FromObjectType(location) && location.Parent!.DeclarationName == location ? location.Parent.Parent : ObjectType(location) ? location : null;
            if (contextToken.Kind is K.CommaToken or K.OpenBraceToken)
                return ObjectType(contextToken.Parent) ? contextToken.Parent : null;
            if (!ObjectType(location)) return null;
            if (Line(contextToken.End) != Line(position)) return location;
            var tokenKind = KeywordOf(contextToken);
            return contextToken.Kind == K.AsteriskToken || (SemanticSyntax.ClassLike(contextToken.Parent?.Parent) ? ClassKeyword(tokenKind) : tokenKind == K.ReadonlyKeyword)
                ? contextToken.Parent?.Parent : null;
        }

        private static bool ObjectType(SyntaxNode? node) => node is ClassDeclarationNode or ClassExpressionNode or InterfaceDeclarationNode or TypeLiteralNode;
        private static bool FromObjectType(SyntaxNode node) => node.Parent is { } parent && ObjectType(parent.Parent)
            && (SemanticSyntax.ClassElement(parent) || parent is PropertySignatureDeclarationNode or MethodSignatureDeclarationNode or CallSignatureDeclarationNode or ConstructSignatureDeclarationNode);
        private static bool ConstructorParameter(SyntaxNode node) => node.Parent is ParameterDeclarationNode { Parent: ConstructorDeclarationNode }
            && (ParameterPropertyKeyword(node.Kind) || QuerySyntax.DeclarationName(node));
    }
}
