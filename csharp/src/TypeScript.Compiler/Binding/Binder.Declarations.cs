using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using S = TypeScript.Compiler.Binding.SymbolFlags;

namespace TypeScript.Compiler.Binding;

public sealed partial class Binder
{
    private static bool Assignment(K kind) => kind >= K.FirstAssignment && kind <= K.LastAssignment;

    private static SyntaxNode? AccessBase(SyntaxNode? node) => node switch
    {
        PropertyAccessExpressionNode p => p.Expression,
        ElementAccessExpressionNode e => e.Expression,
        _ => null
    };

    private static TextSlice? AccessName(SyntaxNode? node) => node switch
    {
        PropertyAccessExpressionNode { Name: IdentifierNode name } => NameText(name),
        ElementAccessExpressionNode e when LiteralLike(SkipParentheses(e.ArgumentExpression)) => LiteralName(SkipParentheses(e.ArgumentExpression)),
        _ => null
    };

    private static bool ModuleExports(SyntaxNode? node) =>
        AccessBase(node) is IdentifierNode { Text.Span: "module" } && AccessName(node) == "exports";

    private static bool ExportsBase(SyntaxNode? node) => node is IdentifierNode { Text.Span: "exports" } || ModuleExports(node);

    private static bool AliasExpression(SyntaxNode? node)
    {
        if (node is ClassExpressionNode)
            return true;
        if (node is IdentifierNode)
            return true;
        while (node is PropertyAccessExpressionNode property)
            node = property.Expression;
        return node is IdentifierNode;
    }

    private bool IsRequireVariable(SyntaxNode node)
    {
        if (node is BindingElementNode && node.Parent?.Parent is { } declaration)
            node = declaration;
        return file.ScriptKind is ScriptKind.JS or ScriptKind.JSX && !CombinedHas(node, K.ExportKeyword)
            && node is VariableDeclarationNode
            {
                Type: null, Initializer: CallExpressionNode
                { Expression: IdentifierNode { Text.Span: "require" }, Arguments: { Count: 1 } args }
            } && args[0] is StringLiteralNode or NoSubstitutionTemplateLiteralNode;
    }

    private void BindAssignmentDeclaration(BinaryExpressionNode node)
    {
        bool js = file.ScriptKind is ScriptKind.JS or ScriptKind.JSX;
        if (js && ModuleExports(node.Left) && node.Right is not IdentifierNode { Text.Span: "exports" })
        {
            if (CommonJS(node))
            {
                var owner = SymbolOf(file)!;
                SetValue(Declare(owner.ExportTable, owner, node, AliasExpression(node.Right) ? S.Alias : S.Property, 0, "export="), node);
            }
        }
        else if (js && ExportsBase(AccessBase(node.Left)) && AccessName(node.Left) is not null)
        {
            if (CommonJS(node))
            {
                var owner = SymbolOf(file)!;
                Declare(owner.ExportTable, owner, node, AliasExpression(node.Right) ? S.Alias : S.FunctionScopedVariable,
                    S.FunctionScopedVariableExcludes, AccessName(node.Left) ?? Internal("computed"));
            }
        }
        else if (js && AccessBase(node.Left)?.Kind == K.ThisKeyword)
        {
            if (node.Left is PropertyAccessExpressionNode { Name: PrivateIdentifierNode })
                return;
            if (thisContainer.Kind is K.Constructor or K.PropertyDeclaration or K.MethodDeclaration or K.GetAccessor or K.SetAccessor
                or K.ClassStaticBlockDeclaration
                && thisContainer.Parent is { } parent && SymbolOf(parent) is { } owner)
            {
                TextSlice name = DeclarationName(node);
                bool dynamic = name == Internal("computed");
                Declare(
                    Has(thisContainer, K.StaticKeyword) || thisContainer.Kind == K.ClassStaticBlockDeclaration
                        ? owner.ExportTable
                        : owner.MemberTable,
                    owner, node, dynamic ? S.Property : S.Property | S.Assignment, 0, name, true);
                if (dynamic)
                    LateAssignment(owner, node);
            }
        }
        else if (node.Left is PropertyAccessExpressionNode { Name: IdentifierNode } or ElementAccessExpressionNode)
            deferredAssignments.Add((node, container, blockContainer));
    }

    private void BindDefineProperty(CallExpressionNode node)
    {
        if (file.ScriptKind is not (ScriptKind.JS or ScriptKind.JSX)
            || node.Expression is not PropertyAccessExpressionNode { Expression: IdentifierNode { Text.Span: "Object" }, Name: IdentifierNode { Text.Span: "defineProperty" } }
            || node.Arguments is not { Count: 3 } args || !LiteralLike(args[1]))
            return;
        if (ExportsBase(args[0]))
        {
            if (CommonJS(node))
            {
                var owner = SymbolOf(file)!;
                Declare(owner.ExportTable, owner, node, S.FunctionScopedVariable, S.FunctionScopedVariableExcludes);
            }
        }
        else
            deferredAssignments.Add((node, container, blockContainer));
    }

