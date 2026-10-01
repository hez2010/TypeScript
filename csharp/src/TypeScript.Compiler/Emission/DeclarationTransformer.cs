using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed partial class DeclarationTransformer : SyntaxRewriter
{
    private readonly Checker checker;
    private readonly CompilerOptions options;
    private readonly Utf8String declarationFilePath;
    private readonly DeclarationSymbolTracker tracker;
    private readonly Dictionary<SyntaxNode, SyntaxNode?> lateStatements = [];
    private readonly List<(SyntaxNode Node, int DiagnosticIndex)> inferenceFallbacks = [];
    private SourceFileNode source = null!;
    private SyntaxNode enclosing = null!;
    private bool needsDeclare, needsScopeMarker, hasScopeMarker, hasModuleIndicator, suppressDiagnostics, classExpression;
    private NodeFactory F => Context.Factory;
    private const NodeBuilderFlags BuilderFlags = NodeBuilderFlags.MultilineObjectLiterals | NodeBuilderFlags.WriteClassExpressionAsTypeLiteral
        | NodeBuilderFlags.UseTypeOfFunction | NodeBuilderFlags.UseStructuralFallback | NodeBuilderFlags.AllowEmptyTuple
        | NodeBuilderFlags.GenerateNamesForShadowedTypeParams | NodeBuilderFlags.NoTruncation;
    private const NodeBuilderInternalFlags InternalFlags = NodeBuilderInternalFlags.AllowUnresolvedNames;
    private NodeBuilderFlags CurrentBuilderFlags => classExpression ? BuilderFlags & ~NodeBuilderFlags.WriteClassExpressionAsTypeLiteral : BuilderFlags;
    internal IReadOnlyList<Diagnostic> Diagnostics => tracker.Diagnostics;

    internal DeclarationTransformer(EmitContext context, Checker checker, CompilerOptions options, CancellationToken cancellation = default,
        Utf8String declarationFilePath = default)
        : base(context, cancellation)
    {
        this.checker = checker;
        this.options = options;
        this.declarationFilePath = declarationFilePath;
        tracker = new(QueueInferenceFallback);
    }

    protected override ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node) => node switch
    {
        SourceFileNode file => SourceAsync(file),
        ExportDeclarationNode export => ExportAsync(export),
        NamespaceExportDeclarationNode export => ValueTask.FromResult<SyntaxNode?>(StripInternal(export) ? null : export),
        ExportAssignmentNode export => ExportAssignmentAsync(export, export, export.Expression!, export.IsExportEquals),
        _ when LateStatement(node) => DeferAsync(node),
        _ when TypeNodeFlow.Statement(node) => ValueTask.FromResult<SyntaxNode?>(null),
        _ => SubtreeAsync(node)
    };

    protected override ValueTask<NodeList?> VisitParametersAsync(NodeList? nodes) => VisitListAsync(nodes);
    protected override ValueTask<SyntaxNode?> VisitFunctionBodyAsync(SyntaxNode? node) => ValueTask.FromResult<SyntaxNode?>(null);

    private async ValueTask<SyntaxNode?> SourceAsync(SourceFileNode file)
    {
        if (file.IsDeclarationFile) return file;
        source = file; enclosing = file;
        needsDeclare = true;
        needsScopeMarker = hasScopeMarker = hasModuleIndicator = suppressDiagnostics = classExpression = false;
        lateStatements.Clear(); tracker.LateStatements.Clear(); tracker.Diagnostics.Clear(); inferenceFallbacks.Clear();
        expandoHosts.Clear(); expandoMembers.Clear(); deferredExpandos.Clear(); witnessedExports.Clear(); cjsExports.Clear();
        cjsAssignment = null; cjsAssignmentName = null;
        tracker.DiagnosticSelector = null; tracker.ErrorName = null;
        await checker.PrecalculateDeclarationEmitVisibilityAsync((SourceFileNode)Context.MostOriginal(file), Cancellation);
        try
        {
            await CollectAssignmentsAsync(file);
            var statements = await ReplaceLateAsync(await VisitListAsync(file.Statements));
            if (cjsAssignment is not null || cjsExports.Count != 0)
                statements = new([.. cjsAssignment is null ? [] : Parts(cjsAssignment), .. cjsExports.SelectMany(Parts), .. statements ?? new([])],
                    statements?.Pos ?? -1, statements?.End ?? -1);
            if ((file.Flags & NodeFlags.JavaScriptFile) != 0 && checker.Symbols.Binding(file)?.Symbol?.Exports.GetValueOrDefault("export="u8) is { Declarations.Length: > 1 } exportEquals)
                foreach (var declaration in exportEquals.Declarations)
                    Report(declaration, Messages.Multiple_module_exports_assignments_cannot_be_serialized_for_declaration_emit);
            if ((file.ExternalModuleIndicator is not null || checker.Symbols.Binding(file)?.CommonJSModuleIndicator is not null)
                && (!hasModuleIndicator || needsScopeMarker && !hasScopeMarker))
                statements = new([.. statements ?? new([]), ModuleUtilities.EmptyExport(F)], statements?.Pos ?? -1, statements?.End ?? -1);
            var result = Context.Clone(file);
            result.Statements = statements;
            result.IsDeclarationFile = true;
            result.ReferencedFiles = References(file);
            result.TypeReferenceDirectives = file.TypeReferenceDirectives.Where(reference => reference.Preserve).Select(reference => reference with { Pos = -1, End = -1 }).ToArray();
            result.LibReferenceDirectives = file.LibReferenceDirectives.Where(reference => reference.Preserve).Select(reference => reference with { Pos = -1, End = -1 }).ToArray();
            await FlushInferenceAsync();
            return result;
        }
        finally { source = null!; enclosing = null!; tracker.DiagnosticSelector = null; tracker.ErrorName = null; }
    }

    private ValueTask<SyntaxNode?> ExportAsync(ExportDeclarationNode node)
    {
        if (StripInternal(node)) return ValueTask.FromResult<SyntaxNode?>(null);
        if (node.Parent is SourceFileNode) hasModuleIndicator = true;
        hasScopeMarker = true;
        return ValueTask.FromResult<SyntaxNode?>(node);
    }

    private async ValueTask<SyntaxNode?> DeferAsync(SyntaxNode node)
    {
        if (StripInternal(node)) return null;
        var original = Context.MostOriginal(node);
        if (!lateStatements.ContainsKey(original)) lateStatements[original] = await TopLevelAsync(node);
        return node;
    }

    private async ValueTask<NodeList?> ReplaceLateAsync(NodeList? statements)
    {
        while (tracker.LateStatements.Count != 0)
        {
            Cancellation.ThrowIfCancellationRequested();
            var next = tracker.LateStatements[0];
            tracker.LateStatements.RemoveAt(0);
            var saved = needsDeclare;
            try { needsDeclare = next.Parent is SourceFileNode; lateStatements[Context.MostOriginal(next)] = await TopLevelAsync(next); }
            finally { needsDeclare = saved; }
        }
        if (statements is null) return null;
        List<SyntaxNode> results = [];
        foreach (var statement in statements)
        {
            if (!LateStatement(statement)) { results.Add(statement); continue; }
            var original = Context.MostOriginal(statement);
            if (!lateStatements.Remove(original, out var replacement)) { results.Add(statement); continue; }
            if (replacement is null) continue;
            foreach (var part in Parts(replacement))
            {
                needsScopeMarker |= part is not (ImportDeclarationNode or ImportEqualsDeclarationNode or ExportDeclarationNode or ExportAssignmentNode)
                    && !SemanticSyntax.HasModifier(part, K.ExportKeyword) && part is not ModuleDeclarationNode { Name: StringLiteralNode }
                    && part is not ModuleDeclarationNode { Keyword: K.GlobalKeyword };
                hasModuleIndicator |= statement.Parent is SourceFileNode && ModuleUtilities.IsIndicator(part);
                results.Add(part);
            }
        }
        return new(results.ToArray(), statements.Pos, statements.End);
    }

    private async ValueTask<SyntaxNode?> TopLevelAsync(SyntaxNode node)
    {
        tracker.LateStatements.RemoveAll(candidate => candidate == node);
        if (StripInternal(node)) return null;
        if (node is ImportDeclarationNode import) return await ImportAsync(import);
        if (node is ImportEqualsDeclarationNode alias) return await ImportEqualsAsync(alias);
        if (await InvisibleAsync(node) || node.HasFunctionSignature && await checker.IsImplementationOfOverloadAsync(node, Cancellation)) return null;
        var original = Context.MostOriginal(node);
        if (expandoHosts.ContainsKey(original) || deferredExpandos.ContainsKey(original)) return await FullExpandoAsync(original);
        var saved = SaveContext();
        try
        {
            if (Enclosing(node)) enclosing = node;
            SetDiagnosticContext(node, force: true);
            return node switch
            {
                TypeAliasDeclarationNode type => await TypeAliasAsync(type),
                InterfaceDeclarationNode type => await InterfaceAsync(type),
                FunctionDeclarationNode function => await FunctionAsync(function),
                ModuleDeclarationNode module => await ModuleAsync(module),
                ClassDeclarationNode type => await ClassAsync(type),
                VariableStatementNode variables => await VariablesAsync(variables),
                EnumDeclarationNode enumeration => await EnumAsync(enumeration),
                _ => throw new InvalidOperationException($"Unhandled declaration statement: {node.Kind}")
            };
        }
        finally { RestoreContext(saved); }
    }

    private async ValueTask<SyntaxNode?> SubtreeAsync(SyntaxNode node)
    {
        if (StripInternal(node) || await InvisibleAsync(node) || node is SemicolonClassElementNode) return null;
        if (SemanticSyntax.Name(node) is ComputedPropertyNameNode { Expression: { } expression } && !LiteralName(expression))
        {
            if (options.IsolatedDeclarations == true)
            {
                if (!await checker.IsGlobalSymbolObjectReferenceAsync(expression, Cancellation))
                {
                    if (node.Parent is ClassDeclarationNode or ObjectLiteralExpressionNode)
                    {
                        Report(node, Messages.Computed_property_names_on_class_or_object_literals_cannot_be_inferred_with_isolatedDeclarations);
                        return null;
                    }
                    if (node.Parent is InterfaceDeclarationNode or TypeLiteralNode && !ConstantEvaluator.EntityName(expression))
                    {
                        Report(node, Messages.Computed_properties_must_be_number_or_string_literals_variables_or_dotted_expressions_with_isolatedDeclarations);
                        return null;
                    }
                }
            }
            else if (!await checker.IsLateBoundDeclarationAsync(node, Cancellation) || !ConstantEvaluator.EntityName(expression)) return null;
        }
        if (node.HasFunctionSignature && await checker.IsImplementationOfOverloadAsync(node, Cancellation)) return null;
        var saved = SaveContext();
        try
        {
            if (Enclosing(node)) enclosing = node;
            SetDiagnosticContext(node);
            if (node is TypeLiteralNode or MappedTypeNode && node.Parent is not TypeAliasDeclarationNode) suppressDiagnostics = true;
            var result = await SubtreeWorkerAsync(node);
            if (result is not null && CanDiagnose(node) && SemanticSyntax.Name(node) is ComputedPropertyNameNode { Expression: { } name } && !LiteralName(name))
                await CheckNameAsync(node, name);
            return result;
        }
        finally { RestoreContext(saved); }
    }

    private (SyntaxNode Enclosing, Func<SymbolAccessibilityResult, DeclarationDiagnostics.Info?>? Diagnostic, SyntaxNode? Name, bool Suppress, bool Declare) SaveContext() =>
        (enclosing, tracker.DiagnosticSelector, tracker.ErrorName, suppressDiagnostics, needsDeclare);
    private void RestoreContext((SyntaxNode Enclosing, Func<SymbolAccessibilityResult, DeclarationDiagnostics.Info?>? Diagnostic, SyntaxNode? Name, bool Suppress, bool Declare) saved) =>
        (enclosing, tracker.DiagnosticSelector, tracker.ErrorName, suppressDiagnostics, needsDeclare) = saved;
    private void SetDiagnosticContext(SyntaxNode node, bool force = false)
    {
        if (CanDiagnose(node) && (force || !suppressDiagnostics)) tracker.DiagnosticSelector = result => DeclarationDiagnostics.ForNode(node, result);
    }
    private async ValueTask CheckNameAsync(SyntaxNode node, SyntaxNode name)
    {
        var saved = SaveContext();
        try
        {
            if (!suppressDiagnostics) tracker.DiagnosticSelector = result => DeclarationDiagnostics.ForNode(node, result, nameOnly: true);
            tracker.ErrorName = SemanticSyntax.Name(node);
            await CheckEntityAsync(name);
        }
        finally { RestoreContext(saved); }
    }
    private async ValueTask CheckEntityAsync(SyntaxNode name) => tracker.HandleAccessibility(await checker.GetEntityNameVisibilityAsync(name, enclosing, Cancellation));
    private void Report(SyntaxNode node, DiagnosticMessage message, params Utf8String[] arguments) => tracker.Diagnostics.Add(CheckerDiagnostic.Create(node, message, arguments));
    private void QueueInferenceFallback(SyntaxNode node)
    {
        if (options.IsolatedDeclarations == true) inferenceFallbacks.Add((node, tracker.Diagnostics.Count));
    }
    private async ValueTask FlushInferenceAsync()
    {
        var pending = inferenceFallbacks.ToArray();
        inferenceFallbacks.Clear();
        int inserted = 0;
        foreach (var (node, index) in pending)
        {
            if (SemanticSyntax.Source(node) != source) continue;
            int start = tracker.Diagnostics.Count;
            if (await checker.IsExpandoFunctionForEmitAsync(node, Cancellation)) await ReportExpandoErrorsAsync(node);
            if (!await ChildOfExpandoAsync(node)) tracker.Diagnostics.Add(await IsolatedDeclarationDiagnostics.CreateAsync(node, checker, Cancellation));
            var diagnostics = tracker.Diagnostics.GetRange(start, tracker.Diagnostics.Count - start);
            tracker.Diagnostics.RemoveRange(start, diagnostics.Count);
            tracker.Diagnostics.InsertRange(index + inserted, diagnostics);
            inserted += diagnostics.Count;
        }
    }
    private static IEnumerable<SyntaxNode> Parts(SyntaxNode node) => node is SyntaxListNode list ? list.Children : [node];
    private static bool LateStatement(SyntaxNode node) => node is FunctionDeclarationNode or ModuleDeclarationNode or ImportEqualsDeclarationNode
        or InterfaceDeclarationNode or ClassDeclarationNode or TypeAliasDeclarationNode or EnumDeclarationNode or VariableStatementNode or ImportDeclarationNode;
    private static bool Enclosing(SyntaxNode node) => node is SourceFileNode or TypeAliasDeclarationNode or ModuleDeclarationNode or ClassDeclarationNode
        or InterfaceDeclarationNode or IndexSignatureDeclarationNode or MappedTypeNode or VariableDeclarationNode || node.HasFunctionSignature;
    private static bool CanDiagnose(SyntaxNode node) => node is VariableDeclarationNode or PropertyDeclarationNode or PropertySignatureDeclarationNode
        or BindingElementNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode or ConstructSignatureDeclarationNode or CallSignatureDeclarationNode
        or MethodDeclarationNode or MethodSignatureDeclarationNode or FunctionDeclarationNode or ParameterDeclarationNode or TypeParameterDeclarationNode
        or ExpressionWithTypeArgumentsNode or ImportEqualsDeclarationNode or TypeAliasDeclarationNode or ConstructorDeclarationNode or IndexSignatureDeclarationNode
        or PropertyAccessExpressionNode or ElementAccessExpressionNode or BinaryExpressionNode or CallExpressionNode;
    private static bool LiteralName(SyntaxNode node) => node is StringLiteralNode or NumericLiteralNode
        or PrefixUnaryExpressionNode { Operator: K.PlusToken or K.MinusToken, Operand: NumericLiteralNode } || node.Kind == K.NoSubstitutionTemplateLiteral;
    private async ValueTask<bool> InvisibleAsync(SyntaxNode node) => node switch
    {
        FunctionDeclarationNode or ModuleDeclarationNode or InterfaceDeclarationNode or ClassDeclarationNode or TypeAliasDeclarationNode or EnumDeclarationNode =>
            !await checker.IsDeclarationVisibleAsync(Context.MostOriginal(node), Cancellation),
        VariableDeclarationNode => !await BindingVisibleAsync(node),
        ClassStaticBlockDeclarationNode => true,
        _ => false
    };
    private async ValueTask<bool> BindingVisibleAsync(SyntaxNode node)
    {
        Stack<SyntaxNode> pending = new(); pending.Push(node);
        while (pending.TryPop(out var current))
        {
            Cancellation.ThrowIfCancellationRequested();
            if (SemanticSyntax.Name(current) is BindingPatternNode pattern)
                foreach (var element in pattern.Elements ?? new([])) pending.Push(element);
            else if (SemanticSyntax.Name(current) is not null && await checker.IsDeclarationVisibleAsync(current, Cancellation)) return true;
        }
        return false;
    }
    private bool StripInternal(SyntaxNode node)
    {
        if (options.StripInternal != true || source is null || Context.ParseNode(node) is not { } original) return false;
        IReadOnlyList<SourceCommentRange> comments;
        if (original is ParameterDeclarationNode && original.Parent is IFunctionSignature { Parameters: { } parameters })
        {
            var index = parameters.IndexOf(original);
            int pos = index > 0 ? parameters[index - 1].End + 1 : original.Pos;
            var bytes = source.Source.Text.Span;
            while (pos >= 0 && pos < bytes.Length && bytes[pos] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') pos++;
            var trailing = SyntaxPrinter.CommentRanges(source.Source.Text, pos, trailing: true);
            var leading = index > 0 ? SyntaxPrinter.CommentRanges(source.Source.Text, original.Pos, trailing: false) : [];
            comments = leading.Count != 0 ? [leading[^1]] : trailing.Count != 0 ? [trailing[^1]] : [];
        }
        else comments = SyntaxPrinter.CommentRanges(source.Source.Text, original.Pos, trailing: false);
        return comments.Any(comment => source.Source.Text.Span[comment.Pos..comment.End].IndexOf("@internal"u8) >= 0);
    }
}
