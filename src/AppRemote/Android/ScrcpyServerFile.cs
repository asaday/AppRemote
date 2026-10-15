using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace AppRemote.Android;

internal static class ScrcpyServerFile
{
    private const string Version = "5.0.1";
    private const string Sha256 = "764eb6f79811d5211fe9df341120882ba9994c7a61b897d7bf3fb662e53bc536";

    public static async Task<string> GetAsync()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppRemote");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "scrcpy-server-v" + Version);
        if (File.Exists(path) && await ValidAsync(path)) return path;
        var temporary = path + ".download";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            await using (var input = await client.GetStreamAsync(
                $"https://github.com/Genymobile/scrcpy/releases/download/v{Version}/scrcpy-server-v{Version}"))
            await using (var output = File.Create(temporary))
                await input.CopyToAsync(output);
            if (!await ValidAsync(temporary)) throw new IOException("scrcpy server checksum mismatch.");
            File.Move(temporary, path, true);
            return path;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<bool> ValidAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexStringLower(hash) == Sha256;
    }
}
