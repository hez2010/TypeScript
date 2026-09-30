using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using S = TypeScript.Compiler.Binding.SymbolFlags;

namespace TypeScript.Compiler.Binding;

public sealed partial class Binder
{
    private sealed class BindingCache
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal BoundSourceFile? Value;
    }

    private static readonly ConditionalWeakTable<SourceFileNode, BindingCache> Cache = new();
    private readonly SourceFileNode file;
    private readonly BoundSourceFile result;
    private readonly CancellationToken cancellation;
    private readonly List<Diagnostic> diagnostics = [];
    private readonly List<SyntaxNode> containers = [];
    private readonly SymbolTable globalExports = new();
    private readonly HashSet<Symbol> notConstEnumOnly = [];

    private sealed class PatternIdentity
    {
        private static long next;
        internal readonly long Value = Interlocked.Increment(ref next);
    }

    private static readonly ConditionalWeakTable<SyntaxNode, PatternIdentity> PatternIdentities = new();
    private SyntaxNode container, blockContainer, thisContainer;
    private readonly List<(SyntaxNode Node, SyntaxNode Container, SyntaxNode Block)> deferredAssignments = [];
    private FlowNode currentFlow;
    private readonly FlowNode unreachable = new(FlowFlags.Unreachable);
    private FlowNode? breakTarget, continueTarget, returnTarget, exceptionTarget, trueTarget, falseTarget;
    private readonly List<ActiveLabel> labels = [];
    private int labelStart;
    private bool explicitReturn, seenThis, assignmentPattern, hasFlowEffects, seenParseError;
    private NodeFlags emitFlags;

    private readonly record struct ActiveLabel(Utf8String Name, FlowNode Break, FlowNode? Continue, bool Referenced = false);

    private Binder(SourceFileNode file, CancellationToken cancellation)
    {
        this.file = file;
        this.cancellation = cancellation;
        result = new(file);
        container = blockContainer = thisContainer = file;
        currentFlow = new(FlowFlags.Start);
    }

    public static BoundSourceFile Bind(SourceFileNode file, CancellationToken cancellation = default) =>
        Parser.RunParse(BindAsync(file, cancellation));

    public static async ValueTask<BoundSourceFile> BindAsync(SourceFileNode file, CancellationToken cancellation = default)
    {
        var entry = Cache.GetValue(file, static _ => new());
        await entry.Gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (entry.Value is { } cached)
                return cached;
            var binder = new Binder(file, cancellation);
            await binder.Visit(file).ConfigureAwait(false);
            binder.DeferredAssignments();
            binder.result.Diagnostics = binder.diagnostics.ToArray();
            binder.result.Containers = binder.containers.ToArray();
            binder.result.GlobalExports = binder.globalExports.AsReadOnly();
            cancellation.ThrowIfCancellationRequested();
            return entry.Value = binder.result;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private NodeBinding Data(SyntaxNode node) => result.Data(node);

    private Symbol? SymbolOf(SyntaxNode node) => result.Get(node)?.Symbol;

    private static SyntaxNode? Name(SyntaxNode node) => node switch
    {
        BinaryExpressionNode { Left: PropertyAccessExpressionNode p } => p.Name,
        BinaryExpressionNode { Left: ElementAccessExpressionNode p } => LiteralLike(SkipParentheses(p.ArgumentExpression))
            ? SkipParentheses(p.ArgumentExpression)
            : p,
        CallExpressionNode { Arguments: { Count: 3 } args } => args[1],
        ExportAssignmentNode { Expression: IdentifierNode name } => name,
        _ => node.DeclarationName
    };

    private static bool Has(SyntaxNode node, K kind) => SemanticSyntax.HasModifier(node, kind);

    private static bool CombinedHas(SyntaxNode node, K kind)
    {
        while (node is BindingElementNode or BindingPatternNode or VariableDeclarationNode or VariableDeclarationListNode)
        {
            if (Has(node, kind))
                return true;
            if (node.Parent is not { } parent)
                break;
            node = parent;
        }
        return Has(node, kind);
    }

    private static bool AmbientModule(SyntaxNode node) =>
        node is ModuleDeclarationNode { Name: StringLiteralNode } or ModuleDeclarationNode { Keyword: K.GlobalKeyword };

    private static bool ClassLike(SyntaxNode node) => node.Kind is K.ClassDeclaration or K.ClassExpression;

    private static bool FunctionLike(SyntaxNode node) => node.HasFunctionSignature;

    private static SyntaxNode? ContainingClass(SyntaxNode node)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            if (ClassLike(parent))
                return parent;
        return null;
    }

    private static SyntaxNode? Body(SyntaxNode node) => node switch
    {
        FunctionDeclarationNode n => n.Body,
        FunctionExpressionNode n => n.Body,
        ArrowFunctionNode n => n.Body,
        MethodDeclarationNode n => n.Body,
        ConstructorDeclarationNode n => n.Body,
        GetAccessorDeclarationNode n => n.Body,
        SetAccessorDeclarationNode n => n.Body,
        ClassStaticBlockDeclarationNode n => n.Body,
        ModuleDeclarationNode n => n.Body,
        _ => null
    };

    private static NodeList? Statements(SyntaxNode? node) => node switch
    {
        SourceFileNode n => n.Statements,
        BlockNode n => n.Statements,
        ModuleBlockNode n => n.Statements,
        CaseOrDefaultClauseNode n => n.Statements,
        _ => null
    };

    private ValueTask Visit(SyntaxNode? node)
    {
        if (node is null)
            return default;
        cancellation.ThrowIfCancellationRequested();
        if (node.Kind <= K.LastToken)
        {
            // Tokens cannot recurse. Keep them out of the container and async
            // traversal machinery, while preserving their flow and error state.
            switch (node.Kind)
            {
                case K.Identifier when node is IdentifierNode:
                    Data(node).Flow = currentFlow;
                    CheckIdentifier(node);
                    break;
                case K.PrivateIdentifier when node is PrivateIdentifierNode { Text.Span: var matchedText } && matchedText.SequenceEqual("#constructor"u8):
                    if (file.ParseDiagnostics.Count == 0)
                        Error(node, Messages.X_constructor_is_a_reserved_word, SourceName(node));
                    break;
                case K.ThisKeyword:
                    seenThis = true;
                    Data(node).Flow = currentFlow;
                    break;
                case K.SuperKeyword:
                    Data(node).Flow = currentFlow;
                    break;
            }
            RecordParseError(node, (node.Flags & NodeFlags.ThisNodeHasError) != 0);
            return default;
        }
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            return VisitOnFreshStack(node);
        DeclareNode(node);
        bool hasError = (node.Flags & NodeFlags.ThisNodeHasError) != 0;
        return VisitChildren(node, hasError);
    }

    private async ValueTask VisitOnFreshStack(SyntaxNode node)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        await Visit(node).ConfigureAwait(false);
    }

    private async ValueTask VisitChildren(SyntaxNode node, bool hasError)
    {
        bool savedError = seenParseError;
        seenParseError = false;
        if (IsContainer(node) || BlockScope(node) || node.Kind == K.ModuleBlock
            || node is PropertyDeclarationNode { Initializer: not null })
            await VisitContainer(node).ConfigureAwait(false);
        else
            await Children(node).ConfigureAwait(false);
        hasError |= seenParseError;
        seenParseError = savedError;
        RecordParseError(node, hasError);
    }

    private void RecordParseError(SyntaxNode node, bool hasError)
    {
        if (hasError)
        {
            Data(node).Flags |= NodeFlags.ThisNodeOrAnySubNodesHasError;
            seenParseError = true;
        }
    }

    internal static bool IsContainer(SyntaxNode n) => FunctionLike(n) || n.Kind is K.SourceFile or K.ClassDeclaration or K.ClassExpression
        or K.EnumDeclaration or K.ObjectLiteralExpression or K.TypeLiteral or K.JsxAttributes or K.InterfaceDeclaration
        or K.ModuleDeclaration or K.TypeAliasDeclaration or K.JSTypeAliasDeclaration or K.MappedType or K.ClassStaticBlockDeclaration or K.IndexSignature;

    private static bool BlockScope(SyntaxNode n) => n.Kind is K.CatchClause or K.ForStatement or K.ForInStatement or K.ForOfStatement
        or K.CaseBlock
        || n.Kind == K.Block && n.Parent is { } parent && !FunctionLike(parent) && parent.Kind != K.ClassStaticBlockDeclaration;

    private void DeclareNode(SyntaxNode node)
    {
        if (!file.IsDeclarationFile && (node.Flags & NodeFlags.Ambient) == 0 && Has(node, K.AsyncKeyword) && Body(node) is not null
            && node.Kind is K.FunctionDeclaration or K.FunctionExpression or K.ArrowFunction or K.MethodDeclaration
            && node is not (FunctionDeclarationNode { AsteriskToken: not null } or FunctionExpressionNode { AsteriskToken: not null }
                or MethodDeclarationNode { AsteriskToken: not null }))
            emitFlags |= NodeFlags.HasAsyncFunctions;
        switch (node.Kind)
        {
            case K.QualifiedName when node is QualifiedNameNode:
                SyntaxNode qualified = node;
                while (qualified.Parent is QualifiedNameNode parentName)
                    qualified = parentName;
                if (qualified.Parent is TypeQueryNode)
                    Data(node).Flow = currentFlow;
                break;
            case K.SourceFile when node is SourceFileNode:
                SetExportContext(node);
                if (file.ExternalModuleIndicator is not null || file.ScriptKind == ScriptKind.JSON)
                    ExternalModule();
                if (file.ScriptKind == ScriptKind.JSON)
                {
                    var symbol = SymbolOf(file)!;
                    Declare(symbol.ExportTable, symbol, file, S.Property, S.All);
                    Data(file).Symbol = symbol;
                }
                break;
            case K.VariableDeclaration or K.BindingElement when node is (VariableDeclarationNode or BindingElementNode):
                CheckEval(node, Name(node));
                if (Name(node) is not BindingPatternNode && Name(node) is not null)
                {
                    SyntaxNode root = node;
                    while (root.Parent is { } parent && root.Kind is K.BindingElement or K.ObjectBindingPattern or K.ArrayBindingPattern)
                        root = parent;
                    bool parameter = root is ParameterDeclarationNode;
                    bool block = root.Parent is CatchClauseNode
                        || root.Parent is VariableDeclarationListNode list && (list.Flags & NodeFlags.BlockScoped) != 0;
                    if (IsRequireVariable(node))
                        Member(node, S.Alias, S.AliasExcludes);
                    else if (block)
                        BlockMember(node, S.BlockScopedVariable, S.BlockScopedVariableExcludes);
                    else
                        Member(node, S.FunctionScopedVariable, parameter ? S.ParameterExcludes : S.FunctionScopedVariableExcludes);
                }
                break;
            case K.Parameter when node is (ParameterDeclarationNode parameter):
                if ((node.Flags & NodeFlags.Ambient) == 0)
                    CheckEval(node, parameter.Name);
                if (parameter.Name is BindingPatternNode)
                {
                    int index = node.Parent is IFunctionSignature { Parameters: { } parameters } ? parameters.IndexOf(node) : 0;
                    Anonymous(node, S.FunctionScopedVariable, Utf8String.Concat("__"u8, Utf8String.Format(index)));
                }
                else
                    Member(node, S.FunctionScopedVariable, S.ParameterExcludes);
                if (node.Parent?.Kind == K.Constructor && node.Parent.Parent is { } containingClass
                    && parameter.Modifiers?.Any(
                        m => m.Kind is K.PublicKeyword or K.PrivateKeyword or K.ProtectedKeyword or K.ReadonlyKeyword
                            or K.OverrideKeyword) == true)
                {
                    var symbol = SymbolOf(containingClass)!;
                    Declare(
                        symbol.MemberTable,
                        symbol,
                        node,
                        S.Property | (parameter.QuestionToken is null ? 0 : S.Optional),
                        S.PropertyExcludes);
                }
                break;
            case K.TypeParameter when node is TypeParameterDeclarationNode:
                if (node.Parent is InferTypeNode)
                {
                    SyntaxNode child = node.Parent;
                    while (child.Parent is { } parent && !(parent is ConditionalTypeNode conditional && conditional.ExtendsType == child))
                        child = parent;
                    if (child.Parent is ConditionalTypeNode inferOwner)
                        Declare(Data(inferOwner).LocalTable, null, node, S.TypeParameter, S.TypeParameterExcludes);
                    else
                        Anonymous(node, S.TypeParameter, DeclarationName(node));
                }
                else
                    Member(node, S.TypeParameter, S.TypeParameterExcludes);
                break;
            case K.FunctionDeclaration when node is FunctionDeclarationNode:
                if ((node.Flags & NodeFlags.Ambient) == 0)
                    CheckEval(node, Name(node));
                BlockMember(node, S.Function, S.FunctionExcludes);
                break;
            case K.FunctionExpression or K.ArrowFunction when node is (FunctionExpressionNode or ArrowFunctionNode):
                CheckEval(node, Name(node));
                Data(node).Flow = currentFlow;
                Anonymous(node, S.Function, Name(node) is { } functionName ? NameText(functionName) : (Symbol.InternalFunction));
                break;
            case K.ClassDeclaration or K.ClassExpression when node is (ClassDeclarationNode or ClassExpressionNode):
                if (node is ClassDeclarationNode)
                    BlockMember(node, S.Class, S.ClassExcludes);
                else
                    Anonymous(node, S.Class, Name(node) is { } className ? NameText(className) : (Symbol.InternalClass));
                var classSymbol = SymbolOf(node)!;
                if (classSymbol.ExportTable.TryGetValue(Utf8Literals.Prototype, out var old) && old.Declarations.Length != 0)
                    Error(
                        Name(old.Declarations[0]) ?? old.Declarations[0],
                        Messages.Duplicate_identifier_0,
                        DisplayName(old.Declarations[0]));
                classSymbol.ExportTable[Utf8Literals.Prototype] = NewSymbol(S.Property | S.Prototype, Utf8Literals.Prototype, classSymbol);
                break;
            case K.InterfaceDeclaration when node is InterfaceDeclarationNode:
                BlockMember(node, S.Interface, S.InterfaceExcludes);
                break;
            case K.TypeAliasDeclaration or K.JSTypeAliasDeclaration when node is TypeAliasDeclarationNode:
                if (node.Kind != K.JSTypeAliasDeclaration || blockContainer != file)
                    BlockMember(node, S.TypeAlias, S.TypeAliasExcludes);
                break;
            case K.EnumDeclaration when node is EnumDeclarationNode:
                BlockMember(
                    node,
                    Has(node, K.ConstKeyword) ? S.ConstEnum : S.RegularEnum,
                    Has(node, K.ConstKeyword) ? S.ConstEnumExcludes : S.RegularEnumExcludes);
                break;
            case K.ModuleDeclaration when node is (ModuleDeclarationNode module):
                SetExportContext(node);
                int state = ModuleState(module);
                S flags = state == 0 ? S.NamespaceModule : S.ValueModule;
                bool augmentation = node.Parent == file && file.ExternalModuleIndicator is not null
                    || node.Parent is ModuleBlockNode { Parent: ModuleDeclarationNode outerModule }
                        && outerModule.Parent == file && AmbientModule(outerModule) && file.ExternalModuleIndicator is null;
                if (AmbientModule(module))
                {
                    if (!augmentation)
                        flags = S.ValueModule;
                    if (Has(node, K.ExportKeyword))
                        Error(
                            node,
                            Messages.X_export_modifier_cannot_be_applied_to_ambient_modules_and_module_augmentations_since_they_are_always_visible,
                            firstToken: true);
                    if (!augmentation && module.Name is StringLiteralNode literal && literal.Text.Span.Count((byte)'*') > 1)
                        Error(literal, Messages.Pattern_0_can_have_at_most_one_Asterisk_character, literal.Text);
                    else if (!augmentation && module.Name is StringLiteralNode { Text: { } moduleName }
                        && !moduleName.Span.Contains((byte)'*')
                        && module.Attributes is not null)
                        Error(
                            module.Name,
                            Messages.An_ambient_module_declaration_with_import_attributes_must_use_a_pattern_name_with_an_Asterisk_character);
                }
                Member(node, flags, flags == S.ValueModule ? S.ValueModuleExcludes : 0);
                var moduleSymbol = SymbolOf(node)!;
                if (state != 0 && !AmbientModule(module))
                {
                    if (state == 1
                        && (moduleSymbol.Flags & (S.Function | S.Class | S.RegularEnum)) == 0
                        && !notConstEnumOnly.Contains(moduleSymbol))
                        moduleSymbol.Flags |= S.ConstEnumOnlyModule;
                    else
                    {
                        moduleSymbol.Flags &= ~S.ConstEnumOnlyModule;
                        notConstEnumOnly.Add(moduleSymbol);
                    }
                }
                break;
            case K.PropertyDeclaration when node is PropertyDeclarationNode:
                Property(node, (Has(node, K.AccessorKeyword) ? S.Accessor : S.Property) | OptionalMember(node),
                    Has(node, K.AccessorKeyword) ? S.AccessorExcludes : S.PropertyExcludes);
                break;
            case K.PropertySignature or K.PropertyAssignment or K.ShorthandPropertyAssignment when node is (PropertySignatureDeclarationNode or PropertyAssignmentNode or ShorthandPropertyAssignmentNode):
                Property(node, S.Property | OptionalMember(node), S.PropertyExcludes);
                break;
            case K.EnumMember when node is EnumMemberNode:
                Property(node, S.EnumMember, S.EnumMemberExcludes);
                break;
            case K.MethodDeclaration or K.MethodSignature when node is (MethodDeclarationNode or MethodSignatureDeclarationNode):
                Property(node, S.Method | OptionalMember(node), node.Parent is ObjectLiteralExpressionNode ? S.Value : S.MethodExcludes);
                break;
            case K.GetAccessor when node is GetAccessorDeclarationNode:
                Property(node, S.GetAccessor, S.GetAccessorExcludes);
                break;
            case K.SetAccessor when node is SetAccessorDeclarationNode:
                Property(node, S.SetAccessor, S.SetAccessorExcludes);
                break;
            case K.Constructor when node is ConstructorDeclarationNode:
                Member(node, S.Constructor, 0);
                break;
            case K.CallSignature or K.ConstructSignature or K.IndexSignature when node is (CallSignatureDeclarationNode or ConstructSignatureDeclarationNode or IndexSignatureDeclarationNode):
                Member(node, S.Signature, 0);
                break;
            case K.FunctionType or K.ConstructorType when node is (FunctionTypeNode or ConstructorTypeNode):
                var signature = Anonymous(node, S.Signature, DeclarationName(node));
                var type = Anonymous(node, S.TypeLiteral, Symbol.InternalType);
                type.MemberTable[signature.Name] = signature;
                break;
            case K.TypeLiteral or K.MappedType when node is (TypeLiteralNode or MappedTypeNode):
                Anonymous(node, S.TypeLiteral, Symbol.InternalType);
                break;
            case K.ObjectLiteralExpression when node is ObjectLiteralExpressionNode:
                Anonymous(node, S.ObjectLiteral, Symbol.InternalObject);
                break;
            case K.JsxAttributes when node is JsxAttributesNode:
                Anonymous(node, S.ObjectLiteral, Symbol.InternalJsxAttributes);
                break;
            case K.JsxAttribute when node is JsxAttributeNode:
                Member(node, S.Property, S.PropertyExcludes);
                break;
            case K.ImportClause or K.ImportEqualsDeclaration or K.NamespaceImport or K.ImportSpecifier or K.ExportSpecifier when node is (ImportClauseNode { Name: not null } or ImportEqualsDeclarationNode or NamespaceImportNode or ImportSpecifierNode
                or ExportSpecifierNode):
                Member(node, S.Alias, S.AliasExcludes);
                break;
            case K.ExportDeclaration when node is (ExportDeclarationNode export):
                var owner = SymbolOf(container);
                if (owner is null)
                    Anonymous(node, S.ExportStar, Symbol.InternalExport);
                else if (export.ExportClause is null)
                    Declare(owner.ExportTable, owner, node, S.ExportStar, 0);
                else if (export.ExportClause is NamespaceExportNode clause)
                    Declare(owner.ExportTable, owner, clause, S.Alias, S.AliasExcludes);
                break;
            case K.ExportAssignment when node is (ExportAssignmentNode export):
                var exportOwner = SymbolOf(container);
                if (exportOwner is null)
                    Anonymous(node, S.Value, DeclarationName(node));
                else
                {
                    var symbol = Declare(
                        exportOwner.ExportTable,
                        exportOwner,
                        node,
                        AliasExpression(export.Expression) ? S.Alias : S.Property,
                        S.All);
                    if (export.IsExportEquals)
                        SetValue(symbol, node);
                }
                break;
            case K.NamespaceExportDeclaration when node is NamespaceExportDeclarationNode:
                if (node is IModifiedNode { Modifiers.Count: > 0 })
                    Error(node, Messages.Modifiers_cannot_appear_here);
                if (node.Parent != file)
                    Error(node, Messages.Global_module_exports_may_only_appear_at_top_level);
                else if (file.ExternalModuleIndicator is null)
                    Error(node, Messages.Global_module_exports_may_only_appear_in_module_files);
                else if (!file.IsDeclarationFile)
                    Error(node, Messages.Global_module_exports_may_only_appear_in_declaration_files);
                else
                    Declare(globalExports, SymbolOf(file), node, S.Alias, S.AliasExcludes);
                break;
            case K.BinaryExpression when node is (BinaryExpressionNode binary):
                if (binary.OperatorToken?.Kind == K.EqualsToken)
                    BindAssignmentDeclaration(binary);
                if (Assignment(binary.OperatorToken?.Kind ?? 0))
                    CheckEval(node, binary.Left);
                break;
            case K.CallExpression when node is (CallExpressionNode call):
                if (file.ScriptKind is ScriptKind.JS or ScriptKind.JSX
                    && call.Expression is IdentifierNode { Text.Span: var matchedText2 } && matchedText2.SequenceEqual("require"u8)
                    && call.Arguments?.Count == 1)
                    CommonJS(node);
                BindDefineProperty(call);
                break;
            case K.DeleteExpression when node is (DeleteExpressionNode { Expression: IdentifierNode identifier }):
                Error(identifier, Messages.X_delete_cannot_be_called_on_an_identifier_in_strict_mode);
                break;
            case K.CatchClause when node is (CatchClauseNode { VariableDeclaration: { } variable }):
                CheckEval(node, Name(variable));
                break;
            case K.PostfixUnaryExpression when node is (PostfixUnaryExpressionNode postfix):
                CheckEval(node, postfix.Operand);
                break;
            case K.PrefixUnaryExpression when node is (PrefixUnaryExpressionNode { Operator: K.PlusPlusToken or K.MinusMinusToken } prefix):
                CheckEval(node, prefix.Operand);
                break;
            case K.WithStatement when node is WithStatementNode:
                Error(node, Messages.X_with_statements_are_not_allowed_in_strict_mode, firstToken: true);
                break;
            case K.LabeledStatement when node is (LabeledStatementNode label) && label.Statement?.Kind is K.VariableStatement or K.FunctionDeclaration or K.ClassDeclaration
                or K.InterfaceDeclaration or K.TypeAliasDeclaration or K.EnumDeclaration or K.ModuleDeclaration or K.ImportEqualsDeclaration:
                Error(label.Label!, Messages.A_label_is_not_allowed_here, firstToken: true);
                break;
        }
        if (node.Kind == K.MetaProperty)
            Data(node).Flow = currentFlow;
        if (node.Kind == K.ThisType)
            seenThis = true;
    }

    private Symbol NewSymbol(S flags, Utf8String name, Symbol? parent = null)
    {
        result.SymbolCount++;
        return new(flags, name) { Parent = parent };
    }

    private static S OptionalMember(SyntaxNode node) => node switch
    {
        PropertyDeclarationNode { PostfixToken.Kind: K.QuestionToken }
            or PropertySignatureDeclarationNode { PostfixToken.Kind: K.QuestionToken }
            or MethodDeclarationNode { PostfixToken.Kind: K.QuestionToken }
            or MethodSignatureDeclarationNode { PostfixToken.Kind: K.QuestionToken } => S.Optional,
        _ => 0
    };

    private Symbol Anonymous(SyntaxNode node, S flags, Utf8String name)
    {
        var symbol = NewSymbol(flags, name, (flags & (S.EnumMember | S.ClassMember)) != 0 ? SymbolOf(container) : null);
        AddDeclaration(symbol, node, flags);
        return symbol;
    }

    private void AddDeclaration(Symbol symbol, SyntaxNode node, S flags)
    {
        symbol.Flags |= flags;
        if (!symbol.DeclarationList.Contains(node))
            symbol.DeclarationList = symbol.DeclarationList.Add(node);
        Data(node).Symbol = symbol;
        if ((symbol.Flags & S.ConstEnumOnlyModule) != 0 && (symbol.Flags & (S.Function | S.Class | S.RegularEnum)) != 0)
        {
            symbol.Flags &= ~S.ConstEnumOnlyModule;
            notConstEnumOnly.Add(symbol);
        }
        if ((flags & S.Value) != 0)
            SetValue(symbol, node);
    }

    private static void SetValue(Symbol symbol, SyntaxNode node)
    {
        if (symbol.ValueDeclaration is not { } existing
            || existing is BinaryExpressionNode or CallExpressionNode && node is not (BinaryExpressionNode or CallExpressionNode)
            || existing.Kind != node.Kind && existing.Kind == K.ModuleDeclaration)
            symbol.ValueDeclaration = node;
    }

    private Symbol Declare(SymbolTable table, Symbol? parent, SyntaxNode node, S includes, S excludes,
        Utf8String? suppliedName = null, bool replaceable = false)
    {
        bool isDefault = Has(node, K.DefaultKeyword)
            || node is ExportSpecifierNode { Name: { } exportName } && NameText(exportName) == Utf8Literals.Default;
        Utf8String name = suppliedName ?? (isDefault && parent is not null ? Utf8Literals.Default : DeclarationName(node));
        bool missing = name == Symbol.InternalMissing;
        Symbol symbol;
        bool existing = false;
        if (missing)
            symbol = NewSymbol(replaceable ? S.ReplaceableByMethod : 0, name);
        else
        {
            ref Symbol? entry = ref table.GetValueRefOrAddDefault(name, out existing);
            if (!existing)
                entry = NewSymbol(replaceable ? S.ReplaceableByMethod : 0, name);
            symbol = entry!;
        }
        if (existing && replaceable && (symbol.Flags & S.ReplaceableByMethod) == 0)
            return symbol;
        else if (existing && (symbol.Flags & excludes) != 0)
        {
            if ((symbol.Flags & S.ReplaceableByMethod) != 0)
                table[name] = symbol = NewSymbol(0, name);
            else if (!((includes & S.Variable) != 0 && (symbol.Flags & S.Assignment) != 0
                || (includes & S.Assignment) != 0 && (symbol.Flags & S.Variable) != 0))
            {
                var message = (symbol.Flags & S.BlockScopedVariable) != 0
                    ? Messages.Cannot_redeclare_block_scoped_variable_0
                    : Messages.Duplicate_identifier_0;
                bool needsName = true;
                if (((symbol.Flags | includes) & S.Enum) != 0)
                {
                    message = Messages.Enum_declarations_can_only_merge_with_namespace_or_other_enum_declarations;
                    needsName = false;
                }
                bool multipleDefaults = symbol.Declarations.Length > 0
                    && (isDefault || node is ExportAssignmentNode { IsExportEquals: false });
                if (multipleDefaults)
                {
                    message = Messages.A_module_cannot_have_multiple_default_exports;
                    needsName = false;
                }
                SyntaxNode declarationName = Name(node) ?? node;
                var current = CreateDiagnostic(declarationName, message, needsName ? [DisplayName(node)] : []);
                var related = new List<Diagnostic>();
                if (node is TypeAliasDeclarationNode { Type: { } type, Name: { } alias } && type.Pos == type.End
                    && Has(node, K.ExportKeyword) && (symbol.Flags & (S.Alias | S.Type | S.Namespace)) != 0)
                    related.Add(CreateDiagnostic(node, Messages.Did_you_mean_0, [Utf8String.Concat("export type { "u8, alias.Text, " }"u8)]));
                for (int i = 0; i < symbol.Declarations.Length; i++)
                {
                    var previous = symbol.Declarations[i];
                    SyntaxNode previousName = Name(previous) ?? previous;
                    var diagnostic = CreateDiagnostic(previousName, message, needsName ? [DisplayName(previous)] : []);
                    if (multipleDefaults)
                    {
                        diagnostic = diagnostic with
                        {
                            RelatedInformation = [CreateDiagnostic(declarationName,
                            i == 0 ? Messages.Another_export_default_is_here : Messages.X_and_here)]
                        };
                        related.Add(CreateDiagnostic(previousName, Messages.The_first_export_default_is_here));
                    }
                    diagnostics.Add(diagnostic);
                }
                diagnostics.Add(current with { RelatedInformation = related.ToArray() });
                if ((symbol.Flags & S.Accessor) != 0 && (symbol.Flags & S.Accessor) != (includes & S.Accessor))
                    symbol.Flags |= S.Accessor;
                symbol = NewSymbol(0, name);
            }
        }
        AddDeclaration(symbol, node, includes);
        if (symbol.Parent is null)
            symbol.Parent = parent;
        else if (!ReferenceEquals(symbol.Parent, parent))
            throw new InvalidOperationException("Existing symbol parent must match new declaration");
        return symbol;
    }

    private Symbol Member(SyntaxNode node, S flags, S excludes)
    {
        var symbol = SymbolOf(container);
        if (container is ModuleDeclarationNode || container == file && file.ExternalModuleIndicator is not null)
            return ModuleMember(node, flags, excludes);
        if (ClassLike(container))
            return Declare(Has(node, K.StaticKeyword) ? symbol!.ExportTable : symbol!.MemberTable, symbol, node, flags, excludes);
        if (container is EnumDeclarationNode)
            return Declare(symbol!.ExportTable, symbol, node, flags, excludes);
        if (container.Kind is K.TypeLiteral or K.ObjectLiteralExpression or K.InterfaceDeclaration or K.JsxAttributes)
            return Declare(symbol!.MemberTable, symbol, node, flags, excludes);
        return Declare(Data(container).LocalTable, null, node, flags, excludes);
    }

    private Symbol ModuleMember(SyntaxNode node, S flags, S excludes)
    {
        var symbol = SymbolOf(container)!;
        bool exported = CombinedHas(node, K.ExportKeyword) || node.Parent == file && result.IsModule
            && (node.Kind == K.JSTypeAliasDeclaration || node.Kind == K.ModuleDeclaration && (node.Flags & NodeFlags.Reparsed) != 0);
        if ((flags & S.Alias) != 0)
            return node is ExportSpecifierNode || node is ImportEqualsDeclarationNode && exported
                ? Declare(
                    symbol.ExportTable,
                    symbol,
                    node,
                    flags,
                    excludes) : Declare(Data(container).LocalTable, null, node, flags, excludes);
        if (!AmbientModule(node) && (exported || (Data(container).Flags & NodeFlags.ExportContext) != 0))
        {
            if (Has(node, K.DefaultKeyword) && Name(node) is null)
                return Declare(symbol.ExportTable, symbol, node, flags, excludes);
            var local = Declare(Data(container).LocalTable, null, node, (flags & S.Value) != 0 ? S.ExportValue : 0, excludes);
            local.ExportSymbol = Declare(symbol.ExportTable, symbol, node, flags, excludes);
            Data(node).LocalSymbol = local;
            return local;
        }
        return Declare(Data(container).LocalTable, null, node, flags, excludes);
    }

    private void BlockMember(SyntaxNode node, S flags, S excludes)
    {
        if (blockContainer is ModuleDeclarationNode || blockContainer == file && result.IsModule)
            ModuleMember(node, flags, excludes);
        else
            Declare(Data(blockContainer).LocalTable, null, node, flags, excludes);
    }

    private void Property(SyntaxNode node, S flags, S excludes)
    {
        if (node.Kind is K.MethodDeclaration or K.GetAccessor or K.SetAccessor
            && node.Parent?.Kind is K.ObjectLiteralExpression or K.ClassExpression)
            Data(node).Flow = currentFlow;
        if (Name(node) is ComputedPropertyNameNode computed && LiteralName(computed.Expression) is null)
            Anonymous(node, flags, Symbol.InternalComputed);
        else
            Member(node, flags, excludes);
    }

    private static Utf8String Internal(Utf8String text) => Utf8String.Concat(Symbol.InternalPrefix, text);

    private static Utf8String? LiteralName(SyntaxNode? node) => node switch
    {
        StringLiteralNode n => n.Text,
        NumericLiteralNode n => n.Text,
        NoSubstitutionTemplateLiteralNode n => n.Text,
        PrefixUnaryExpressionNode { Operator: K.PlusToken or K.MinusToken, Operand: NumericLiteralNode n } p => Utf8String.Concat(TokenFacts.Text(p.Operator), n.Text),
        _ => (Utf8String?)null
    };

    private static Utf8String NameText(SyntaxNode node) => node switch
    {
        IdentifierNode n => n.Text,
        PrivateIdentifierNode n => n.Text,
        JsxNamespacedNameNode n => Utf8String.Concat(NameText(n.Namespace!), ":"u8, NameText(n.Name!)),
        _ => LiteralName(node) ?? Utf8String.Empty
    };

    private Utf8String DeclarationName(SyntaxNode node)
    {
        if (node is ExportAssignmentNode export)
            return export.IsExportEquals ? Utf8Literals.ExportEquals : Utf8Literals.Default;
        if (Name(node) is { } name)
        {
            if (AmbientModule(node))
            {
                if (node is ModuleDeclarationNode { Keyword: K.GlobalKeyword })
                    return Symbol.InternalGlobal;
                if (node is ModuleDeclarationNode { Attributes: { } attributes } && NameText(name).Span.Count((byte)'*') == 1)
                {
                    return Internal(Utf8String.ConcatMany(Utf8Literals.DoubleQuote, NameText(name), Utf8Literals.Pattern, Utf8String.Format(PatternIdentities.GetValue(attributes, static _ => new()).Value)));
                }
                return Utf8String.Concat("\""u8, NameText(name), "\""u8);
            }
            if (name is PrivateIdentifierNode p)
                return ContainingClass(node) is { } owner ? Internal(Utf8String.ConcatMany(Utf8Literals.Hash, Utf8String.Format(SymbolOf(owner)!.Id), Utf8Literals.At, p.Text)) : (Symbol.InternalMissing);
            if (name is ComputedPropertyNameNode computed)
                return LiteralName(computed.Expression) ?? Symbol.InternalComputed;
            if (name is ElementAccessExpressionNode access)
                return LiteralName(SkipParentheses(access.ArgumentExpression)) is not null ? (Symbol.InternalMissing) : (Symbol.InternalComputed);
            if (name is not (IdentifierNode or JsxNamespacedNameNode) && LiteralName(name) is null)
                return Symbol.InternalMissing;
            return NameText(name);
        }
        return node.Kind switch
        {
            K.Constructor => Symbol.InternalConstructor,
            K.FunctionType or K.CallSignature => Symbol.InternalCall,
            K.ConstructorType or K.ConstructSignature => Symbol.InternalNew,
            K.IndexSignature => Symbol.InternalIndex,
            K.ExportDeclaration => Symbol.InternalExport,
            K.SourceFile or K.BinaryExpression => Utf8Literals.ExportEquals,
            _ => Symbol.InternalMissing
        };
    }

    private Utf8String DisplayName(SyntaxNode node)
    {
        if (node is ExportAssignmentNode)
            return DeclarationName(node);
        if (Name(node) is not { } name)
        {
            Utf8String value = DeclarationName(node);
            return value == Symbol.InternalMissing ? Utf8Literals.MissingDisplay : value;
        }
        return SourceName(name);
    }

    private Utf8String SourceName(SyntaxNode name)
    {
        if (name.Pos == name.End)
            return Utf8Literals.MissingDisplay;
        if (name.Pos < 0)
            return NameText(name);
        if ((name.Flags & NodeFlags.ReparserTransformedLiteral) != 0 && name is IdentifierNode identifier)
            return identifier.Text;
        var scanner = new Scanner(file.Source);
        scanner.ResetPosition(name.Pos);
        scanner.Scan();
        return file.Source.Text[scanner.TokenStart..name.End];
    }

    private void ExternalModule() =>
        Anonymous(file, S.ValueModule, Utf8String.Concat("\""u8, file.FileName.AsSpan()[..^ModuleResolver.Extension(file.FileName).Length], "\""u8));

    private bool CommonJS(SyntaxNode node)
    {
        if (file.ExternalModuleIndicator is { } indicator && indicator != file)
            return false;
        if (result.CommonJSModuleIndicator is null)
        {
            result.CommonJSModuleIndicator = node;
            if (file.ExternalModuleIndicator is null)
                ExternalModule();
        }
        return true;
    }

    private void SetExportContext(SyntaxNode node)
    {
        var statements = Statements(node is ModuleDeclarationNode module ? module.Body : node);
        if ((node.Flags & NodeFlags.Ambient) != 0 && statements?.Any(n => n.Kind is K.ExportDeclaration or K.ExportAssignment) != true)
            Data(node).Flags |= NodeFlags.ExportContext;
        else
            Data(node).Flags &= ~NodeFlags.ExportContext;
    }

    internal static int ModuleState(ModuleDeclarationNode node)
    {
        var states = new Dictionary<SyntaxNode, int>(ReferenceEqualityComparer.Instance);
        var dependencies = new Dictionary<SyntaxNode, SyntaxNode[]>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(SyntaxNode Node, bool Exit)>();
        stack.Push((node, false));
        while (stack.TryPop(out var item))
        {
            if (item.Exit)
            {
                foreach (var dependency in dependencies[item.Node])
                    states[item.Node] = Math.Max(states[item.Node], states.GetValueOrDefault(dependency));
                continue;
            }
            if (states.ContainsKey(item.Node))
                continue;
            states[item.Node] = 0;
            SyntaxNode[] children = [];
            switch (item.Node)
            {
                case ModuleDeclarationNode { Body: { } body }:
                    children = [body];
                    break;
                case ModuleBlockNode block:
                    children = block.Statements?.ToArray() ?? [];
                    break;
                case InterfaceDeclarationNode or TypeAliasDeclarationNode:
                    break;
                case EnumDeclarationNode enumNode:
                    states[item.Node] = Has(enumNode, K.ConstKeyword) ? 1 : 2;
                    break;
                case ImportDeclarationNode or ImportEqualsDeclarationNode when !Has(item.Node, K.ExportKeyword):
                    break;
                case ExportDeclarationNode { ModuleSpecifier: null, ExportClause: NamedExportsNode exports }:
                    var targets = new List<SyntaxNode>();
                    foreach (ExportSpecifierNode specifier in exports.Elements ?? new NodeList([]))
                    {
                        if ((specifier.PropertyName ?? specifier.Name) is not IdentifierNode identifier)
                        {
                            states[item.Node] = 2;
                            break;
                        }
                        bool found = false;
                        for (SyntaxNode? parent = specifier.Parent; parent is not null && !found; parent = parent.Parent)
                            foreach (var statement in (IEnumerable<SyntaxNode>?)Statements(parent) ?? [])
                            {
                                bool matches = Name(statement) is IdentifierNode name && name.Text == identifier.Text
                                    || statement is VariableStatementNode { DeclarationList.Declarations: { } declarations }
                                        && declarations.Any(d => Name(d) is IdentifierNode variable && variable.Text == identifier.Text);
                                if (!matches)
                                    continue;
                                found = true;
                                if (statement is ImportEqualsDeclarationNode)
                                    states[item.Node] = 2;
                                else
                                    targets.Add(statement);
                            }
                        if (!found)
                            states[item.Node] = 2;
                    }
                    children = targets.ToArray();
                    break;
                default:
                    states[item.Node] = 2;
                    break;
            }
            dependencies[item.Node] = children;
            stack.Push((item.Node, true));
            for (int i = children.Length - 1; i >= 0; i--)
                stack.Push((children[i], false));
        }
        return states[node];
    }
}
