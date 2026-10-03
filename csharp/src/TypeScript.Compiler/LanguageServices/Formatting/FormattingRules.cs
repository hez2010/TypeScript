using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

[Flags]
internal enum FormattingAction
{
    None = 0, StopProcessingSpaceActions = 1, StopProcessingTokenActions = 2, InsertSpace = 4,
    InsertNewLine = 8, DeleteSpace = 16, DeleteToken = 32, InsertTrailingSemicolon = 64,
    Stop = StopProcessingSpaceActions | StopProcessingTokenActions,
    ModifySpace = InsertSpace | InsertNewLine | DeleteSpace, ModifyToken = DeleteToken | InsertTrailingSemicolon,
}

internal static partial class FormattingRules
{
    internal sealed record Rule(string Name, Func<FormattingContext, ValueTask<bool>>[] Predicates, FormattingAction Action, bool CanDeleteNewLines);
    private readonly record struct TokenSet(K[] Tokens, bool Specific = true)
    {
        public static implicit operator TokenSet(K kind) => new([kind]);
    }
    private sealed record RuleSpec(TokenSet Left, TokenSet Right, Rule Rule);
    private static readonly Lazy<Rule[][]> Rules = new(Build);
    private static int Bucket(K left, K right) => (int)left * ((int)K.LastToken + 1) + (int)right;

    internal static async ValueTask<List<Rule>> GetAsync(FormattingContext context)
    {
        var rules = Rules.Value[Bucket(context.Current.Kind, context.Next.Kind)];
        var result = new List<Rule>();
        FormattingAction mask = 0;
        foreach (var rule in rules)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            FormattingAction excluded = 0;
            if ((mask & (FormattingAction.StopProcessingSpaceActions | FormattingAction.ModifySpace)) != 0) excluded |= FormattingAction.ModifySpace;
            if ((mask & (FormattingAction.StopProcessingTokenActions | FormattingAction.ModifyToken)) != 0) excluded |= FormattingAction.ModifyToken;
            if ((rule.Action & ~excluded) == 0) continue;
            bool accept = true;
            foreach (var predicate in rule.Predicates)
                if (!await predicate(context).ConfigureAwait(false)) { accept = false; break; }
            if (!accept) continue;
            result.Add(rule); mask |= rule.Action;
        }
        return result;
    }

    private static Rule[][] Build()
    {
        var specifications = CreateRules();
        var buckets = new List<(Rule Rule, int Priority)>[((int)K.LastToken + 1) * ((int)K.LastToken + 1)];
        foreach (var specification in specifications)
        {
            bool specific = specification.Left.Specific && specification.Right.Specific;
            var rule = specification.Rule;
            int priority = (rule.Action & FormattingAction.Stop) != 0 ? 0 : rule.Predicates.Length != 0 ? 2 : 4;
            if (!specific) priority++;
            foreach (var left in specification.Left.Tokens) foreach (var right in specification.Right.Tokens)
                (buckets[Bucket(left, right)] ??= []).Add((rule, priority));
        }
        return buckets.Select(bucket => bucket?.OrderBy(entry => entry.Priority).Select(entry => entry.Rule).ToArray() ?? []).ToArray();
    }

    private static RuleSpec R(string name, TokenSet left, TokenSet right, Func<FormattingContext, ValueTask<bool>>[] predicates,
        FormattingAction action, bool canDeleteNewLines = false) => new(left, right, new(name, predicates, action, canDeleteNewLines));
    private static TokenSet Tokens(params K[] kinds) => new(kinds);
    private static TokenSet Range(K start, K end) => new(Enumerable.Range((int)start, (int)(end - start + 1)).Select(i => (K)i).ToArray());
    private static Func<FormattingContext, ValueTask<bool>> P(Func<FormattingContext, bool> predicate) => c => new(predicate(c));
    private static Func<FormattingContext, ValueTask<bool>> P(Func<FormattingContext, ValueTask<bool>> predicate) => predicate;
}
