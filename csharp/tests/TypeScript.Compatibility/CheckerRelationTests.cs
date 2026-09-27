using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Text;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerRelationTests
{
    internal static async Task<int> DiagnosticSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Relation diagnostic assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        var files = new Dictionary<string, byte[]>
        {
            ["/project/globals.d.ts"] = Wtf8.Encode(
                "interface Object{toString():string}interface Function{}interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}interface String{length:number}interface Number{}interface Boolean{}interface RegExp{}"),
            ["/project/main.ts"] = Wtf8.Encode(
                "interface Inv<in out T>{f:(x:T)=>T}declare let a:Inv<string>;let b:Inv<unknown>=a;function missing<T>(x:T){let y:number=x;}interface Left{p:string}interface Right{p:number}interface Both extends Left,Right{p:boolean}declare let boxed:String;let primitive:string=boxed;namespace A{export enum E{X=1,Y=2}}namespace B{export enum E{X=1,Y=3}}declare let sourceEnum:B.E;let enumeration:A.E=sourceEnum;declare let optional:number|undefined;optional='bad';"),
            ["/project/excess.ts"] = Wtf8.Encode(
                "declare function accept<T>(x:{[key:string]:T}|{[key:number]:T}):void;accept({toString:123});type Basic={id:number};type Extra=Basic&{description:string};const data:{items:Basic[]}&{items:Extra[]}={items:[{id:1,description:'ok'}]};")
        };
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/project",
            new("/project/tsconfig.json", options, files.Keys.ToArray(), [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.ts")!.Syntax;
        var snapshot = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        await checker.CheckProgramAsync();
        var diagnostics = checker.DetailedDiagnosticsForProgramFile(source);
        static bool Contains(Diagnostic d, int code) => d.Code == code || d.MessageChain.Any(c => Contains(c, code));
        var invariant = diagnostics.Single(d => d.Arguments.SequenceEqual(["Inv<string>", "Inv<unknown>"]));
        Check(Contains(invariant, 2326) && Contains(invariant, 2328));
        var constraint = diagnostics.Single(d => d.Arguments.SequenceEqual(["T", "number"]));
        Check(constraint.RelatedInformation is [var note] && note.Code == 2208 && note.Arguments.SequenceEqual(["number"]));
        Check(diagnostics.Count(d => d.Code == 2430) == 2);
        Check(diagnostics.Where(d => d.Code == 2430).Select(d => d.Arguments[1]).Order().SequenceEqual(["Left", "Right"]));
        Check(diagnostics.Any(d => Contains(d, 2692)));
        Check(diagnostics.Any(d => Contains(d, 4125)));
        Check(diagnostics.Any(d => d.Code == 2322 && d.Arguments.SequenceEqual(["string", "number"])));
        Check(checker.DetailedDiagnosticsForProgramFile(program.GetFile("/project/excess.ts")!.Syntax).Count == 0);
        await checker.CheckProgramAsync();
        Check(checker.DetailedDiagnosticsForProgramFile(source).SequenceEqual(diagnostics, DiagnosticEqualityComparer.Instance));
        Check(snapshot.All(n => n.Node.Parent == n.Parent && n.Node.Pos == n.Pos && n.Node.End == n.End && n.Node.Flags == n.Flags));
        return checks;
    }

    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Relation assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        const string text = "interface Box<T>{value:T;self:this}interface Other<T>{value:T}interface Empty extends Box<string>{}type N=Empty;type A={next:A};type B={next:B};";
        var program = await CompilerProgram.CreateAsync(
            new MemoryFileSystem(new Dictionary<string, byte[]> { ["/project/main.ts"] = Wtf8.Encode(text) }),
            "/project", new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var scope = new CheckerEnvironment(context, links);
        var symbols = await CheckerSymbols.CreateAsync(program, links, scope);
        var host = new Checker(context, links, scope);
        var box = (InterfaceType)await host.Declared.GetAsync(symbols.Globals["Box"]);
        var other = (InterfaceType)await host.Declared.GetAsync(symbols.Globals["Other"]);
        var p = context.NewTypeParameter();
        var q = context.NewTypeParameter();
        var constrained = context.NewTypeParameter();
        constrained.Constraint = context.StringType;
        var bp = context.CreateTypeReference(box, [p]);
        var op = context.CreateTypeReference(other, [p]);
        var bq = context.CreateTypeReference(box, [q]);
        var oq = context.CreateTypeReference(other, [q]);
        var bc = context.CreateTypeReference(box, [constrained]);
        var oc = context.CreateTypeReference(other, [constrained]);
        var key = await host.RelationKeys.CreateAsync(bp, op);
        Check(!key.Constrained && key.Key.Equals((await host.RelationKeys.CreateAsync(bq, oq)).Key));
        Check(!key.Key.Equals((await host.RelationKeys.CreateAsync(bp, oq)).Key));
        Check(!key.Key.Equals((await host.RelationKeys.CreateAsync(bp, op, IntersectionState.Source)).Key));
        Check(
            (await host.RelationKeys.CreateAsync(
                bp,
                op,
                identity: true)).Key.Equals((await host.RelationKeys.CreateAsync(op, bp, identity: true)).Key));
        var constrainedKey = await host.RelationKeys.CreateAsync(bc, oc);
        Check(constrainedKey.Constrained && !constrainedKey.Key.Equals(key.Key));
        Check((await host.RelationKeys.CreateAsync(bc, oc, ignoreConstraints: true)).Key.Equals(key.Key));

        var recursion = new TypeRecursion(async (t, token) => await host.Instantiation.Members.ModifiersTypeAsync(t, token));
        var relation = new Relation(RelationKind.Assignable);
        var state = new RelationState();
        var session = new RelationSession(context, relation, host.RelationKeys, recursion, state);
        ValueTask NoOverflow(Type _, Type __, CancellationToken cancellation) =>
            throw new InvalidOperationException("Unexpected comparison overflow");
        var result = await session.RecursiveAsync(bp, op, 0, RecursionFlags.Both, false,
            () => session.RecursiveAsync(
                bc,
                oc,
                0,
                RecursionFlags.Both,
                false,
                () => throw new InvalidOperationException("Broad key was not recognized"),
                NoOverflow),
            NoOverflow);
        Check(result == Ternary.Maybe && relation.Count == 1 && session.PendingCount == 0);
        Check(relation.Get(key.Key) == RelationComparisonResult.Succeeded && relation.Get(constrainedKey.Key) == 0);
        Check(session.Remaining == 1_999_999 && !session.Overflow);
        await session.CompleteAsync(bp, op, NoOverflow);
        Check(
            await session.RecursiveAsync(
                bp,
                op,
                0,
                RecursionFlags.Both,
                false,
                () => throw new InvalidOperationException("Cache ignored"),
                NoOverflow) == Ternary.True);

        var cancelledRelation = new Relation(RelationKind.Identity);
        var cancelledState = new RelationState { Reliability = RelationComparisonResult.ReportsUnreliable };
        var cancelled = new RelationSession(context, cancelledRelation, host.RelationKeys, recursion, cancelledState);
        try
        {
            await cancelled.RecursiveAsync(context.EmptyObjectType, context.EmptyTypeLiteralType, 0, RecursionFlags.Both, false, async () =>
            {
                await cancelled.RecursiveAsync(bp, op, 0, RecursionFlags.Both, false, () =>
                {
                    cancelledState.Reliability |= RelationComparisonResult.ReportsUnmeasurable;
                    return ValueTask.FromResult(Ternary.True);
                }, NoOverflow);
                throw new OperationCanceledException();
            }, NoOverflow);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(
            cancelledRelation.Count == 0
                && cancelled.PendingCount == 0
                && cancelled.SourceStack.Count == 0
                && cancelled.TargetStack.Count == 0);
        Check(cancelledState.Reliability == RelationComparisonResult.ReportsUnreliable && cancelled.Remaining == 2_000_000);
        Check(
            await cancelled.RecursiveAsync(
                bp,
                op,
                0,
                RecursionFlags.Both,
                false,
                () => ValueTask.FromResult(Ternary.Unknown),
                NoOverflow) == Ternary.Unknown);
        Check(cancelledRelation.Count == 0 && cancelled.PendingCount == 0);

        var failed = new RelationSession(context, relation, host.RelationKeys, recursion, state);
        Check(
            await failed.RecursiveAsync(
                bc,
                oc,
                0,
                RecursionFlags.Both,
                false,
                () => ValueTask.FromResult(Ternary.False),
                NoOverflow) == Ternary.False);
        Check(relation.Get(constrainedKey.Key) == RelationComparisonResult.Failed);
        int calls = 0;
        Check(await failed.RecursiveAsync(bc, oc, 0, RecursionFlags.Both, false, () =>
        {
            calls++;
            return ValueTask.FromResult(Ternary.True);
        }, NoOverflow) == Ternary.False && calls == 0);
        Check(await failed.RecursiveAsync(bc, oc, 0, RecursionFlags.Both, true, () =>
        {
            calls++;
            return ValueTask.FromResult(Ternary.True);
        }, NoOverflow) == Ternary.True && calls == 1);
        await failed.CompleteAsync(bc, oc, NoOverflow);

        relation.Set(constrainedKey.Key, RelationComparisonResult.Failed);
        var owner = new RelationSession(context, relation, host.RelationKeys, recursion, state);
        await owner.RecursiveAsync(bc, oc, 0, RecursionFlags.Both, true, () => ValueTask.FromResult(Ternary.False), NoOverflow);
        var independent = new RelationSession(context, relation, host.RelationKeys, recursion, state);
        await independent.RecursiveAsync(bc, oc, 0, RecursionFlags.Both, true, () => ValueTask.FromResult(Ternary.True), NoOverflow);
        await independent.CompleteAsync(bc, oc, NoOverflow);
        owner.Abort();
        Check(relation.Get(constrainedKey.Key) == RelationComparisonResult.Succeeded);

        var budgetRelation = new Relation(RelationKind.Assignable);
        var budget = new RelationSession(context, budgetRelation, host.RelationKeys, recursion, state) { Remaining = 0 };
        int overflows = 0;
        Check(
            await budget.RecursiveAsync(
                bp,
                op,
                0,
                RecursionFlags.Both,
                false,
                () => throw new InvalidOperationException("Budget ignored"),
                (_, _, _) =>
                {
                    overflows++;
                    return ValueTask.CompletedTask;
                }) == Ternary.False
                && budget.Overflow);
        await budget.CompleteAsync(bp, op, (_, _, _) =>
        {
            overflows++;
            return ValueTask.CompletedTask;
        });
        Check(
            overflows == 1
                && budgetRelation.Get(key.Key) == (RelationComparisonResult.Failed | RelationComparisonResult.ComplexityOverflow));
        var reuse = new RelationSession(context, budgetRelation, host.RelationKeys, recursion, state);
        Check(
            await reuse.RecursiveAsync(
                bp,
                op,
                0,
                RecursionFlags.Both,
                true,
                () => throw new InvalidOperationException("Overflow cache ignored"),
                (_, _, _) =>
                {
                    overflows++;
                    return ValueTask.CompletedTask;
                }) == Ternary.False
                && overflows == 2);

        var depthRelation = new Relation(RelationKind.Identity);
        var depthSession = new RelationSession(context, depthRelation, host.RelationKeys, recursion, state);
        var left = Enumerable.Range(
            0,
            101).Select(i => (Type)context.NewObjectType(ObjectFlags.Anonymous, new(SymbolFlags.TypeLiteral, "L" + i))).ToArray();
        var right = Enumerable.Range(
            0,
            101).Select(i => (Type)context.NewObjectType(ObjectFlags.Anonymous, new(SymbolFlags.TypeLiteral, "R" + i))).ToArray();
        int visited = 0;
        ValueTask<Ternary> Descend(int depth) => depthSession.RecursiveAsync(left[depth], right[depth], 0, RecursionFlags.Both, false,
            () =>
            {
                visited++;
                return Descend(depth + 1);
            }, NoOverflow);
        Check(await Descend(0) == Ternary.Maybe && visited == 100 && depthRelation.Count == 100 && !depthSession.Overflow);
        Check(depthSession.PendingCount == 0 && depthSession.SourceStack.Count == 0 && depthSession.TargetStack.Count == 0);

        var expandingRelation = new Relation(RelationKind.Assignable);
        var expandingSession = new RelationSession(context, expandingRelation, host.RelationKeys, recursion, state);
        var sources = Enumerable.Range(0, 4).Select(i => context.CreateTypeReference(box, [context.GetNumberLiteralType(i)])).ToArray();
        var targets = Enumerable.Range(0, 4).Select(i => context.CreateTypeReference(other, [context.GetNumberLiteralType(i)])).ToArray();
        visited = 0;
        ValueTask<Ternary> Expand(int depth) => expandingSession.RecursiveAsync(
            sources[depth],
            targets[depth],
            0,
            RecursionFlags.Both,
            false,
            () =>
            {
                visited++;
                return Expand(depth + 1);
            }, NoOverflow);
        Check(await Expand(0) == Ternary.Maybe && visited == 2 && expandingRelation.Count == 3);
        Check(expandingSession.PendingCount == 0 && !expandingSession.Overflow);
        var reliableCache = new Relation(RelationKind.Assignable);
        var reliability = new RelationState();
        var reporting = new RelationSession(context, reliableCache, host.RelationKeys, recursion, reliability);
        await reporting.RecursiveAsync(bp, op, 0, RecursionFlags.Both, false, () =>
        {
            reliability.Reliability = RelationComparisonResult.ReportsUnmeasurable;
            return ValueTask.FromResult(Ternary.True);
        }, NoOverflow);
        await reporting.CompleteAsync(bp, op, NoOverflow);
        reliability.Reliability = 0;
        var cachedReporting = new RelationSession(context, reliableCache, host.RelationKeys, recursion, reliability);
        Check(
            await cachedReporting.RecursiveAsync(
                bp,
                op,
                0,
                RecursionFlags.Both,
                false,
                () => throw new InvalidOperationException("Reliability cache ignored"),
                NoOverflow) == Ternary.True);
        Check(reliability.Reliability == RelationComparisonResult.ReportsUnmeasurable);

        var n = (TypeReference)await host.Declared.GetAsync(symbols.Globals["N"]);
        var regular = context.CreateTypeReference((InterfaceType)n.ReferencedType, (await host.References.TypeArgumentsAsync(n)).ToArray());
        host.BeforeMemberTable = _ => throw new OperationCanceledException();
        try
        {
            await host.Normalization.SingleBaseAsync(regular);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check((regular.ObjectFlags & ObjectFlags.IdenticalBaseTypeCalculated) == 0);
        host.BeforeMemberTable = null;
        var baseType = context.CreateTypeReference(box, [context.StringType]);
        var normalized = await host.Normalization.GetAsync(regular);
        Check(normalized == baseType);
        Check(
            await host.Relations.RelatedAsync(
                await host.Declared.GetAsync(symbols.Globals["A"]),
                await host.Declared.GetAsync(symbols.Globals["B"]),
                RelationKind.Identity));

        Type deep = bp;
        for (int i = 0; i < 20_000; i++)
            deep = context.CreateTypeReference(box, [deep]);
        Check((await host.RelationKeys.CreateAsync(deep, deep)).Key.Equals((await host.RelationKeys.CreateAsync(deep, deep)).Key));
        Type chain = baseType;
        for (int i = 0; i < 20_000; i++)
        {
            var target = (InterfaceType)context.NewObjectType(
                ObjectFlags.Interface | ObjectFlags.Reference,
                new(SymbolFlags.Interface, "E" + i));
            var self = context.NewTypeParameter();
            self.IsThisType = true;
            self.Constraint = target;
            target.ThisType = self;
            target.AllTypeParameters = [self];
            target.Target = target;
            target.ResolvedTypeArguments = [];
            target.BaseTypesResolved = true;
            target.ResolvedBaseTypes = [chain];
            chain = context.CreateTypeReference(target, []);
        }
        Check(await host.Normalization.GetAsync(chain) == baseType);
        try
        {
            await host.RelationKeys.CreateAsync(context.StringType, new TypeContext().StringType);
            throw new InvalidOperationException("Foreign type accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        checks += await EnumSafety();
        Console.WriteLine(
            $"{checks} relation key/cache/normalization/cancellation assertions; exact 100-level comparison cutoff; key and base chains depth 20000");
    }

    private static async Task<int> EnumSafety()
    {
        const string source = """
            namespace A { export enum E { First = 0, Second = 1 } }
            namespace B { export enum E { First = 0, Second = 1 } }
            namespace C { export enum E { First = 0, Second = 2 } }
            namespace D { export enum E { First = 0 } }
            namespace Opaque { export declare enum E { First, Second } }
            namespace Text { export enum E { First = 'first', Second = 'second' } }
            namespace Constant { export const enum E { First = 0, Second = 1 } }
            """;
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source) }), "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var enums = program.SourceFiles[0].Syntax.DescendantsAndSelf().OfType<EnumDeclarationNode>()
            .ToDictionary(n => ((IdentifierNode)((ModuleDeclarationNode)n.Parent!.Parent!).Name!).Text,
                n => checker.Environment.Symbols.Declaration(n)!);
        var cases = new (string Source, string Target, bool Expected)[]
        {
            ("A", "A", true), ("A", "B", true), ("A", "C", false), ("A", "D", false), ("D", "A", true),
            ("A", "Opaque", true), ("Opaque", "A", true), ("Text", "Opaque", false), ("Opaque", "Text", false), ("A", "Constant", false)
        };
        foreach (var item in cases)
            if (await checker.EnumRelatedAsync(enums[item.Source], enums[item.Target], default) != item.Expected)
                throw new InvalidOperationException($"Enum relation {item.Source} -> {item.Target}");
        if (!await checker.EnumRelatedAsync(enums["A"].Exports["First"], enums["B"].Exports["First"], default))
            throw new InvalidOperationException("Enum member relation lost parent identity");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await checker.EnumRelatedAsync(enums["A"], enums["B"], cancelled.Token);
            throw new InvalidOperationException("Cached enum relation ignored cancellation");
        }
        catch (OperationCanceledException) { }
        return cases.Length + 2;
    }

    internal static async Task WriteAsync(Utf8JsonWriter writer, SyntaxNode[] nodes, CheckerSymbols symbols, Checker host,
        Func<Type?, int> typeId, Func<SyntaxNode?, int> nodeId, bool allKinds = false)
    {
        var roots = new List<(SyntaxNode Node, Type Type)>();
        foreach (var node in nodes)
            if (node is TypeAliasDeclarationNode or InterfaceDeclarationNode or ClassDeclarationNode
                && node is INamedNode { Name: IdentifierNode identifier } && identifier.Text.Length > 1 && identifier.Text[0] == 'R' && char.IsAsciiDigit(identifier.Text[1]))
                roots.Add((node, await host.Declared.GetAsync(symbols.Declaration(node)!)));
        var keys = new Dictionary<RelationKey, int>();
        writer.WriteStartArray("relations");
        foreach (var source in roots)
            foreach (var target in roots)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(nodeId(source.Node));
                writer.WriteNumberValue(nodeId(target.Node));
                writer.WriteNumberValue(typeId(source.Type));
                writer.WriteNumberValue(typeId(target.Type));
                if (allKinds)
                {
                    writer.WriteStartArray();
                    foreach (var kind in Enum.GetValues<RelationKind>())
                    {
                        writer.WriteStartArray();
                        writer.WriteBooleanValue(await host.Relations.RelatedAsync(source.Type, target.Type, kind));
                        writer.WriteNumberValue(host.Relations.Cache(kind).Count);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndArray();
                    continue;
                }
                writer.WriteBooleanValue(await host.Relations.RelatedAsync(source.Type, target.Type, RelationKind.Identity));
                var relation = host.Relations.Cache(RelationKind.Identity);
                writer.WriteNumberValue(relation.Count);
                var (key, constrained) = await host.RelationKeys.CreateAsync(source.Type, target.Type, identity: true);
                if (!keys.TryGetValue(key, out int id))
                    keys.Add(key, id = keys.Count + 1);
                writer.WriteNumberValue(id);
                writer.WriteNumberValue((uint)relation.Get(key));
                writer.WriteBooleanValue(constrained);
                writer.WriteStartArray();
                foreach (var kind in Enum.GetValues<RelationKind>())
                    writer.WriteBooleanValue(await host.Relations.SimpleAsync(source.Type, target.Type, kind));
                writer.WriteEndArray();
                writer.WriteEndArray();
            }
        writer.WriteEndArray();
        if (allKinds)
        {
            writer.WriteStartArray("variances");
            var seen = new HashSet<Symbol>();
            foreach (var node in nodes)
            {
                var symbol = symbols.Declaration(node);
                if (symbol is null || !seen.Add(symbol) || !host.Variances.Cache.TryGetValue(symbol, out var flags))
                    continue;
                writer.WriteStartArray();
                writer.WriteNumberValue(nodeId(node));
                writer.WriteStartArray();
                foreach (var flag in flags)
                    writer.WriteNumberValue((uint)flag);
                writer.WriteEndArray();
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("facts");
            foreach (var root in roots)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(nodeId(root.Node));
                writer.WriteNumberValue((uint)await host.Facts.GetAsync(root.Type, TypeFacts.All));
                writer.WriteNumberValue(typeId(await host.Facts.NonNullableAsync(root.Type)));
                writer.WriteNumberValue(typeId(await host.Facts.FilterAsync(root.Type, TypeFacts.NEUndefined)));
                writer.WriteNumberValue(typeId(await host.Facts.AdjustAsync(root.Type, TypeFacts.NENull)));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        var keyRoots = new List<(SyntaxNode Node, Type Type)>();
        foreach (var node in nodes)
            if (node is TypeAliasDeclarationNode { Name: IdentifierNode identifier }
                && identifier.Text.Length > 1
                && identifier.Text[0] == 'K'
                && char.IsAsciiDigit(identifier.Text[1]))
                keyRoots.Add((node, await host.Normalization.GetAsync(await host.Declared.GetAsync(symbols.Declaration(node)!))));
        writer.WriteStartArray("relationKeys");
        foreach (var source in keyRoots)
            foreach (var target in keyRoots)
                foreach (bool identity in new[] { false, true })
                    foreach (var intersection in new[] { IntersectionState.None, IntersectionState.Source, IntersectionState.Target })
                        foreach (bool broad in new[] { false, true })
                        {
                            writer.WriteStartArray();
                            writer.WriteNumberValue(nodeId(source.Node));
                            writer.WriteNumberValue(nodeId(target.Node));
                            writer.WriteNumberValue(typeId(source.Type));
                            writer.WriteNumberValue(typeId(target.Type));
                            var (key, constrained) = await host.RelationKeys.CreateAsync(
                                source.Type,
                                target.Type,
                                intersection,
                                identity,
                                broad);
                            if (!keys.TryGetValue(key, out int id))
                                keys.Add(key, id = keys.Count + 1);
                            writer.WriteBooleanValue(identity);
                            writer.WriteNumberValue((uint)intersection);
                            writer.WriteBooleanValue(broad);
                            writer.WriteNumberValue(id);
                            writer.WriteBooleanValue(constrained);
                            writer.WriteEndArray();
                        }
        writer.WriteEndArray();
    }
}
