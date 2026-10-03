using System.Text;
using TypeScript.Compiler.Execution;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Text;

Console.OutputEncoding = new UTF8Encoding(false);
using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, signal) => { signal.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    if (args.Length > 0 && args[0] == "--api") return await ApiCommand.RunAsync(args[1..], cancellation.Token);
    if (args.Length > 0 && args[0] == "--lsp") return await LspCommand.RunAsync(args[1..], cancellation.Token);
    await using var command = new CompilerCommand(new LibraryFileSystem(new PhysicalFileSystem()),
        Utf8String.FromString(Environment.CurrentDirectory), Console.Out, outputIsTerminal: !Console.IsOutputRedirected)
    { TerminalWidth = Console.IsOutputRedirected ? 0 : Console.WindowWidth };
    var status = await command.ExecuteAsync(args.Select(Utf8String.FromString).ToArray(), cancellation.Token);
    if (command.Watches is not null) await command.RunWatchAsync(cancellation.Token);
    return (int)status;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 0; }
finally { Console.CancelKeyPress -= cancel; }
