using System.Diagnostics;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Incremental;
using TypeScript.Compiler.LanguageServer;
using TypeScript.Compiler.Projects.TypeAcquisition;
using TypeScript.Compiler.Text;

internal static class LspCommand
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellation)
    {
        bool stdio = false;
        string profileDirectory = "";
        long clientProcessId = 0;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];
                string[]? parts = ServerFlags.Split(argument);
                if (parts is null) break;
                string name = parts[0];
                if (name is "h" or "help")
                {
                    await Console.Error.WriteLineAsync("Usage: --lsp -stdio [-pprofDir directory] [-clientProcessId pid]");
                    return 2;
                }
                if (name is not ("stdio" or "pprofDir" or "pipe" or "socket" or "clientProcessId"))
                    throw new ArgumentException($"flag provided but not defined: -{name}");
                string value = parts.Length == 2 ? parts[1] : name == "stdio" ? "true" : ++i < args.Length ? args[i]
                    : throw new ArgumentException($"flag needs an argument: -{name}");
                if (name == "stdio") stdio = value is "true" or "TRUE" or "True" or "1" or "t" or "T" ? true
                    : value is "false" or "FALSE" or "False" or "0" or "f" or "F" ? false
                    : throw new ArgumentException($"invalid value \"{value}\" for flag -{name}");
                else if (name == "pprofDir") profileDirectory = value;
                else if (name == "clientProcessId") clientProcessId = ServerFlags.Integer(value);
            }
        }
        catch (ArgumentException error) { await Console.Error.WriteLineAsync(error.Message); return 2; }
        if (!stdio) { await Console.Error.WriteLineAsync("only stdio is supported"); return 1; }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task? watchdog = null;
        void WatchParent(int id)
        {
            if (id > 0 && watchdog is null) watchdog = WatchParentAsync(id, lifetime);
        }
        NativeProfile? profile = null;
        Utf8String cpuPath = default, memoryPath = default;
        try
        {
            if (profileDirectory.Length != 0)
            {
                await Console.Error.WriteLineAsync($"pprof profiles will be written to: {profileDirectory}");
                Directory.CreateDirectory(profileDirectory);
                cpuPath = Utf8String.FromString(Path.Combine(profileDirectory, $"{Environment.ProcessId}-cpuprofile.pb.gz"));
                memoryPath = Utf8String.FromString(Path.Combine(profileDirectory, $"{Environment.ProcessId}-memprofile.pb.gz"));
                using (File.Create(cpuPath.ToString())) { }
                profile = NativeProfile.Start();
            }
            if (clientProcessId > 0) watchdog = WatchParentAsync(clientProcessId, lifetime);
            await LanguageServer.RunAsync(new LibraryFileSystem(new PhysicalFileSystem()), Console.OpenStandardInput(), Console.OpenStandardOutput(), new()
            {
                CurrentDirectory = Utf8String.FromString(Environment.CurrentDirectory), ErrorWriter = Console.Error,
                ProgressDelay = TimeSpan.FromMilliseconds(250), TypingsLocation = TypingsLocation(), Npm = new ProcessNpmExecutor(),
                SetParentProcessId = clientProcessId > 0 ? null : WatchParent,
            }, lifetime.Token);
            return 0;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return 0; }
        catch (Exception error) { await Console.Error.WriteLineAsync(error.Message); return 1; }
        finally
        {
            lifetime.Cancel();
            if (watchdog is not null) await watchdog;
            if (profile is not null)
            {
                using (profile)
                {
                    profile.StopCpu(cpuPath);
                    profile.SaveAllocations(memoryPath);
                    await Console.Error.WriteLineAsync($"Memory profile: {memoryPath}\nCPU profile: {cpuPath}");
                }
            }
        }
    }

    private static async Task WatchParentAsync(long id, CancellationTokenSource lifetime)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                bool exited;
                try { using var parent = Process.GetProcessById(OperatingSystem.IsWindows() ? unchecked((int)id) : checked((int)id)); exited = parent.HasExited; }
                catch (ArgumentException) { exited = true; }
                catch (OverflowException) { exited = true; }
                catch (System.ComponentModel.Win32Exception) { continue; }
                if (!exited) continue;
                await Console.Error.WriteLineAsync($"Parent process {id} has exited, shutting down.");
                lifetime.Cancel(); return;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private static Utf8String TypingsLocation()
    {
        string cache = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches")
            : Environment.GetEnvironmentVariable("XDG_CACHE_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        if (cache.Length == 0) cache = Path.GetTempPath();
        string version = string.Join('.', BuildInfo.CompilerVersion.ToString().Split('.').Take(2));
        return Utf8String.FromString(Path.Combine(cache, OperatingSystem.IsWindows() ? "Microsoft/TypeScript" : "typescript", version));
    }
}
