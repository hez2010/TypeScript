using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal sealed partial class CommonJsModuleTransformer(EmitContext context, CompilerOptions options, Checker checker,
    Func<SourceFileNode, ModuleKind> moduleFormat, CancellationToken cancellation = default) : SyntaxRewriter(context, cancellation)
{
    private enum Mode { Normal, TopLevel, Nested }
    private Mode mode;
    private bool discarded;
    private SyntaxNode? current, parent;
    private SourceFileNode? source;
    private ExternalModuleInfo info = null!;
    private NodeFactory F => Context.Factory;

    protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
    {
        var saved = (current, parent, mode, discarded);
        parent = current; current = node; mode = Mode.Normal; discarded = false;
        try
        {
            if (node is SourceFileNode file) return await SourceAsync(file);
            if (saved.mode == Mode.TopLevel)
            {
                switch (node)
                {
                    case ImportDeclarationNode import: return await ImportAsync(import);
                    case ImportEqualsDeclarationNode import: return await ImportEqualsAsync(import);
                    case ExportDeclarationNode export: return ExportDeclaration(export);
                    case ExportAssignmentNode export: return export.IsExportEquals ? null
                        : ExportStatement(F.NewIdentifier("default"u8), (await VisitAsync(export.Expression))!, export, true);
                    case FunctionDeclarationNode function: return await FunctionAsync(function);
                    case ClassDeclarationNode type: return await ClassAsync(type);
                }
            }
            if (saved.mode != Mode.Normal)
            {
                switch (node)
                {
                    case VariableStatementNode variable: return await VariableAsync(variable);
                    case BlockNode or CaseBlockNode or TryStatementNode:
                        mode = Mode.Nested;
                        return await VisitEachChildAsync(node);
                    case CaseOrDefaultClauseNode clause:
                        var updatedClause = Context.Clone(clause);
                        updatedClause.Expression = await VisitAsync(clause.Expression);
                        updatedClause.Statements = await ListInAsync(clause.Statements, Mode.Nested);
                        return updatedClause;
                    case CatchClauseNode clause:
                        var updatedCatch = Context.Clone(clause);
                        updatedCatch.Block = (BlockNode?)await InAsync(clause.Block, Mode.Nested);
                        return updatedCatch;
                    case IfStatementNode statement:
                        var updatedIf = Context.Clone(statement);
                        updatedIf.Expression = await VisitAsync(statement.Expression);
                        updatedIf.ThenStatement = await EmbeddedAsync(statement.ThenStatement, false);
                        updatedIf.ElseStatement = await EmbeddedAsync(statement.ElseStatement, false);
                        return updatedIf;
                    case SwitchStatementNode statement:
                        var updatedSwitch = Context.Clone(statement);
                        updatedSwitch.Expression = await VisitAsync(statement.Expression);
                        updatedSwitch.CaseBlock = (CaseBlockNode?)await InAsync(statement.CaseBlock, Mode.Nested);
                        return updatedSwitch;
                    case DoStatementNode or WhileStatementNode or LabeledStatementNode or WithStatementNode:
                        return await NestedStatementAsync(node);
                }
            }
            switch (node)
            {
                case IdentifierNode name: return TransformSyntax.IsIdentifierReference(name, parent) ? await IdentifierAsync(name) : name;
                case ForStatementNode statement: return await ForAsync(statement, saved.mode != Mode.Normal);
                case ForInOrOfStatementNode statement: return await ForInOrOfAsync(statement, saved.mode != Mode.Normal);
                case ExpressionStatementNode statement:
                    var updatedStatement = Context.Clone(statement);
                    updatedStatement.Expression = await InAsync(statement.Expression, discard: true);
                    return updatedStatement;
                case VoidExpressionNode expression:
                    var updatedVoid = Context.Clone(expression);
                    updatedVoid.Expression = await InAsync(expression.Expression, discard: true);
                    return updatedVoid;
                case ParenthesizedExpressionNode expression:
                    var updatedParen = Context.Clone(expression);
                    updatedParen.Expression = await InAsync(expression.Expression, discard: saved.discarded);
                    return updatedParen;
                case PartiallyEmittedExpressionNode expression:
                    var updatedPartial = Context.Clone(expression);
                    updatedPartial.Expression = await InAsync(expression.Expression, discard: saved.discarded);
                    return updatedPartial;
                case BinaryExpressionNode binary: return await BinaryAsync(binary, saved.discarded);
                case PrefixUnaryExpressionNode prefix: return await PrefixAsync(prefix);
                case PostfixUnaryExpressionNode postfix: return await PostfixAsync(postfix, saved.discarded);
                case CallExpressionNode call: return await CallAsync(call);
                case TaggedTemplateExpressionNode tagged: return await TaggedAsync(tagged);
                case ShorthandPropertyAssignmentNode shorthand: return await ShorthandAsync(shorthand);
                default: return await VisitEachChildAsync(node);
            }
        }
        finally { (current, parent, mode, discarded) = saved; }
    }

    private async ValueTask<SyntaxNode?> InAsync(SyntaxNode? node, Mode requested = Mode.Normal, bool discard = false)
    {
        var saved = (mode, discarded); mode = requested; discarded = discard;
        try { return await VisitAsync(node); }
        finally { (mode, discarded) = saved; }
    }
    private async ValueTask<NodeList?> ListInAsync(NodeList? list, Mode requested)
    {
        var saved = mode; mode = requested;
        try { return await VisitListAsync(list); }
        finally { mode = saved; }
    }
    private async ValueTask<SyntaxNode?> EmbeddedAsync(SyntaxNode? node, bool iteration)
    {
        var saved = mode; mode = Mode.Nested;
        try { return iteration ? await VisitIterationBodyAsync(node) : await VisitEmbeddedStatementAsync(node); }
        finally { mode = saved; }
    }

    private async ValueTask<SyntaxNode> SourceAsync(SourceFileNode file)
    {
        if (file.IsDeclarationFile) return file;
        bool effectiveModule = file.ExternalModuleIndicator is not null || options.IsolatedModules == true || options.VerbatimModuleSyntax == true;
        if (!effectiveModule && !file.DescendantsAndSelf().Any(node => node is CallExpressionNode { Expression.Kind: K.ImportKeyword })) return file;
        source = file;
        try
        {
            info = await ExternalModuleInfo.CollectAsync(file, Context, checker, Cancellation);
            Context.StartVariableEnvironment();
            var input = file.Statements ?? new([]);
            List<SyntaxNode> statements = [];
            int index = 0;
            while (index < input.Count && input[index] is ExpressionStatementNode { Expression: StringLiteralNode }) statements.Add(input[index++]);
            int custom = index;
            while (index < input.Count && (Context.GetFlags(input[index]) & EmitFlags.CustomPrologue) != 0) index++;
            statements.AddRange(await ListInAsync(new(input.Skip(custom).Take(index - custom).ToArray()), Mode.TopLevel) ?? new([]));
            bool commonJsInput = file.FileName.EndsWith(".js"u8) || file.FileName.EndsWith(".jsx"u8) || file.FileName.EndsWith(".mjs"u8) || file.FileName.EndsWith(".cjs"u8);
            if (!(commonJsInput && await checker.IsCommonJsModuleForEmitAsync((SourceFileNode)Context.MostOriginal(file), Cancellation)
                && file.ExternalModuleIndicator is null or SourceFileNode) && info.ExportEquals is null && file.ExternalModuleIndicator is not null)
            {
                var marker = F.NewExpressionStatement(Call(Property(F.NewIdentifier("Object"u8), "defineProperty"u8),
                    [F.NewIdentifier("exports"u8), String("__esModule"u8), Object([AssignmentProperty("value"u8, F.NewKeywordExpression(K.TrueKeyword))])]));
                Context.SetFlags(marker, EmitFlags.CustomPrologue);
                statements.Add(marker);
            }
            // Match the reference's bounded export-initialization chains.
            for (int start = 0; start < info.Names.Count; start += 50)
            {
                SyntaxNode expression = Context.VoidZero();
                foreach (var name in info.Names.Skip(start).Take(50))
                {
                    var key = Context.Clone(name);
                    if (name is not StringLiteralNode) Context.SetFlags(key, EmitFlags.NoSourceMap | EmitFlags.NoComments);
                    expression = Assign(ExportAccess(key), expression);
                }
                var statement = F.NewExpressionStatement(expression);
                Context.AddFlags(statement, EmitFlags.CustomPrologue);
                statements.Add(statement);
            }
            int functionStart = statements.Count;
            foreach (var function in info.Functions) await AppendClassOrFunctionAsync(statements, function);
            for (int i = functionStart; i < statements.Count; i++) Context.AddFlags(statements[i], EmitFlags.CustomPrologue);
            statements.AddRange(await ListInAsync(new(input.Skip(index).ToArray()), Mode.TopLevel) ?? new([]));
            if (info.ExportEquals is { } export)
            {
                var savedCurrent = current; current = export;
                SyntaxNode? expression;
                try { expression = await VisitAsync(export.Expression); }
                finally { current = savedCurrent; }
                if (expression is not null)
                {
                    var statement = F.NewExpressionStatement(Assign(Property(F.NewIdentifier("module"u8), "exports"u8), expression));
                    Context.AssignCommentAndSourceMapRanges(statement, export);
                    Context.AddFlags(statement, EmitFlags.NoComments);
                    statements.Add(statement);
                }
            }
            var merged = Context.MergeEnvironment(new(statements.ToArray(), input.Pos, input.End), Context.EndVariableEnvironment());
            var result = Context.Clone(file); result.Statements = merged;
            foreach (var helper in Context.ReadHelpers()) Context.AddHelper(result, helper);
            var helpers = ModuleUtilities.HelpersImport(Context, result, options, moduleFormat(file));
            if (helpers is not null)
            {
                statements = [.. result.Statements ?? new([])]; index = 0;
                while (index < statements.Count && (statements[index] is ExpressionStatementNode { Expression: StringLiteralNode }
                    || (Context.GetFlags(statements[index]) & EmitFlags.CustomPrologue) != 0)) index++;
                statements.Insert(index, (await InAsync(helpers, Mode.TopLevel))!);
                result = Context.Clone(result); result.Statements = new(statements.ToArray(), input.Pos, input.End);
            }
            return result;
        }
        finally { source = null; info = null!; }
    }

    private SyntaxNode Many(List<SyntaxNode> statements) => statements.Count == 1 ? statements[0] : F.NewSyntaxList(statements.ToArray());
    private static bool Exported(SyntaxNode node) => SemanticSyntax.HasModifier(node, K.ExportKeyword);
    private static NodeList? RemoveExport(NodeList? modifiers) => modifiers is null ? null
        : new(modifiers.Where(node => node.Kind is not (K.ExportKeyword or K.DefaultKeyword)).ToArray(), modifiers.Pos, modifiers.End);
    private StringLiteralNode String(Utf8String text) => F.NewStringLiteral(text, TokenFlags.None);
    private CallExpressionNode Call(SyntaxNode target, SyntaxNode[] arguments) => F.NewCallExpression(target, null, null, new(arguments), NodeFlags.None);
    private SyntaxNode Assign(SyntaxNode left, SyntaxNode right) => Context.Binary(left, K.EqualsToken, right);
    private PropertyAccessExpressionNode Property(SyntaxNode target, Utf8String name) => F.NewPropertyAccessExpression(target, null, F.NewIdentifier(name), NodeFlags.None);
    private ObjectLiteralExpressionNode Object(SyntaxNode[] properties) => F.NewObjectLiteralExpression(new(properties), false);
    private PropertyAssignmentNode AssignmentProperty(Utf8String name, SyntaxNode value) => F.NewPropertyAssignment(null, F.NewIdentifier(name), null, null, value);
    private SyntaxNode ExportAccess(SyntaxNode name) => name is StringLiteralNode
        ? F.NewElementAccessExpression(F.NewIdentifier("exports"u8), null, Context.StringLiteralFromNode(name), NodeFlags.None)
        : F.NewPropertyAccessExpression(F.NewIdentifier("exports"u8), null, name, NodeFlags.None);
    private T Located<T>(T node, SyntaxNode original, bool link = true) where T : SyntaxNode
    {
        if (link) Context.SetOriginal(node, original);
        Context.AssignCommentAndSourceMapRanges(node, original);
        return node;
    }
}
