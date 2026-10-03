using System.Diagnostics;
using System.Text.Json;
using TypeScript.Compiler.Projects.TypeAcquisition;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class NpmProcessTests
{
    internal static async Task<int> SafetyAsync()
    {
        string executable = OperatingSystem.IsWindows() ? "node.exe" : "node";
        string node = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory.Trim('"'), executable)).First(File.Exists);
        string directory = Path.Combine(Path.GetTempPath(), "typescript-npm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int assertions = 0;
        void Check(bool value, string reason) { assertions++; if (!value) throw new InvalidOperationException(reason); }
        var ownedProcesses = new List<int>();
        try
        {
            string script = Path.Combine(directory, "npm-mock.cjs");
            await File.WriteAllTextAsync(script, """
                const fs = require('node:fs');
                if (process.argv[2] === 'wait') {
                    const child = require('node:child_process').spawn(process.execPath,
                        ['-e', 'setInterval(() => {}, 1000)'], { windowsHide: true, stdio: 'ignore' });
                    child.on('spawn', () => fs.writeFileSync('pids.json', JSON.stringify([process.pid, child.pid])));
                    setInterval(() => {}, 1000);
                } else {
                    process.stdout.write(JSON.stringify(process.argv.slice(2)) + '\n' + 'x'.repeat(1024 * 1024));
                    process.stderr.write('y'.repeat(1024 * 1024));
                    process.exitCode = 7;
                }
                """);
            var executor = new ProcessNpmExecutor(Utf8String.FromString(node), [Utf8String.FromString(script)]);
            Utf8String[] arguments = ["a b"u8, "\"quoted\""u8, "日本語"u8, "$(untouched);&"u8];
            var output = await executor.InstallAsync(Utf8String.FromString(directory), arguments, default);
            Check(output.ExitCode == 7, "The owned process exit status is returned");
            int newline = output.Output.IndexOf((byte)'\n');
            using var values = JsonDocument.Parse(output.Output[..newline].Memory);
            Check(values.RootElement.EnumerateArray().Select(value => value.GetString()).SequenceEqual(arguments.Select(value => value.ToString())),
                "Spaces, quotes, Unicode and shell metacharacters keep their argument boundaries");
            Check(output.Output.Length == newline + 1 + 2 * 1024 * 1024, "Both full output streams are drained without deadlock");
            using var cancellation = new CancellationTokenSource();
            var pending = executor.InstallAsync(Utf8String.FromString(directory), ["wait"u8], cancellation.Token).AsTask();
            string pids = Path.Combine(directory, "pids.json");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(pids)) await Task.Delay(10, timeout.Token);
            using (var processes = JsonDocument.Parse(await File.ReadAllTextAsync(pids)))
                ownedProcesses.AddRange(processes.RootElement.EnumerateArray().Select(value => value.GetInt32()));
            cancellation.Cancel();
            try { await pending.WaitAsync(timeout.Token); Check(false, "Cancellation must propagate after process cleanup"); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested && !timeout.IsCancellationRequested) { assertions++; }
            foreach (int id in ownedProcesses)
            {
                try { using var process = Process.GetProcessById(id); await process.WaitForExitAsync(timeout.Token); Check(process.HasExited, "The owned process tree is joined"); }
                catch (ArgumentException) { assertions++; }
            }
            return assertions;
        }
        finally
        {
            foreach (int id in ownedProcesses)
            {
                try { using var process = Process.GetProcessById(id); if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
                catch (ArgumentException) { }
            }
            Directory.Delete(directory, recursive: true);
        }
    }
}
