using TypeScript.Compiler.Api;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Text;

internal static class ApiCommand
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellation)
    {
        var options = new ApiServerOptions { CurrentDirectory = Utf8String.FromString(Environment.CurrentDirectory) };
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];
                if (!argument.StartsWith('-') || argument == "--") break;
                string[] parts = argument.TrimStart('-').Split('=', 2); string name = parts[0];
                bool boolean = name is "async" or "timing" or "runExternalCode";
                if (name is "h" or "help")
                {
                    await Console.Error.WriteLineAsync("Usage: --api [-cwd directory] [-pipe path] [-callbacks names] [-async] [-timing] [-runExternalCode]");
                    return 2;
                }
                if (name is not ("cwd" or "pipe" or "callbacks" or "async" or "timing" or "runExternalCode"))
                    throw new ArgumentException($"flag provided but not defined: -{name}");
                string value = parts.Length == 2 ? parts[1] : boolean ? "true" : ++i < args.Length ? args[i]
                    : throw new ArgumentException($"flag needs an argument: -{name}");
                bool enabled = boolean && (value is "true" or "TRUE" or "True" or "1" or "t" or "T" ? true
                    : value is "false" or "FALSE" or "False" or "0" or "f" or "F" ? false : throw new ArgumentException($"invalid value \"{value}\" for flag -{name}"));
                options = name switch
                {
                    "cwd" => options with { CurrentDirectory = Utf8String.FromString(value) },
                    "pipe" => options with { PipePath = Utf8String.FromString(value) },
                    "callbacks" => options with { Callbacks = value.Length == 0 ? [] : value.Split(',').Select(Utf8String.FromString).ToArray() },
                    "async" => options with { Async = enabled },
                    "timing" => options with { CollectTiming = enabled },
                    _ => options with { RunExternalCode = enabled },
                };
            }
        }
        catch (ArgumentException error) { await Console.Error.WriteLineAsync(error.Message); return 2; }
        try
        {
            await ApiServer.RunAsync(new LibraryFileSystem(new PhysicalFileSystem()), Console.OpenStandardInput(), Console.OpenStandardOutput(), options, cancellation);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return 0; }
        catch (Exception error) { await Console.Error.WriteLineAsync(error.Message); return 1; }
    }
}
