using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using F = TypeScript.Compiler.Binding.FlowFlags;

namespace TypeScript.Compiler.Binding;

public sealed partial class Binder
{
    private FlowNode? preSwitchFlow;

    private static FlowNode Label(bool loop = false) => new(loop ? F.LoopLabel : F.BranchLabel);

    private static void Reference(FlowNode flow) => flow.Flags |= (flow.Flags & F.Referenced) == 0 ? F.Referenced : F.Shared;

    private static void Add(FlowNode? label, FlowNode antecedent)
    {
        if (label is null || (antecedent.Flags & F.Unreachable) != 0 || label.AntecedentList.Contains(antecedent))
            return;
        label.AntecedentList.Add(antecedent);
        Reference(antecedent);
    }

    private FlowNode Finish(FlowNode label) =>
        label.AntecedentList.Count switch { 0 => unreachable, 1 => label.AntecedentList[0], _ => label };

    private FlowNode Mutation(F flags, SyntaxNode node)
    {
        Reference(currentFlow);
        hasFlowEffects = true;
        var flow = new FlowNode(flags, node, currentFlow);
        if (flags != F.Call)
            Add(exceptionTarget, flow);
        return flow;
    }

    private FlowNode ConditionFlow(F flags, SyntaxNode? node)
    {
        if ((currentFlow.Flags & F.Unreachable) != 0)
            return currentFlow;
        if (node is null)
            return (flags & F.TrueCondition) != 0 ? currentFlow : unreachable;
        if ((node.Kind == K.TrueKeyword && flags == F.FalseCondition || node.Kind == K.FalseKeyword && flags == F.TrueCondition)
            && !Optional(node.Parent) && node.Parent is not BinaryExpressionNode { OperatorToken.Kind: K.QuestionQuestionToken })
            return unreachable;
        if (!Narrowing(node))
            return currentFlow;
        Reference(currentFlow);
        return new(flags, node, currentFlow);
    }

