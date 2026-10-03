using System.Runtime.CompilerServices;
using TypeScript.Compiler;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Projects;

namespace TypeScript.Compatibility;

internal static class ReferenceTests
{
    internal static async Task<int> SafetyAsync()
    {
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        const int depth = 2048;
        var text = Utf8String.FromString("export const 名前 = '😀';\r\n" + string.Concat(Enumerable.Repeat("function f(){", depth)) + "return 名前;" + new string('}', depth));
        Utf8String members = "export interface 表 { 名前: string; } export class 子 implements 表 { constructor(public 名前: string) {} } new 子('😀').名前;"u8;
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/deep.ts"u8] = text.Span.ToArray(), ["/members.ts"u8] = members.Span.ToArray() }, true);
        var options = new CompilerOptions(); options.SetRaw("noLib"u8, "true"u8);
        await using var host = new ProjectSnapshotHost(fs);
        await using var snapshot = await host.CreateAsync(new() { CreatePrograms = [new(["/deep.ts"u8], options), new(["/members.ts"u8], options)] });
        var project = snapshot.CreatedPrograms[0];
        var file = project.Program!.GetFile("/deep.ts"u8)!;
        var original = file.Syntax.DescendantsAndSelf().Select(node => (Node: node, node.Parent, node.Pos, node.End, node.Flags)).ToArray();
        var service = new LanguageServiceDocument(project.Program, file);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var position = new DocumentLineMap(text).ToPosition(text.LastIndexOf("名前"u8), PositionEncoding.Utf8);
        var references = await service.GetReferencesAsync(project, position, cancellation: deadline.Token);
        Check(references.Length == 2 && references[0].Range == new DocumentRange(new(0, 13), new(0, 19)), "Deep reference search includes the declaration with its byte range");
        var usages = await service.GetReferencesAsync(project, position, false, deadline.Token);
        Check(usages is [var usage] && usage == references[1], "Excluding declarations retains the nested usage");
        var visual = await service.GetVisualStudioReferencesAsync(project, position, true, deadline.Token);
        Check(visual is [var definition, var reference] && definition.DefinitionId is null && reference.DefinitionId == definition.Id
            && reference.Id != definition.Id && reference.Location == references[1], "VS references preserve definition groups and locations");
        Check(visual[0].DefinitionText is { Count: > 1 } && visual[1].Kind == 3, "VS references classify definitions and reads");
        var plain = await service.GetVisualStudioReferencesAsync(project, position, false, deadline.Token);
        Check(plain[0].DefinitionText is [var run] && run.Classification == "text"u8, "VS clients without classified text receive a single text run");
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => service.GetReferencesAsync(project, position, cancellation: deadline.Token).AsTask()));
        Check(concurrent.All(result => result.SequenceEqual(references)), "Concurrent searches retain symbol identity and ordering");
        var utf16 = new LanguageServiceDocument(project.Program, file, PositionEncoding.Utf16);
        var converted = await utf16.GetReferencesAsync(project, new DocumentLineMap(text).ToPosition(text.LastIndexOf("名前"u8), PositionEncoding.Utf16), cancellation: deadline.Token);
        Check(converted.Length == 2 && converted[0].Range == new DocumentRange(new(0, 13), new(0, 15)), "Reference ranges use the negotiated position encoding");
        Check(original.All(item => item.Node.Parent == item.Parent && item.Node.Pos == item.Pos && item.Node.End == item.End && item.Node.Flags == item.Flags), "Reference search preserves the source tree");
        Check(await service.GetReferencesAsync(project, new(uint.MaxValue, 0), cancellation: deadline.Token) is [], "Reference search beyond EOF is empty");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await service.GetReferencesAsync(project, position, cancellation: cancelled.Token); Check(false, "Pre-canceled reference search must throw"); }
        catch (OperationCanceledException) { checks++; }
        try { await service.GetVisualStudioReferencesAsync(project, position, cancellation: cancelled.Token); Check(false, "Canceled VS references must throw"); }
        catch (OperationCanceledException) { checks++; }

        var memberProject = snapshot.CreatedPrograms[1];
        var memberFile = memberProject.Program!.GetFile("/members.ts"u8)!;
        var property = memberFile.Syntax.DescendantsAndSelf().OfType<IdentifierNode>().First(node => node.Text == "名前"u8);
        using var during = new CancellationTokenSource();
        using var request = new ProjectRequest(during.Token);
        bool reached = false;
        using (var lease = await memberProject.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, request))
        {
            var interrupted = lease.Checker;
            interrupted.BeforeMemberTable = _ => { reached = true; during.Cancel(); };
            try { await interrupted.GetReferenceGroupsAsync(property, property.Pos, [memberFile.Syntax], new(ReferenceUse.References), during.Token); Check(false, "Member lookup cancellation must throw"); }
            catch (OperationCanceledException) { checks++; }
        }
        Check(reached, "The cancellation control runs during member lookup");
        using var nextRequest = new ProjectRequest(deadline.Token);
        using (var lease = await memberProject.Resource!.Checkers.AcquireAsync(ProjectCheckerLifetime.Query, nextRequest))
        {
            lease.Checker.BeforeMemberTable = null;
            var recovered = await lease.Checker.GetReferenceGroupsAsync(property, property.Pos, [memberFile.Syntax], new(ReferenceUse.References), deadline.Token);
            Check(recovered.SelectMany(group => group.Entries).Select(entry => entry.Node).Distinct().Count() == 3,
                "An interrupted search releases ownership and leaves subsequent queries usable");
        }
        var memberService = new LanguageServiceDocument(memberProject.Program, memberFile);
        var memberPosition = new DocumentLineMap(members).ToPosition(members.IndexOf("名前"u8), PositionEncoding.Utf8);
        Check((await memberService.GetReferencesAsync(memberProject, memberPosition, cancellation: deadline.Token)).Length == 3, "Inherited parameter properties and their usages share references");
        Check((await memberService.GetReferencesAsync(memberProject, memberPosition, false, deadline.Token)).Length == 1, "Declaration filtering handles both interface and parameter-property declarations");
        var interfacePosition = new DocumentLineMap(members).ToPosition(members.IndexOf("表"u8), PositionEncoding.Utf8);
        var implementations = await memberService.GetImplementationsAsync(memberProject, interfacePosition, true, deadline.Token);
        int classStart = members.IndexOf("子"u8);
        Check(implementations is [var implementation] && implementation.SelectionRange == new DocumentRange(new(0, (uint)classStart), new(0, (uint)(classStart + 3))), "Implementation traversal deduplicates the derived class");
        var released = await ReleasedSessionAsync();
        for (int i = 0; i < 3 && released.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Check(!released.IsAlive, "Disposing a cross-project search and session releases its program graph");
        return checks;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> ReleasedSessionAsync()
    {
        var fs = new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/a.ts"u8] = "export const a = 1; a;"u8.ToArray() }, true);
        await using var session = new ProjectSession(fs);
        var options = new CompilerOptions(); options.SetRaw("noLib"u8, "true"u8); session.SetInferredOptions(options);
        session.Notify(new(FileChangeKind.Open, "/a.ts"u8, 1, "export const a = 1; a;"u8));
        await using var snapshot = await session.GetSnapshotAsync(["/a.ts"u8]);
        await CrossProjectReferences.ReferencesAsync(session, snapshot.Snapshot, "/a.ts"u8, new(0, 19), true, PositionEncoding.Utf8, default);
        return new(snapshot.Snapshot.GetDefaultProject("/a.ts"u8)!.Program);
    }
}
