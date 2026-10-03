using System.Diagnostics;

namespace TypeScript.Compiler.Projects.TypeAcquisition;

/// <summary>Runs npm with argument boundaries preserved and joins both output streams before returning.</summary>
public sealed class ProcessNpmExecutor(Utf8String executable = default, IReadOnlyList<Utf8String>? prefixArguments = null) : INpmExecutor
{
    public async ValueTask<NpmResult> InstallAsync(Utf8String directory, IReadOnlyList<Utf8String> arguments, CancellationToken cancellation)
    {
        var command = executable.IsEmpty ? FindNpm() : (executable, prefixArguments?.ToArray() ?? []);
        var start = new ProcessStartInfo(command.Item1.ToString())
        {
            WorkingDirectory = directory.ToString(), UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in command.Item2.Concat(arguments)) start.ArgumentList.Add(argument.ToString());
        cancellation.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("Could not start npm");
        using var registration = cancellation.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        using var stdout = new MemoryStream(); using var stderr = new MemoryStream();
        var output = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var errorOutput = process.StandardError.BaseStream.CopyToAsync(stderr);
        await Task.WhenAll(output, errorOutput, process.WaitForExitAsync()).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return new(Utf8String.Concat(stdout.GetBuffer().AsSpan(0, checked((int)stdout.Length)),
            stderr.GetBuffer().AsSpan(0, checked((int)stderr.Length))), process.ExitCode);
    }

    private static (Utf8String, Utf8String[]) FindNpm()
    {
        if (!OperatingSystem.IsWindows()) return ("npm"u8, []);
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;
            string root = directory.Trim('"');
            string node = Path.Combine(root, "node.exe"), script = Path.Combine(root, "node_modules", "npm", "bin", "npm-cli.js");
            if (File.Exists(node) && File.Exists(script)) return (Utf8String.FromString(node), [Utf8String.FromString(script)]);
        }
        throw new FileNotFoundException("Could not find Node.js and npm together on PATH");
    }
}