    private async ValueTask VisitContainer(SyntaxNode node)
    {
        var savedContainer = container;
        var savedBlock = blockContainer;
        var savedThis = thisContainer;
        bool isContainer = IsContainer(node), isBlock = BlockScope(node);
        if (isContainer)
            container = blockContainer = node;
        else if (isBlock)
            blockContainer = node;
        if (isBlock
            || isContainer
                && !ClassLike(node)
                && node.Kind is not (K.EnumDeclaration or K.TypeLiteral or K.ObjectLiteralExpression or K.InterfaceDeclaration
                    or K.JsxAttributes))
            containers.Add(node);
        bool propagatesThis = node.Kind is K.ArrowFunction or K.MethodSignature or K.CallSignature or K.FunctionType
            or K.ConstructSignature or K.ConstructorType;
        bool controlFlow = FunctionLike(node) || node.Kind is K.SourceFile or K.ModuleBlock or K.ClassStaticBlockDeclaration
            || node is PropertyDeclarationNode { Initializer: not null };
        if (controlFlow && !propagatesThis && node.Kind is not (K.SourceFile or K.ModuleBlock))
            thisContainer = node;
        if (controlFlow)
        {
            var savedFlow = currentFlow;
            var savedBreak = breakTarget;
            var savedContinue = continueTarget;
            var savedReturn = returnTarget;
            var savedException = exceptionTarget;
            var savedLabels = labels;
            bool savedReturnFlag = explicitReturn, savedSeenThis = seenThis;
            bool immediate = node.Kind == K.ClassStaticBlockDeclaration || IsImmediate(node);
            if (!immediate)
                currentFlow = new(F.Start, node.Kind is K.FunctionExpression or K.ArrowFunction
                || node.Kind is K.MethodDeclaration or K.GetAccessor or K.SetAccessor
                    && node.Parent?.Kind is K.ObjectLiteralExpression or K.ClassExpression ? node : null);
            returnTarget = immediate || node.Kind == K.Constructor ? Label() : null;
            exceptionTarget = breakTarget = continueTarget = null;
            labels = [];
            explicitReturn = seenThis = false;
            await Children(node).ConfigureAwait(false);
            var data = Data(node);
            data.Flags &= ~(NodeFlags.ReachabilityAndEmitFlags | NodeFlags.ContainsThis);
            if ((currentFlow.Flags & F.Unreachable) == 0 && FunctionLike(node) && Body(node) is { } body && body.End > body.Pos)
            {
                data.Flags |= NodeFlags.HasImplicitReturn;
                if (explicitReturn)
                    data.Flags |= NodeFlags.HasExplicitReturn;
                data.EndFlow = currentFlow;
            }
            if (seenThis)
                data.Flags |= NodeFlags.ContainsThis;
            if (node == file)
                data.Flags |= emitFlags;
            if (returnTarget is not null)
            {
                Add(returnTarget, currentFlow);
                currentFlow = Finish(returnTarget);
                if (node.Kind is K.Constructor or K.ClassStaticBlockDeclaration)
                    data.ReturnFlow = currentFlow;
            }
            if (!immediate)
                currentFlow = savedFlow;
            breakTarget = savedBreak;
            continueTarget = savedContinue;
            returnTarget = savedReturn;
            exceptionTarget = savedException;
            labels = savedLabels;
            explicitReturn = savedReturnFlag;
            seenThis = savedSeenThis || propagatesThis && seenThis;
        }
        else
        {
            bool savedSeenThis = seenThis;
            if (node is InterfaceDeclarationNode)
                seenThis = false;
            await Children(node).ConfigureAwait(false);
            if (node is InterfaceDeclarationNode)
            {
                if (seenThis)
                    Data(node).Flags |= NodeFlags.ContainsThis;
                seenThis = savedSeenThis;
            }
        }
        if (node == file && file.ScriptKind is ScriptKind.JS or ScriptKind.JSX)
        {
            foreach (var statement in (IEnumerable<SyntaxNode>?)file.Statements ?? [])
                if (statement.Kind == K.JSTypeAliasDeclaration)
                    BlockMember(statement, SymbolFlags.TypeAlias, SymbolFlags.TypeAliasExcludes);
            if (result.CommonJSModuleIndicator is not null)
            {
                CommonJSVariable("module");
                CommonJSVariable("exports");
            }
        }
        if (node == file && result.IsModule || AmbientModule(node))
            FinishModule(SymbolOf(node));
        container = savedContainer;
        blockContainer = savedBlock;
        thisContainer = savedThis;
    }

    private static bool IsImmediate(SyntaxNode node)
    {
        if (node.Kind is not (K.FunctionExpression or K.ArrowFunction) || Has(node, K.AsyncKeyword)
            || node is FunctionExpressionNode { AsteriskToken: not null })
            return false;
        var target = node;
        while (target.Parent is ParenthesizedExpressionNode parent)
            target = parent;
        return target.Parent is CallExpressionNode call && call.Expression == target;
    }

    private async ValueTask Each(NodeList? list, bool functionsFirst = false)
    {
        if (list is null)
            return;
        if (functionsFirst)
            foreach (var node in list)
                if (node.Kind == K.FunctionDeclaration)
                    await Visit(node).ConfigureAwait(false);
        foreach (var node in list)
            if (!functionsFirst || node.Kind != K.FunctionDeclaration)
                await Visit(node).ConfigureAwait(false);
    }

    private async ValueTask EachChild(SyntaxNode node)
    {
        for (int i = 0; i < node.ChildCount; i++)
            await Visit(node.GetChild(i)).ConfigureAwait(false);
    }