    private Symbol? LookupEntity(SyntaxNode? node, SyntaxNode scope)
    {
        var names = new Stack<TextSlice>();
        while (node is PropertyAccessExpressionNode or ElementAccessExpressionNode)
        {
            if (AccessName(node) is not { } name)
                return null;
            names.Push(name);
            node = AccessBase(node);
        }
        if (node is not IdentifierNode identifier)
            return null;
        Symbol? symbol = null;
        symbol = result.Get(scope)?.Locals.GetValueOrDefault(UserName(identifier.Text)) ?? SymbolOf(scope)?.Exports.GetValueOrDefault(UserName(identifier.Text));
        symbol = symbol?.ExportSymbol ?? symbol;
        while (names.TryPop(out TextSlice name))
            symbol = InitializerSymbol(symbol)?.Exports.GetValueOrDefault(name);
        return symbol?.ExportSymbol ?? symbol;
    }

    private Symbol? InitializerSymbol(Symbol? symbol)
    {
        if (symbol?.ValueDeclaration is not { } declaration)
            return null;
        bool js = (declaration.Flags & NodeFlags.JavaScriptFile) != 0;
        if (declaration is FunctionDeclarationNode || js && declaration is ClassDeclarationNode)
            return symbol;
        SyntaxNode? initializer = declaration switch
        {
            VariableDeclarationNode variable when js || ((variable.Parent?.Flags ?? 0) & NodeFlags.Const) != 0 => variable.Initializer,
            BinaryExpressionNode assignment when js => assignment.Right,
            _ => null
        };
        return initializer is FunctionExpressionNode or ArrowFunctionNode
            || js && (initializer is ClassExpressionNode || initializer is ObjectLiteralExpressionNode { Properties.Count: 0 }
                && declaration is not ITypedNode { Type: not null })
                ? SymbolOf(initializer!) : null;
    }

    private void DeferredAssignments()
    {
        foreach (var (node, savedContainer, savedBlock) in deferredAssignments)
        {
            cancellation.ThrowIfCancellationRequested();
            container = savedContainer;
            blockContainer = savedBlock;
            SyntaxNode? target = node is BinaryExpressionNode binary ? AccessBase(binary.Left) : ((CallExpressionNode)node).Arguments![0];
            var symbol = InitializerSymbol(LookupEntity(target, blockContainer) ?? LookupEntity(target, container));
            if (symbol is null)
                continue;
            TextSlice name = DeclarationName(node);
            if (name == Internal("computed"))
            {
                Anonymous(node, S.Property | S.Assignment, name);
                LateAssignment(symbol, node);
            }
            else if (!symbol.ExportTable.TryGetValue(name, out var existing) || (existing.Flags & S.Assignment) != 0)
                Declare(symbol.ExportTable, symbol, node, S.Property | S.Assignment, S.PropertyExcludes);
        }
    }

    private void LateAssignment(Symbol symbol, SyntaxNode node)
    {
        if (!symbol.ExportTable.TryGetValue(Internal("assignment"), out var assignments))
            symbol.ExportTable[Internal("assignment")] = assignments = NewSymbol(0, Internal("assignment"));
        assignments.DeclarationList.Add(node);
    }

    private void FinishModule(Symbol? symbol)
    {
        if (symbol?.Exports.GetValueOrDefault("export=") is not { } exported)
            return;
        foreach (var entry in symbol.Exports)
            if (entry.Key != "export=" && (entry.Value.Flags & (S.Type | S.Namespace)) != 0)
            {
                exported.ExportTable[entry.Key] = entry.Value;
                exported.Flags |= S.NamespaceModule;
            }
    }

    private void CommonJSVariable(TextSlice name)
    {
        if (Data(file).LocalTable.ContainsKey(name))
            return;
        var symbol = NewSymbol(S.FunctionScopedVariable | S.ModuleExports, name);
        symbol.DeclarationList.Add(file);
        symbol.ValueDeclaration = file;
        Data(file).LocalTable[name] = symbol;
        if (name == "module")
        {
            var exports = NewSymbol(S.ModuleExports | S.Property, "exports", symbol);
            exports.DeclarationList.Add(file);
            exports.ValueDeclaration = file;
            symbol.MemberTable["exports"] = exports;
        }
    }

    private void CheckEval(SyntaxNode context, SyntaxNode? name)
    {
        if (name is not IdentifierNode { Text.Span: "eval" or "arguments" } identifier)
            return;
        var message = ContainingClass(context) is not null ? Messages.Code_contained_in_a_class_is_evaluated_in_JavaScript_s_strict_mode_which_does_not_allow_this_use_of_0_For_more_information_see_https_Colon_Slash_Slashdeveloper_mozilla_org_Slashen_US_Slashdocs_SlashWeb_SlashJavaScript_SlashReference_SlashStrict_mode
            : file.ExternalModuleIndicator is not null
                ? Messages.Invalid_use_of_0_Modules_are_automatically_in_strict_mode
                : Messages.Invalid_use_of_0_in_strict_mode;
        Error(name, message, identifier.Text);
    }

