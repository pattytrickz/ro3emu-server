using System.Diagnostics;

namespace Ro3Emu.Patcher;

/// <summary>
/// Replaces the running launcher exe: Windows lets a running exe be renamed, so the old one moves aside to
/// <c>*.old</c>, the new one takes its name, and <c>*.old</c> is deleted on the next start.
/// </summary>
public static class SelfUpdate
{
    public static void DeleteLeftovers(string exePath)
    {
        try
        {
            string old = exePath + ".old";
            if (File.Exists(old)) File.Delete(old);
            string partial = exePath + ".new";
            if (File.Exists(partial)) File.Delete(partial);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static bool IsNewer(string remote, string current)
        => Version.TryParse(remote, out var r) && Version.TryParse(current, out var c) && r > c;

    /// <summary>
    /// Downloads and swaps in the new exe; returns true when the caller should start it and exit. The download streams
    /// to <c>*.new</c> and resumes after a break (<see cref="RepositoryClient.DownloadFileAsync"/>), with no fixed time
    /// limit: 1.2.3 gave up after 60 s, which a 64 MB launcher on a slow connection never made.
    /// <paramref name="progress"/> gets (bytes so far, total or -1).
    /// </summary>
    public static async Task<bool> ApplyAsync(RepositoryClient repo, LauncherRelease release, string currentVersion, string exePath,
        CancellationToken ct, Action<long, long>? progress = null)
    {
        if (!IsNewer(release.Version, currentVersion) || string.IsNullOrEmpty(release.Url))
        {
            return false;
        }
        PathRules.ValidateRelative(release.Url);
        string staged = exePath + ".new";
        if (File.Exists(staged)) File.Delete(staged);
        try
        {
            await repo.DownloadFileAsync(release.Url, staged, progress, ct);
        }
        catch (HttpRequestException e) when (e.StatusCode is { } status && (int)status is >= 400 and < 500)
        {
            File.Delete(staged);
            return false; // the release isn't there (404): keep the working exe
        }
        catch
        {
            File.Delete(staged);
            throw;
        }
        if (!string.Equals(Hashing.Sha256File(staged, ct: ct), release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(staged);
            return false; // a broken launcher release must never replace a working one
        }
        string old = exePath + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exePath, old);
        try
        {
            File.Move(staged, exePath);
        }
        catch
        {
            File.Move(old, exePath);
            throw;
        }
        return true;
    }

    public static void Restart(string exePath, string arguments = "")
    {
        Process.Start(new ProcessStartInfo(exePath, arguments) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exePath)! });
    }
}