    private async ValueTask Children(SyntaxNode node)
    {
        bool savedPattern = assignmentPattern;
        if (currentFlow == unreachable)
        {
            Data(node).Flow = null;
            bool executable = node.Kind >= K.FirstStatement && node.Kind <= K.LastStatement;
            if (node is VariableStatementNode statement)
                executable = statement.DeclarationList is { } list && ((list.Flags & NodeFlags.BlockScoped) != 0
                    || list.Declarations?.Any(d => d is IInitializedNode { Initializer: not null }) == true);
            if (executable || node.Kind is K.ClassDeclaration or K.EnumDeclaration or K.ModuleDeclaration)
                Data(node).Flags |= NodeFlags.Unreachable;
            assignmentPattern = false;
            await EachChild(node).ConfigureAwait(false);
            assignmentPattern = savedPattern;
            return;
        }
        if (node.Kind >= K.FirstStatement && node.Kind <= K.LastStatement)
            Data(node).Flow = currentFlow;
        if (node is BinaryExpressionNode { OperatorToken.Kind: K.EqualsToken } destructuring
            && destructuring.Left is ObjectLiteralExpressionNode or ArrayLiteralExpressionNode)
        {
            if (savedPattern)
            {
                assignmentPattern = false;
                await Visit(destructuring.OperatorToken).ConfigureAwait(false);
                await Visit(destructuring.Right).ConfigureAwait(false);
                assignmentPattern = true;
                await Visit(destructuring.Left).ConfigureAwait(false);
                await Visit(destructuring.Type).ConfigureAwait(false);
            }
            else
            {
                assignmentPattern = true;
                await Visit(destructuring.Left).ConfigureAwait(false);
                await Visit(destructuring.Type).ConfigureAwait(false);
                assignmentPattern = false;
                await Visit(destructuring.OperatorToken).ConfigureAwait(false);
                await Visit(destructuring.Right).ConfigureAwait(false);
            }
            AssignmentFlow(destructuring.Left);
            assignmentPattern = savedPattern;
            return;
        }
        if (node.Kind is not (K.ObjectLiteralExpression or K.ArrayLiteralExpression or K.PropertyAssignment or K.SpreadElement))
            assignmentPattern = false;
        switch (node)
        {
            case SourceFileNode:
                await Each(file.Statements, true).ConfigureAwait(false);
                await Visit(file.EndOfFileToken).ConfigureAwait(false);
                break;
            case BlockNode or ModuleBlockNode:
                await Each(Statements(node), true).ConfigureAwait(false);
                break;
            case IfStatementNode n:
                await If(n).ConfigureAwait(false);
                break;
            case WhileStatementNode n:
                await While(n).ConfigureAwait(false);
                break;
            case DoStatementNode n:
                await Do(n).ConfigureAwait(false);
                break;
            case ForStatementNode n:
                await For(n).ConfigureAwait(false);
                break;
            case ForInOrOfStatementNode n:
                await ForEach(node, n.Initializer, n.Expression, n.Statement, n.AwaitModifier).ConfigureAwait(false);
                break;
            case ReturnStatementNode n:
                await Visit(n.Expression).ConfigureAwait(false);
                Add(returnTarget, currentFlow);
                currentFlow = unreachable;
                explicitReturn = hasFlowEffects = true;
                break;
            case ThrowStatementNode n:
                await Visit(n.Expression).ConfigureAwait(false);
                currentFlow = unreachable;
                hasFlowEffects = true;
                break;
            case BreakStatementNode n:
                await BreakContinue(n.Label, false).ConfigureAwait(false);
                break;
            case ContinueStatementNode n:
                await BreakContinue(n.Label, true).ConfigureAwait(false);
                break;
            case LabeledStatementNode n:
                await Labeled(n).ConfigureAwait(false);
                break;
            case TryStatementNode n:
                await Try(n).ConfigureAwait(false);
                break;
            case SwitchStatementNode n:
                await Switch(n).ConfigureAwait(false);
                break;
            case CaseBlockNode n:
                await Cases(n).ConfigureAwait(false);
                break;
            case CaseOrDefaultClauseNode n:
                var savedFlow = currentFlow;
                currentFlow = preSwitchFlow!;
                await Visit(n.Expression).ConfigureAwait(false);
                currentFlow = savedFlow;
                await Each(n.Statements).ConfigureAwait(false);
                break;
            case ExpressionStatementNode n:
                await Visit(n.Expression).ConfigureAwait(false);
                AssertionCall(n.Expression);
                break;
            case PrefixUnaryExpressionNode { Operator: K.ExclamationToken } n:
                (trueTarget, falseTarget) = (falseTarget, trueTarget);
                await EachChild(n).ConfigureAwait(false);
                (trueTarget, falseTarget) = (falseTarget, trueTarget);
                break;
            case PrefixUnaryExpressionNode n:
                await EachChild(n).ConfigureAwait(false);
                if (n.Operator is K.PlusPlusToken or K.MinusMinusToken)
                    AssignmentFlow(n.Operand);
                break;
            case PostfixUnaryExpressionNode n:
                await EachChild(n).ConfigureAwait(false);
                AssignmentFlow(n.Operand);
                break;
            case BinaryExpressionNode n:
                await Binary(n).ConfigureAwait(false);
                break;
            case ConditionalExpressionNode n:
                await Conditional(n).ConfigureAwait(false);
                break;
            case DeleteExpressionNode n:
                await EachChild(n).ConfigureAwait(false);
                if (n.Expression is PropertyAccessExpressionNode)
                    AssignmentFlow(n.Expression);
                break;
            case VariableDeclarationNode n:
                await EachChild(n).ConfigureAwait(false);
                if (n.Initializer is not null || n.Parent?.Parent?.Kind is K.ForInStatement or K.ForOfStatement)
                    Initialized(n);
                break;
            case BindingElementNode n:
                Data(n).Flow = currentFlow;
                await Visit(n.DotDotDotToken).ConfigureAwait(false);
                await Visit(n.PropertyName).ConfigureAwait(false);
                await Initializer(n.Initializer).ConfigureAwait(false);
                await Visit(n.Name).ConfigureAwait(false);
                break;
            case ParameterDeclarationNode n:
                await Each(n.Modifiers).ConfigureAwait(false);
                await Visit(n.DotDotDotToken).ConfigureAwait(false);
                await Visit(n.QuestionToken).ConfigureAwait(false);
                await Visit(n.Type).ConfigureAwait(false);
                await Initializer(n.Initializer).ConfigureAwait(false);
                await Visit(n.Name).ConfigureAwait(false);
                break;
            case PropertyAccessExpressionNode or ElementAccessExpressionNode or NonNullExpressionNode:
                if (node is not NonNullExpressionNode && Narrowable(node))
                    Data(node).Flow = currentFlow;
                if (Optional(node))
                    await OptionalFlow(node).ConfigureAwait(false);
                else
                    await EachChild(node).ConfigureAwait(false);
                break;
            case CallExpressionNode n:
                await Call(n).ConfigureAwait(false);
                break;
            default:
                await EachChild(node).ConfigureAwait(false);
                break;
        }
        assignmentPattern = savedPattern;
    }