    private void CheckIdentifier(SyntaxNode node)
    {
        if (file.ParseDiagnostics.Count != 0 || (node.Flags & (NodeFlags.Ambient | NodeFlags.JSDoc)) != 0 || IdentifierName(node))
            return;
        TextSlice text = ((IdentifierNode)node).Text;
        TextSlice display = SourceName(node);
        K keyword = TokenFacts.FromText(text);
        if (keyword >= K.FirstFutureReservedWord && keyword <= K.LastFutureReservedWord)
            Error(node, ContainingClass(node) is not null
                ? Messages.Identifier_expected_0_is_a_reserved_word_in_strict_mode_Class_definitions_are_automatically_in_strict_mode
                : file.ExternalModuleIndicator is not null ? Messages.Identifier_expected_0_is_a_reserved_word_in_strict_mode_Modules_are_automatically_in_strict_mode
                : Messages.Identifier_expected_0_is_a_reserved_word_in_strict_mode, display);
        else if (keyword == K.AwaitKeyword)
        {
            bool topLevel = true;
            SyntaxNode? start = node.Parent;
            if (start is FunctionDeclarationNode or ClassDeclarationNode && Name(start) == node)
                start = start.Parent;
            for (SyntaxNode? parent = start; parent is not null; parent = parent.Parent)
                if (FunctionLike(parent))
                {
                    topLevel = false;
                    break;
                }
            if (file.ExternalModuleIndicator is not null && topLevel)
                Error(node, Messages.Identifier_expected_0_is_a_reserved_word_at_the_top_level_of_a_module, display);
            else if ((node.Flags & NodeFlags.AwaitContext) != 0)
                Error(node, Messages.Identifier_expected_0_is_a_reserved_word_that_cannot_be_used_here, display);
        }
        else if (keyword == K.YieldKeyword && (node.Flags & NodeFlags.YieldContext) != 0)
            Error(node, Messages.Identifier_expected_0_is_a_reserved_word_that_cannot_be_used_here, display);
    }

    private static bool IdentifierName(SyntaxNode node) => node.Parent switch
    {
        PropertyAccessExpressionNode p => p.Name == node,
        QualifiedNameNode q => q.Right == node,
        PropertyDeclarationNode or PropertySignatureDeclarationNode or PropertyAssignmentNode or MethodDeclarationNode
            or MethodSignatureDeclarationNode
            or GetAccessorDeclarationNode or SetAccessorDeclarationNode or EnumMemberNode or JsxAttributeNode => Name(node.Parent) == node,
        BindingElementNode b => b.PropertyName == node,
        ImportSpecifierNode i => i.PropertyName == node,
        ExportSpecifierNode or JsxSelfClosingElementNode or JsxOpeningElementNode or JsxClosingElementNode => true,
        _ => false
    };

    private void Error(SyntaxNode node, DiagnosticMessage message, TextSlice argument, bool firstToken = false) =>
        Error(node, message, [argument], firstToken);

    private void Error(SyntaxNode node, DiagnosticMessage message, TextSlice[]? arguments = null, bool firstToken = false)
        => diagnostics.Add(CreateDiagnostic(node, message, arguments, firstToken));

    private Diagnostic CreateDiagnostic(SyntaxNode node, DiagnosticMessage message, TextSlice[]? arguments = null, bool firstToken = false)
    {
        if (!firstToken && node.Kind is K.FunctionDeclaration or K.FunctionExpression or K.ClassDeclaration or K.ClassExpression
            or K.VariableDeclaration or K.BindingElement or K.InterfaceDeclaration or K.ModuleDeclaration or K.EnumDeclaration
            or K.EnumMember or K.GetAccessor or K.SetAccessor or K.TypeAliasDeclaration or K.JSTypeAliasDeclaration
            or K.PropertyDeclaration or K.PropertySignature or K.NamespaceImport or K.MethodDeclaration)
        {
            if (Name(node) is { } name)
                node = name;
            else
                firstToken = true;
        }
        if (!firstToken && node.Pos == node.End)
        {
            return new(message, node.Pos, 0, arguments ?? []) { FileName = file.FileName };
        }
        var scanner = new Scanner(file.Source);
        scanner.ResetPosition(file.Source.ToUtf16Position(Math.Max(0, node.Pos)));
        scanner.Scan();
        int start = file.Source.ToBytePosition(scanner.TokenStart);
        int end = firstToken ? file.Source.ToBytePosition(scanner.Position) : node.End;
        return new(message, start, Math.Max(0, end - start), arguments ?? []) { FileName = file.FileName };
    }
}
