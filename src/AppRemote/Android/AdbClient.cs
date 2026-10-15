using System.Diagnostics;
using System.IO;

namespace AppRemote.Android;

internal sealed record AndroidDevice(string Serial, string State, string Description)
{
    public override string ToString() => $"{Description} ({Serial}) — {State}";
}

internal static class AdbClient
{
    public static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var local = Path.Combine(AppContext.BaseDirectory, "adb.exe");
        if (File.Exists(local)) return local;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            try
            {
                var path = Path.Combine(directory, "adb.exe");
                if (File.Exists(path)) return path;
            }
            catch (Exception) { }
        }
        var sdk = Environment.GetEnvironmentVariable("ANDROID_HOME") ??
                  Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ??
                  Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");
        var platformTools = Path.Combine(sdk, "platform-tools", "adb.exe");
        return File.Exists(platformTools) ? platformTools : null;
    }

    public static async Task<IReadOnlyList<AndroidDevice>> DevicesAsync(string adb)
    {
        var output = await RunAsync(adb, null, "devices", "-l");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1).Select(line => line.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2)
            .Select(parts =>
            {
                var model = parts.FirstOrDefault(part => part.StartsWith("model:", StringComparison.Ordinal));
                return new AndroidDevice(parts[0], parts[1], model is null ? parts[0] : model[6..].Replace('_', ' '));
            }).ToArray();
    }

    public static Task<string> RunAsync(string adb, string? serial, params string[] args)
        => RunAsync(adb, serial, CancellationToken.None, args);

    public static async Task<string> RunAsync(string adb, string? serial, CancellationToken stop, params string[] args)
    {
        var info = new ProcessStartInfo(adb) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        if (serial is not null) { info.ArgumentList.Add("-s"); info.ArgumentList.Add(serial); }
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not start ADB.");
        var stdout = process.StandardOutput.ReadToEndAsync(stop);
        var stderr = process.StandardError.ReadToEndAsync(stop);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            throw new IOException("ADB timed out.");
        }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new IOException(error.Trim().Length > 0 ? error.Trim() : $"ADB exited with code {process.ExitCode}.");
        return output.Trim();
    }
}