    private async ValueTask Initializer(SyntaxNode? node)
    {
        if (node is null)
            return;
        var entry = currentFlow;
        await Visit(node).ConfigureAwait(false);
        if (entry != unreachable && entry != currentFlow)
        {
            var exit = Label();
            Add(exit, entry);
            Add(exit, currentFlow);
            currentFlow = Finish(exit);
        }
    }

    private void Initialized(SyntaxNode node)
    {
        var stack = new Stack<SyntaxNode>();
        stack.Push(node);
        while (stack.TryPop(out var declaration))
        {
            NodeList? elements = (Name(declaration) as BindingPatternNode)?.Elements;
            if (elements is not null)
            {
                for (int i = elements.Count - 1; i >= 0; i--)
                    stack.Push(elements[i]);
            }
            else
                currentFlow = Mutation(F.Assignment, declaration);
        }
    }

    private void AssignmentFlow(SyntaxNode? node)
    {
        if (node is null)
            return;
        var stack = new Stack<SyntaxNode>();
        stack.Push(node);
        while (stack.TryPop(out var target))
        {
            if (!ReferenceEquals(target, node) && target is BinaryExpressionNode { OperatorToken.Kind: K.EqualsToken, Left: { } left })
                target = left;
            if (target is ArrayLiteralExpressionNode array)
            {
                foreach (var element in ((IEnumerable<SyntaxNode>?)array.Elements ?? []).Reverse())
                    stack.Push(element is SpreadElementNode spread ? spread.Expression! : element);
            }
            else if (target is ObjectLiteralExpressionNode obj)
            {
                foreach (var property in ((IEnumerable<SyntaxNode>?)obj.Properties ?? []).Reverse())
                    switch (property)
                    {
                        case PropertyAssignmentNode { Initializer: { } value }:
                            stack.Push(value);
                            break;
                        case ShorthandPropertyAssignmentNode { Name: { } name }:
                            stack.Push(name);
                            break;
                        case SpreadAssignmentNode { Expression: { } value }:
                            stack.Push(value);
                            break;
                    }
            }
            else if (Narrowable(target))
                currentFlow = Mutation(F.Assignment, target);
        }
    }

