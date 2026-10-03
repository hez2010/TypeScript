$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

public static class CompilerMetrics {
    [StructLayout(LayoutKind.Sequential)] private struct Memory {
        public uint Size, Faults;
        public UIntPtr PeakWorking, Working, PeakPaged, Paged, PeakNonPaged, NonPaged, Pagefile, PeakPagefile, Private;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Size, Tick; }
    [StructLayout(LayoutKind.Sequential)] private struct Io { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool GetProcessMemoryInfo(IntPtr process, ref Memory memory, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessIoCounters(IntPtr process, out Io counters);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetLastInputInfo(ref Input input);
    public static uint LastInput() { var value = new Input { Size = 8 }; if (!GetLastInputInfo(ref value)) throw new Win32Exception(); return value.Tick; }
    private static object Observe(Process process) {
        var memory = new Memory { Size = (uint)Marshal.SizeOf<Memory>() };
        if (!GetProcessMemoryInfo(process.Handle, ref memory, memory.Size) || !GetProcessIoCounters(process.Handle, out Io io)) throw new Win32Exception();
        return new { pid = process.Id, cpuMs = process.TotalProcessorTime.TotalMilliseconds, peakRssBytes = memory.PeakWorking.ToUInt64(),
            rssBytes = memory.Working.ToUInt64(), privateBytes = memory.Private.ToUInt64(), peakCommitBytes = memory.PeakPagefile.ToUInt64(),
            ioReadBytes = io.ReadBytes, ioWriteBytes = io.WriteBytes, ioReadOperations = io.ReadOperations, ioWriteOperations = io.WriteOperations };
    }
    public static object Observe(int id) { using (var process = Process.GetProcessById(id)) return Observe(process); }
    public static object Run(string executable, string[] arguments, string directory, string input, Dictionary<string, string> environment) {
        using (var process = new Process()) {
            var options = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
            foreach (var argument in arguments) options.ArgumentList.Add(argument);
            foreach (var pair in environment) options.Environment[pair.Key] = pair.Value;
            process.StartInfo = options;
            uint inputBefore = LastInput();
            var timer = Stopwatch.StartNew();
            process.Start(); var handle = process.Handle;
            double? firstOutput = null;
            var stdout = Read(process.StandardOutput, () => firstOutput = timer.Elapsed.TotalMilliseconds);
            var stderr = process.StandardError.ReadToEndAsync();
            if (input != null) process.StandardInput.Write(input);
            process.StandardInput.Close();
            if (!process.WaitForExit(120000)) { process.Kill(true); process.WaitForExit(); throw new TimeoutException("Compiler process exceeded two minutes"); }
            timer.Stop();
            var result = new { exitCode = process.ExitCode, elapsedMs = timer.Elapsed.TotalMilliseconds, firstOutputMs = firstOutput,
                stdout = stdout.GetAwaiter().GetResult(), stderr = stderr.GetAwaiter().GetResult(),
                inputBefore, inputAfter = LastInput(), metrics = Observe(process) };
            GC.KeepAlive(handle); return result;
        }
    }
    private static async Task<string> Read(StreamReader reader, Action first) {
        var text = new StringBuilder(); var buffer = new char[4096]; bool received = false;
        int count;
        while ((count = await reader.ReadAsync(buffer, 0, buffer.Length)) != 0) {
            if (!received) { first(); received = true; } text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
'@
[Console]::WriteLine('{"ready":true}')
while ($null -ne ($line = [Console]::ReadLine())) {
    try {
        $request = $line | ConvertFrom-Json
        switch ($request.kind) {
            'run' {
                $environment = [Collections.Generic.Dictionary[string,string]]::new()
                if ($null -ne $request.environment) { foreach ($property in $request.environment.PSObject.Properties) { $environment.Add($property.Name, [string]$property.Value) } }
                $result = [CompilerMetrics]::Run($request.executable, [string[]]$request.arguments, $request.directory, $request.input, $environment)
            }
            'observe' { $result = [CompilerMetrics]::Observe([int]$request.processId) }
            'activity' {
                $processes = @(Get-Process | Where-Object { $null -ne $_.CPU } | Select-Object Id,ProcessName,CPU)
                $result = @{ lastInputTick = [CompilerMetrics]::LastInput(); tick = [Environment]::TickCount64; processes = $processes }
            }
            'idle' { $result = @{ lastInputTick = [CompilerMetrics]::LastInput(); tick = [Environment]::TickCount64 } }
            default { throw "Unknown metrics operation: $($request.kind)" }
        }
        [Console]::WriteLine(($result | ConvertTo-Json -Depth 8 -Compress))
    }
    catch { [Console]::WriteLine((@{ error = $_.Exception.ToString() } | ConvertTo-Json -Compress)) }
}
