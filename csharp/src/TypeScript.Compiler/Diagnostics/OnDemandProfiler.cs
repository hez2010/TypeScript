namespace TypeScript.Compiler.Diagnostics;

/// <summary>Owns one editor/API CPU profile and produces on-demand pprof files.</summary>
internal sealed class OnDemandProfiler : IDisposable
{
    private readonly object sync = new();
    private NativeProfile? profile;
    private FileStream? output;
    private Utf8String path;

    internal void Start(Utf8String directory)
    {
        lock (sync)
        {
            if (profile is not null) throw new InvalidOperationException("CPU profiling already in progress");
            var destination = CreateFile(directory, "CPU", "cpu");
            try { profile = NativeProfile.Start(); }
            catch (Exception error)
            {
                destination.File.Dispose(); File.Delete(destination.Path.ToString());
                throw new InvalidOperationException($"failed to start CPU profile: {error.Message}", error);
            }
            (path, output) = destination;
        }
    }

    internal Utf8String Stop()
    {
        lock (sync)
        {
            if (profile is null) throw new InvalidOperationException("CPU profiling not in progress");
            var current = profile;
            try { output!.Dispose(); current.StopCpu(path); return path; }
            finally { profile = null; output = null; current.Dispose(); }
        }
    }

    internal static Utf8String Save(Utf8String directory, bool allocations = false)
    {
        var destination = CreateFile(directory, allocations ? "alloc" : "heap", allocations ? "alloc" : "heap");
        destination.File.Dispose();
        try
        {
            using var snapshot = new NativeProfile();
            if (allocations) snapshot.SaveAllocations(destination.Path); else snapshot.SaveHeap(destination.Path);
            return destination.Path;
        }
        catch (Exception error)
        {
            File.Delete(destination.Path.ToString());
            throw new IOException($"failed to write {(allocations ? "alloc" : "heap")} profile: {error.Message}", error);
        }
    }

    private static (Utf8String Path, FileStream File) CreateFile(Utf8String directory, string label, string kind)
    {
        try { Directory.CreateDirectory(directory.ToString()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new IOException($"failed to create profile directory: {error.Message}", error); }
        var name = Utf8String.FromString(System.IO.Path.Combine(directory.ToString(), $"{Environment.ProcessId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{kind}profile.pb.gz"));
        try { return (name, new FileStream(name.ToString(), FileMode.Create, FileAccess.Write, FileShare.Read)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new IOException($"failed to create {label} profile file: {error.Message}", error); }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (profile is null) return;
            try { Stop(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