    private async ValueTask Condition(SyntaxNode? node, FlowNode yes, FlowNode no)
    {
        var savedTrue = trueTarget;
        var savedFalse = falseTarget;
        trueTarget = yes;
        falseTarget = no;
        await Visit(node).ConfigureAwait(false);
        trueTarget = savedTrue;
        falseTarget = savedFalse;
        if (!Logical(SkipParentheses(node)) && !(Optional(node) && OutermostOptional(node!)))
        {
            Add(yes, ConditionFlow(F.TrueCondition, node));
            Add(no, ConditionFlow(F.FalseCondition, node));
        }
    }

    private async ValueTask Iteration(SyntaxNode? body, FlowNode exit, FlowNode next)
    {
        var savedBreak = breakTarget;
        var savedContinue = continueTarget;
        breakTarget = exit;
        continueTarget = next;
        await Visit(body).ConfigureAwait(false);
        breakTarget = savedBreak;
        continueTarget = savedContinue;
    }

    private FlowNode ContinueLabel(SyntaxNode node, FlowNode label)
    {
        for (int i = labels.Count - 1; i >= 0 && node.Parent is LabeledStatementNode; i--)
        {
            labels[i] = labels[i] with { Continue = label };
            node = node.Parent;
        }
        return label;
    }

    private async ValueTask If(IfStatementNode n)
    {
        var yes = Label();
        var no = Label();
        var exit = Label();
        await Condition(n.Expression, yes, no).ConfigureAwait(false);
        currentFlow = Finish(yes);
        await Visit(n.ThenStatement).ConfigureAwait(false);
        Add(exit, currentFlow);
        currentFlow = Finish(no);
        await Visit(n.ElseStatement).ConfigureAwait(false);
        Add(exit, currentFlow);
        currentFlow = Finish(exit);
    }

    private async ValueTask While(WhileStatementNode n)
    {
        var loop = ContinueLabel(n, Label(true));
        var body = Label();
        var exit = Label();
        Add(loop, currentFlow);
        currentFlow = loop;
        await Condition(n.Expression, body, exit).ConfigureAwait(false);
        currentFlow = Finish(body);
        await Iteration(n.Statement, exit, loop).ConfigureAwait(false);
        Add(loop, currentFlow);
        currentFlow = Finish(exit);
    }

