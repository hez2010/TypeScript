using System.Text.Json;
using System.Threading.Channels;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Incremental;
using TypeScript.Compiler.Projects;
using TypeScript.Compiler.Protocol;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.LanguageServer;

// The queue owns serialized summaries only; it never keeps a project, checker, or snapshot alive.
internal sealed class SessionTelemetry : IAsyncDisposable
{
    private readonly Channel<(Utf8String Id, ReadOnlyMemory<byte> Data)> events = Channel.CreateUnbounded<(Utf8String, ReadOnlyMemory<byte>)>(new() { SingleReader = true });
    private readonly HashSet<Utf8String> seen = [], pending = [];
    private readonly CancellationTokenSource lifetime;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send;
    private readonly Func<ValueTask<RpcResponse?>> collect;
    private readonly PeriodicTimer timer;
    private readonly Task publisher, sampler;
    private Task? disposal;
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    internal SessionTelemetry(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send,
        Func<ValueTask<RpcResponse?>> collect, TimeProvider timeProvider, CancellationToken cancellation)
    {
        this.send = send; this.collect = collect;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timer = new(Interval, timeProvider);
        publisher = PublishAsync(); sampler = SampleAsync();
    }

    internal void ProjectAdded(ProjectSnapshot project)
    {
        if (project.Program is null) return;
        lock (seen)
        {
            if (disposal is not null || seen.Contains(project.Id) || !pending.Add(project.Id)) return;
            if (!events.Writer.TryWrite((project.Id, ProjectInfo(project).Data))) pending.Remove(project.Id);
        }
    }

    internal void RequestFailure(Utf8String method, Utf8String stack) => events.Writer.TryWrite((default, Failure(method, stack).Data));

    private async Task PublishAsync()
    {
        try
        {
            await foreach (var item in events.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    await send(item.Data, lifetime.Token).ConfigureAwait(false);
                    if (!item.Id.IsEmpty) lock (seen) seen.Add(item.Id);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException) { }
                finally { if (!item.Id.IsEmpty) lock (seen) pending.Remove(item.Id); }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { while (events.Reader.TryRead(out _)) { } }
    }

    private async Task SampleAsync()
    {
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
                if (await collect().ConfigureAwait(false) is { } data) events.Writer.TryWrite((default, data.Data));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (seen) return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false); timer.Dispose(); events.Writer.TryComplete();
        await Task.WhenAll(publisher, sampler).ConfigureAwait(false);
        lock (seen) { seen.Clear(); pending.Clear(); }
        lifetime.Dispose();
    }

    internal static RpcResponse ProjectInfo(ProjectSnapshot project)
    {
        var options = project.Configuration.Options;
        var compilerOptions = RpcResponse.Json(writer =>
        {
            // Keep the nested JSON stable. These are the only options permitted in telemetry.
            var values = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (Utf8String name in new Utf8String[] { "strict"u8, "noImplicitAny"u8, "noImplicitThis"u8, "strictNullChecks"u8,
                "strictFunctionTypes"u8, "strictBindCallApply"u8, "strictPropertyInitialization"u8, "strictBuiltinIteratorReturn"u8,
                "useUnknownInCatchVariables"u8, "exactOptionalPropertyTypes"u8, "allowJs"u8, "checkJs"u8, "noEmit"u8,
                "declaration"u8, "composite"u8, "isolatedModules"u8, "skipLibCheck"u8, "incremental"u8 })
                if (options.Get(name) is { ValueKind: JsonValueKind.True or JsonValueKind.False } value) values[name.ToString()] = value;
            if (options.Target != ScriptTarget.None) values["target"] = OptionValues.String(Utf8String.FromString(options.Target.ToString()));
            if (options.Module != ModuleKind.None) values["module"] = OptionValues.String(Utf8String.FromString(options.Module.ToString()));
            if (options.ModuleResolution != ModuleResolutionKind.Unknown) values["moduleResolution"] = OptionValues.String(Utf8String.FromString(options.ModuleResolution.ToString()));
            if (options.Jsx != JsxEmit.None) values["jsx"] = OptionValues.String(options.Jsx switch
            {
                JsxEmit.Preserve => "preserve"u8, JsxEmit.React => "react"u8, JsxEmit.ReactNative => "react-native"u8,
                JsxEmit.ReactJSX => "react-jsx"u8, JsxEmit.ReactJSXDev => "react-jsxdev"u8, _ => default,
            });
            writer.WriteStartObject(); foreach (var (name, value) in values) { writer.WritePropertyName(name); value.WriteTo(writer); } writer.WriteEndObject();
        });
        var counts = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var source in project.Program!.SourceFiles)
        {
            var file = source.Syntax;
            var kind = file.ScriptKind switch { ScriptKind.JS => "js", ScriptKind.JSX => "jsx", ScriptKind.TSX => "tsx",
                ScriptKind.TS => file.IsDeclarationFile ? "dts" : "ts", _ => null };
            if (kind is null) continue;
            counts[kind + "FileCount"] = counts.GetValueOrDefault(kind + "FileCount") + 1;
            counts[kind + "FileSize"] = counts.GetValueOrDefault(kind + "FileSize") + file.End;
        }
        return RpcResponse.Json(writer =>
        {
            Header(writer, "languageServer.projectInfo"u8, "usage"u8); writer.WriteStartObject("properties"u8);
            var name = project.Kind == ProjectKind.Configured ? CompilerPath.BaseName(project.Configuration.FileName) : default;
            LspJson.String(writer, "configFileName"u8, name == "tsconfig.json"u8 || name == "jsconfig.json"u8 ? name : "other"u8);
            writer.WriteString("projectType"u8, project.Kind == ProjectKind.Configured ? "configured"u8 : "inferred"u8);
            LspJson.String(writer, "version"u8, BuildInfo.CompilerVersion);
            writer.WriteString("compilerOptions"u8, compilerOptions.Data.Span);
            if (project.Configuration.Raw is { ValueKind: JsonValueKind.Object } raw)
                foreach (string property in new[] { "extends", "files", "include", "exclude" })
                    writer.WriteString(property, raw.TryGetProperty(property, out _) ? "true" : "false");
            writer.WriteEndObject(); Measurements(writer, counts); writer.WriteEndObject();
        });
    }

    internal static RpcResponse Performance(IReadOnlyDictionary<string, double> measurements) => RpcResponse.Json(writer =>
    {
        Header(writer, "languageServer.performanceStats"u8, "usage"u8); Measurements(writer, measurements); writer.WriteEndObject();
    });

    internal static RpcResponse Failure(Utf8String method, Utf8String stack) => RpcResponse.Json(writer =>
    {
        Header(writer, "languageServer.errorResponse"u8, "error"u8); writer.WriteStartObject("properties"u8);
        writer.WriteString("errorCode"u8, "InternalError"u8); writer.WriteString("requestMethod"u8, method.ToString().Replace('/', '.'));
        LspJson.String(writer, "stack"u8, stack); writer.WriteEndObject(); writer.WriteEndObject();
    });

    private static void Header(Utf8JsonWriter writer, ReadOnlySpan<byte> name, ReadOnlySpan<byte> purpose)
    { writer.WriteStartObject(); writer.WriteString("eventName"u8, name); writer.WriteString("telemetryPurpose"u8, purpose); }

    private static void Measurements(Utf8JsonWriter writer, IReadOnlyDictionary<string, double> measurements)
    {
        writer.WriteStartObject("measurements"u8);
        foreach (var (name, value) in measurements) if (value != 0 && double.IsFinite(value)) writer.WriteNumber(name, value);
        writer.WriteEndObject();
    }
}
