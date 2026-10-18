using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace Ro3Emu.AiBots;

/// <summary>
/// The bots' language model (Claude Messages API) for chat replies, shared by all bots: a daily call cap, a few calls
/// at a time, and null instead of an exception whenever it cannot answer (the caller then uses a canned line).
/// </summary>
public sealed class ChatModel
{
    private readonly LlmOptions _options;
    private readonly AnthropicClient? _client;
    private readonly SemaphoreSlim _parallel = new(3);
    private readonly object _sync = new();
    private DateOnly _day;
    private int _calls;

    public ChatModel(LlmOptions options, string configDirectory, Action<string> log)
    {
        _options = options;
        if (!options.Enabled) { log("chat model off (llm.enabled=false): canned lines only"); return; }
        string? key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        string keyFile = Path.Combine(configDirectory, options.ApiKeyFile);
        if (string.IsNullOrWhiteSpace(key) && File.Exists(keyFile)) key = File.ReadAllText(keyFile).Trim();
        if (string.IsNullOrWhiteSpace(key)) { log($"no API key ({keyFile} missing): canned lines only"); return; }
        _client = new AnthropicClient { ApiKey = key, Timeout = TimeSpan.FromSeconds(30), MaxRetries = 1 };
        log($"chat model {options.Model}, at most {options.MaxCallsPerDay} calls a day");
    }

    public bool Enabled => _client != null;

    public int MaxReplyChars => _options.MaxReplyChars;

    public int CallsToday { get { lock (_sync) return _day == Today ? _calls : 0; } }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private bool Reserve()
    {
        lock (_sync)
        {
            if (_day != Today) { _day = Today; _calls = 0; }
            if (_calls >= _options.MaxCallsPerDay) return false;
            _calls++;
            return true;
        }
    }

    /// <summary>One short in-character line, or null (no key, over the cap, busy, refused or failed).</summary>
    public async Task<string?> ReplyAsync(string system, string conversation, Action<string> log, CancellationToken ct)
    {
        if (_client == null || !Reserve()) return null;
        if (!await _parallel.WaitAsync(TimeSpan.FromSeconds(5), ct)) return null;
        try
        {
            var request = new MessageCreateParams
            {
                Model = _options.Model,
                MaxTokens = 1024,
                OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
                System = system,
                Messages = [new() { Role = Role.User, Content = conversation }],
            };
            // Opus/Sonnet/Fable: let the API re-serve a safety decline on a fallback model instead of stopping.
            if (!_options.Model.Contains("haiku", StringComparison.OrdinalIgnoreCase))
            {
                request = request with { Betas = ["server-side-fallback-2026-07-01"], Fallbacks = new Default() };
            }
            var response = await _client.Beta.Messages.Create(request, ct);
            if (response.StopReason == "refusal") return null;
            string text = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log($"chat model call failed: {e.GetType().Name}: {e.Message}");
            return null;
        }
        finally { _parallel.Release(); }
    }
}
