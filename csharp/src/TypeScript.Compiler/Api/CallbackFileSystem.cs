using System.Text.Json;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Protocol;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Api;

/// <summary>Routes selected filesystem operations to an API client. A null reply delegates to the host.</summary>
public sealed class CallbackFileSystem : IFileSystem, ICompilerSourceProvider
{
    private readonly IFileSystem host;
    private readonly HashSet<Utf8String> callbacks;
    private readonly Func<Utf8String, ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> call;
    public CallbackFileSystem(IFileSystem host, IEnumerable<Utf8String> callbacks,
        Func<Utf8String, ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> call)
    {
        this.host = host; this.callbacks = [.. callbacks]; this.call = call;
        foreach (var name in this.callbacks)
            if (name != "readFile"u8 && name != "fileExists"u8 && name != "directoryExists"u8
                && name != "getAccessibleEntries"u8 && name != "realpath"u8 && name != "writeFile"u8)
                throw new ArgumentException($"unknown callback name: {name}", nameof(callbacks));
    }
    public bool CaseSensitive => host.CaseSensitive;
    private ReadOnlyMemory<byte> Call(Utf8String name, RpcResponse parameters) =>
        call(name, parameters.Data, RpcConnection.RequestCancellation).AsTask().GetAwaiter().GetResult();
    private JsonDocument? Read(Utf8String name, Utf8String path)
    {
        if (!callbacks.Contains(name)) return null;
        var bytes = Call(name, RpcResponse.String(path));
        if (bytes.IsEmpty || bytes.Span.SequenceEqual("null"u8)) return null;
        return JsonDocument.Parse(bytes, new() { MaxDepth = int.MaxValue });
    }
    public byte[]? ReadFile(Utf8String path)
    {
        using var response = Read("readFile"u8, path);
        if (response is null || response.RootElement.ValueKind == JsonValueKind.Null) return host.ReadFile(path);
        var content = ApiJson.Get(response.RootElement, "content"u8);
        return content.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : ApiJson.String(content).Span.ToArray();
    }
    public CompilerSource? ReadSource(Utf8String path)
    {
        using var response = Read("readFile"u8, path);
        if (response is null || response.RootElement.ValueKind == JsonValueKind.Null)
            return host is ICompilerSourceProvider source ? source.ReadSource(path)
                : host.ReadFile(path) is { } bytes ? new(new(SourceEncoding.DecodeBytes(bytes)), ScriptKind.Unknown) : null;
        var content = ApiJson.Get(response.RootElement, "content"u8);
        return content.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : new(new(ApiJson.String(content)), ScriptKind.Unknown);
    }
    private bool? Exists(Utf8String name, Utf8String path)
    {
        if (!callbacks.Contains(name)) return null;
        var bytes = Call(name, RpcResponse.String(path));
        return bytes.IsEmpty || bytes.Span.SequenceEqual("null"u8) ? null : bytes.Span.SequenceEqual("true"u8);
    }
    public bool FileExists(Utf8String path) => Exists("fileExists"u8, path) ?? host.FileExists(path);
    public bool DirectoryExists(Utf8String path) => Exists("directoryExists"u8, path) ?? host.DirectoryExists(path);
    public DirectoryEntries GetAccessibleEntries(Utf8String path)
    {
        using var response = Read("getAccessibleEntries"u8, path);
        return response is null || response.RootElement.ValueKind == JsonValueKind.Null ? host.GetAccessibleEntries(path)
            : new(ApiJson.Strings(response.RootElement, "files"u8), ApiJson.Strings(response.RootElement, "directories"u8));
    }
    public Utf8String RealPath(Utf8String path)
    {
        using var response = Read("realpath"u8, path);
        return response is null ? host.RealPath(path) : ApiJson.String(response.RootElement);
    }
    public void WriteFile(Utf8String path, ReadOnlySpan<byte> contents)
    {
        if (!callbacks.Contains("writeFile"u8)) { host.WriteFile(path, contents); return; }
        var data = new Utf8String(contents.ToArray());
        Call("writeFile"u8, RpcResponse.Json(writer =>
        {
            writer.WriteStartObject(); ApiJson.String(writer, "path"u8, path); ApiJson.String(writer, "data"u8, data); writer.WriteEndObject();
        }));
    }
    public void AppendFile(Utf8String path, ReadOnlySpan<byte> contents) => host.AppendFile(path, contents);
    public void Remove(Utf8String path) => host.Remove(path);
    public void SetTimes(Utf8String path, DateTime accessTimeUtc, DateTime writeTimeUtc) => host.SetTimes(path, accessTimeUtc, writeTimeUtc);
    public FileEntry? Stat(Utf8String path) => host.Stat(path);
}
