using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Ro3Emu.Launcher;

/// <summary>
/// Starts ro3.exe the way RO3Launcher.exe does (same as Ro3Emu.ClientTools' Launcher): RO3.ClientSecurity.LauncherProofGuard
/// kills the game (exit 70) unless RO3_LAUNCH_PIPE_HANDLE names an inherited anonymous pipe that delivers, within one
/// second, five CRLF lines: RO3_LAUNCH_PROOF_V1, the game's PID, issued-at and expiry unix seconds, 64 lowercase hex chars.
/// </summary>
public static class GameStarter
{
    private const string GpuPreferences = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>
    /// Windows' per-app graphics choice (Settings > Display > Graphics), "High performance" for ro3.exe: on laptops
    /// that also have integrated graphics the game ran there by default, where its text shaders failed (names over
    /// heads drawn as white blocks, dialogue text without its outline; players' reports 19.10, gone on the dedicated
    /// GPU). A choice the player already made for ro3.exe is left alone; a PC with one GPU is unaffected.
    /// </summary>
    internal static void PreferHighPerformanceGpu(string exe)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(GpuPreferences);
            if (key.GetValue(exe) == null) key.SetValue(exe, "GpuPreference=2;");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }

    /// <summary>Returns null when the game is running, or the reason it stopped within the first seconds.</summary>
    public static async Task<string?> StartAsync(string clientDir)
    {
        string exe = Path.Combine(clientDir, "ro3.exe");
        if (!File.Exists(exe)) return "ro3.exe not found";

        PreferHighPerformanceGpu(exe);
        using var pipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = clientDir };
        start.Environment["RO3_LAUNCH_PIPE_HANDLE"] = pipe.GetClientHandleAsString();
        using var game = Process.Start(start) ?? throw new InvalidOperationException("ro3.exe did not start");
        pipe.DisposeLocalCopyOfClientHandle();

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string proof = string.Join("\r\n", "RO3_LAUNCH_PROOF_V1", game.Id.ToString(), now.ToString(), (now + 30).ToString(),
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))) + "\r\n";
        byte[] bytes = Encoding.ASCII.GetBytes(proof);
        await pipe.WriteAsync(bytes);
        await pipe.FlushAsync();

        // Keep our end open while the game reads the proof (it gives itself one second).
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await game.WaitForExitAsync(wait.Token);
            return game.ExitCode == 70 ? "the game rejected the launch proof (exit 70)" : $"the game closed right away (exit {game.ExitCode})";
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