    private async ValueTask Do(DoStatementNode n)
    {
        var loop = Label(true);
        var condition = ContinueLabel(n, Label());
        var exit = Label();
        Add(loop, currentFlow);
        currentFlow = loop;
        await Iteration(n.Statement, exit, condition).ConfigureAwait(false);
        Add(condition, currentFlow);
        currentFlow = Finish(condition);
        await Condition(n.Expression, loop, exit).ConfigureAwait(false);
        currentFlow = Finish(exit);
    }

    private async ValueTask For(ForStatementNode n)
    {
        await Visit(n.Initializer).ConfigureAwait(false);
        if (currentFlow == unreachable)
        {
            await Visit(n.Condition).ConfigureAwait(false);
            await Visit(n.Statement).ConfigureAwait(false);
            await Visit(n.Incrementor).ConfigureAwait(false);
            return;
        }
        var loop = ContinueLabel(n, Label(true));
        var body = Label();
        var increment = Label();
        var exit = Label();
        Add(loop, currentFlow);
        currentFlow = loop;
        await Condition(n.Condition, body, exit).ConfigureAwait(false);
        currentFlow = Finish(body);
        await Iteration(n.Statement, exit, increment).ConfigureAwait(false);
        Add(increment, currentFlow);
        currentFlow = Finish(increment);
        await Visit(n.Incrementor).ConfigureAwait(false);
        Add(loop, currentFlow);
        currentFlow = Finish(exit);
    }

    private async ValueTask ForEach(
        SyntaxNode node,
        SyntaxNode? initializer,
        SyntaxNode? expression,
        SyntaxNode? statement,
        SyntaxNode? awaitModifier)
    {
        await Visit(expression).ConfigureAwait(false);
        if (currentFlow == unreachable)
        {
            await Visit(initializer).ConfigureAwait(false);
            await Visit(statement).ConfigureAwait(false);
            return;
        }
        var loop = ContinueLabel(node, Label(true));
        var exit = Label();
        Add(loop, currentFlow);
        currentFlow = loop;
        await Visit(awaitModifier).ConfigureAwait(false);
        Add(exit, currentFlow);
        await Visit(initializer).ConfigureAwait(false);
        if (initializer is not VariableDeclarationListNode)
            AssignmentFlow(initializer);
        await Iteration(statement, exit, loop).ConfigureAwait(false);
        Add(loop, currentFlow);
        currentFlow = Finish(exit);
    }

    private async ValueTask BreakContinue(IdentifierNode? label, bool isContinue)
    {
        await Visit(label).ConfigureAwait(false);
        FlowNode? target = isContinue ? continueTarget : breakTarget;
        if (label is not null)
        {
            int index = labels.FindLastIndex(l => l.Name == label.Text);
            if (index < 0)
                return;
            labels[index] = labels[index] with { Referenced = true };
            target = isContinue ? labels[index].Continue : labels[index].Break;
        }
        if (target is not null)
        {
            Add(target, currentFlow);
            currentFlow = unreachable;
            hasFlowEffects = true;
        }
    }

    private async ValueTask Labeled(LabeledStatementNode n)
    {
        var exit = Label();
        labels.Add(new(n.Label!.Text, exit, null));
        await Visit(n.Label).ConfigureAwait(false);
        await Visit(n.Statement).ConfigureAwait(false);
        if (!labels[^1].Referenced)
            Data(n.Label).Flags |= NodeFlags.Unreachable;
        labels.RemoveAt(labels.Count - 1);
        Add(exit, currentFlow);
        currentFlow = Finish(exit);
    }

    private void AssertionCall(SyntaxNode? node)
    {
        if (node is CallExpressionNode call)
        {
            SyntaxNode? target = call.Expression;
            while (target is PropertyAccessExpressionNode or ParenthesizedExpressionNode)
                target = Expression(target);
            if (call.Expression?.Kind != K.SuperKeyword
                && (target is IdentifierNode || target?.Kind is K.ThisKeyword or K.SuperKeyword or K.MetaProperty))
                currentFlow = Mutation(F.Call, node);
        }
    }
}
