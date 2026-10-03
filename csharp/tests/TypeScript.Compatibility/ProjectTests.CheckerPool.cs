using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Projects;

namespace TypeScript.Compatibility;

internal static partial class ProjectTests
{
    private static async Task<int> CheckerPoolContractsAsync(CompilerProgram program)
    {
        int checks = 0;
        void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException(reason); }
        var file = program.SourceFiles[0].Syntax;
        foreach (int maximum in new[] { 0, 1, 4 })
        {
            using var pool = new ProjectCheckerPool(program, maximum);
            int count = maximum == 1 ? 1 : 3;
            var requests = Enumerable.Range(0, count + 1).Select(_ => new ProjectRequest()).ToArray();
            List<ProjectCheckerPool.Lease> held = [];
            try
            {
                foreach (var request in requests.Take(count)) held.Add(await pool.AcquireAsync(ProjectCheckerLifetime.Query, request, file));
                Check(held.Select(lease => lease.Checker).Distinct().Count() == count, "Concurrent queries own distinct checkers up to the configured limit");
                using var diagnostic = await pool.AcquireAsync(ProjectCheckerLifetime.Diagnostics, requests[0]);
                using var api = await pool.AcquireAsync(ProjectCheckerLifetime.Api, requests[0]);
                Check(held.All(lease => lease.Checker != diagnostic.Checker && lease.Checker != api.Checker)
                    && diagnostic.Checker != api.Checker, "Saturated queries do not block diagnostics or API ownership");
                var waiting = pool.AcquireAsync(ProjectCheckerLifetime.Query, requests[^1], file).AsTask();
                Check(!waiting.IsCompleted, "The next query waits for an available slot");
                var first = held[0].Checker;
                held[0].Dispose(); held[0].Dispose();
                using var resumed = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
                Check(resumed.Checker == first, "A double release wakes one waiter and preserves the released checker");
            }
            finally
            {
                foreach (var lease in held) lease.Dispose();
                foreach (var request in requests) request.Dispose();
            }
        }

        using (var pool = new ProjectCheckerPool(program, 2))
        using (var request = new ProjectRequest())
        {
            Checker diagnostics;
            using (var lease = await pool.AcquireAsync(ProjectCheckerLifetime.Diagnostics, request)) diagnostics = lease.Checker;
            using (var query = await pool.AcquireAsync(ProjectCheckerLifetime.Query, request, file))
            {
                Check(query.Checker != diagnostics, "Changing a request's lifetime ignores its previous category");
                var nested = pool.AcquireAsync(ProjectCheckerLifetime.Query, request, file);
                Check(nested.IsCompletedSuccessfully, "A lifetime switch replaces affinity so nested acquisition cannot deadlock");
                using var lease = await nested;
                Check(lease.Checker == query.Checker, "Nested acquisition after a lifetime switch reuses the current checker");
            }
            using var diagnostic = await pool.AcquireAsync(ProjectCheckerLifetime.Diagnostics, request);
            using var nestedDiagnostic = await pool.AcquireAsync(ProjectCheckerLifetime.Diagnostics, request);
            Check(diagnostic.Checker == diagnostics && nestedDiagnostic.Checker == diagnostics, "Switching back restores diagnostic affinity");
        }

        foreach (var lifetime in new[] { ProjectCheckerLifetime.Query, ProjectCheckerLifetime.Diagnostics, ProjectCheckerLifetime.Api })
        {
            using var pool = new ProjectCheckerPool(program, 2);
            using var owner = new ProjectRequest();
            using var held = await pool.AcquireAsync(lifetime, owner, file);
            using var cancel = new CancellationTokenSource();
            using var waitingRequest = new ProjectRequest(cancel.Token);
            var waiting = pool.AcquireAsync(lifetime, waitingRequest, file).AsTask();
            Check(!waiting.IsCompleted, "Each checker category enforces exclusive ownership");
            cancel.Cancel();
            try { using var lease = await waiting; Check(false, "Canceled acquisition must fail"); }
            catch (OperationCanceledException) { checks++; }
            var original = held.Checker;
            held.Dispose();
            using var nextRequest = new ProjectRequest();
            using var next = await pool.AcquireAsync(lifetime, nextRequest, file);
            Check(next.Checker == original, "A canceled waiter does not poison a checker it never acquired");
            pool.Discard(); pool.Discard();
            next.Dispose();
            using var retained = await pool.AcquireAsync(lifetime, nextRequest, file);
            Check(retained.Checker == original, "Discarding while held and releasing preserves every category's identity");
            await retained.Checker.GetTypeAtLocationAsync(file.Statements![0]);
            Check(true, "A discarded program's retained checkers remain usable");
        }

        var time = new ManualTime();
        using (var pool = new ProjectCheckerPool(program, 3, time: time))
        using (var firstRequest = new ProjectRequest())
        using (var secondRequest = new ProjectRequest())
        {
            var first = await pool.AcquireAsync(ProjectCheckerLifetime.Query, firstRequest, file);
            var second = await pool.AcquireAsync(ProjectCheckerLifetime.Query, secondRequest);
            Checker firstChecker = first.Checker, secondChecker = second.Checker;
            first.Dispose();
            time.Advance(TimeSpan.FromSeconds(20));
            second.Dispose();
            time.Advance(TimeSpan.FromSeconds(9)); pool.ExpireIdle();
            using (var before = await pool.AcquireAsync(ProjectCheckerLifetime.Query, firstRequest, file))
                Check(before.Checker == firstChecker, "Default idle timeout is thirty seconds");
            time.Advance(TimeSpan.FromSeconds(22)); pool.ExpireIdle();
            using var kept = await pool.AcquireAsync(ProjectCheckerLifetime.Query, firstRequest, file);
            using var expired = await pool.AcquireAsync(ProjectCheckerLifetime.Query, secondRequest);
            Check(kept.Checker == firstChecker && expired.Checker != secondChecker, "Cleanup respects each checker's last release and removes stale affinity");
            pool.Discard();
            kept.Dispose(); expired.Dispose();
            time.Advance(TimeSpan.FromDays(1)); pool.ExpireIdle();
            using var afterDiscard = await pool.AcquireAsync(ProjectCheckerLifetime.Query, firstRequest, file);
            Check(afterDiscard.Checker == firstChecker, "Cleanup after discard leaves retained query checkers intact");
        }

        using (var pool = new ProjectCheckerPool(program))
        using (var request = new ProjectRequest())
        {
            Check(pool.GlobalDiagnostics.Count == 0 && !pool.TakeNewGlobalDiagnostics(), "Global diagnostics start empty");
            using (var lease = await pool.AcquireAsync(ProjectCheckerLifetime.Diagnostics, request))
                await lease.Checker.CheckSourceFileAsync(file);
            _ = pool.TakeNewGlobalDiagnostics();
            Check(!pool.TakeNewGlobalDiagnostics(), "Taking the global diagnostic flag resets it");
            using (var lease = await pool.AcquireAsync(ProjectCheckerLifetime.Diagnostics, request))
                await lease.Checker.CheckSourceFileAsync(file);
            Check(!pool.TakeNewGlobalDiagnostics(), "Unchanged diagnostics do not notify again");
        }
        return checks;
    }
}
