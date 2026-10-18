namespace Ro3Emu.AiBots;

/// <summary>
/// The server's loopback GM tool (POST /ro3emu/gm?account=...), which sets up a bot's starting level and gear. It only
/// acts on bot accounts when the server lists them in Ro3Emu:GmToolAccounts; without that the bots simply level from 1.
/// </summary>
public sealed class GmTool(string server, Action<string> log)
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(server), Timeout = TimeSpan.FromSeconds(10) };
    private DateTime _offUntil;
    private bool _warned;

    public bool Available => DateTime.UtcNow >= _offUntil;

    public async Task<bool> RunAsync(string account, string line)
    {
        try
        {
            using var body = new StringContent(line);
            using var response = await _http.PostAsync($"/ro3emu/gm?account={Uri.EscapeDataString(account)}", body);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return false; }
    }

    /// <summary>The server refused: stop trying for half an hour (the hint is logged once).</summary>
    public void Refused()
    {
        _offUntil = DateTime.UtcNow.AddMinutes(30);
        if (_warned) return;
        _warned = true;
        log("the GM tool does not act on bot accounts, so bots level from 1 without gear. To let it, add " +
            "Environment=Ro3Emu__GmToolAccounts__0=aibot* to the server's unit and restart it (docs/guide/ai-bots.md)");
    }
}
