using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Protocol;

namespace TypeScript.Compiler.Api;

public sealed record ApiServerOptions
{
    public required Utf8String CurrentDirectory { get; init; }
    public Utf8String DefaultLibraryDirectory { get; init; }
    public Utf8String PipePath { get; init; }
    public IReadOnlyList<Utf8String> Callbacks { get; init; } = [];
    public bool Async { get; init; }
    public bool CollectTiming { get; init; }
    public bool RunExternalCode { get; init; }
    public bool LeaveOpen { get; init; }
}

public static class ApiServer
{
    public static async Task RunAsync(IFileSystem fileSystem, Stream input, Stream output, ApiServerOptions options, CancellationToken cancellation = default)
    {
        if (options.CurrentDirectory.IsEmpty) throw new ArgumentException("CurrentDirectory is required", nameof(options));
        Utf8String libraryDirectory = options.DefaultLibraryDirectory.IsEmpty && fileSystem is LibraryFileSystem libraries
            ? libraries.LibraryDirectory : options.DefaultLibraryDirectory;
        if (!options.PipePath.IsEmpty)
        {
            using var listener = new RpcPipeListener(options.PipePath);
            await using var pipe = await listener.AcceptAsync(cancellation).ConfigureAwait(false);
            await ServeAsync(fileSystem, pipe, pipe, options with { LeaveOpen = true }, libraryDirectory, cancellation).ConfigureAwait(false);
        }
        else await ServeAsync(fileSystem, input, output, options, libraryDirectory, cancellation).ConfigureAwait(false);
    }
    private static async Task ServeAsync(IFileSystem fileSystem, Stream input, Stream output, ApiServerOptions options, Utf8String libraries, CancellationToken cancellation)
    {
        RpcConnection? connection = null;
        if (options.Callbacks.Count != 0) fileSystem = new CallbackFileSystem(fileSystem, options.Callbacks,
            (method, parameters, token) => connection!.CallAsync(method, parameters, token));
        await using var session = new ApiSession(fileSystem, new ProjectHostOptions
        {
            CurrentDirectory = options.CurrentDirectory, DefaultLibraryDirectory = libraries, RunExternalCode = options.RunExternalCode,
        }, new() { UseBinaryResponses = !options.Async });
        await using (connection = new(input, output, session, new()
        {
            UseMessagePack = !options.Async, CollectTiming = options.CollectTiming, LeaveOpen = options.LeaveOpen,
        }))
        {
            try { await connection.RunAsync(cancellation).ConfigureAwait(false); }
            catch (Exception) when (cancellation.IsCancellationRequested) { }
        }
    }
}
