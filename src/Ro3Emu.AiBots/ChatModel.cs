using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace Ro3Emu.AiBots;

/// <summary>
/// The bots' language model for chat replies, through OpenRouter or the Claude API, shared by all bots: a daily call
/// cap, a few calls at a time, and null instead of an exception whenever it cannot answer (the caller then uses a
/// canned line).
/// </summary>
public sealed class ChatModel
{
    private readonly LlmOptions _options;
    private readonly bool _openRouter;
    private readonly string _model;
    private readonly AnthropicClient? _anthropic;
    private readonly HttpClient? _http;
    private readonly SemaphoreSlim _parallel = new(3);
    private readonly object _sync = new();
    private DateOnly _day;
    private int _calls;

    public ChatModel(LlmOptions options, string configDirectory, Action<string> log)
    {
        _options = options;
        _openRouter = !string.Equals(options.Provider, "anthropic", StringComparison.OrdinalIgnoreCase);
        _model = options.Model.Length > 0 ? options.Model : _openRouter ? "anthropic/claude-opus-5.5" : "claude-opus-5-5";
        if (!options.Enabled) { log("chat model off (llm.enabled=false): canned lines only"); return; }
        string? key = Environment.GetEnvironmentVariable(_openRouter ? "OPENROUTER_API_KEY" : "ANTHROPIC_API_KEY");
        string keyFile = Path.Combine(configDirectory, options.ApiKeyFile.Length > 0 ? options.ApiKeyFile
            : _openRouter ? "openrouter.key" : "anthropic.key");
        if (string.IsNullOrWhiteSpace(key) && File.Exists(keyFile)) key = File.ReadAllText(keyFile).Trim();
        if (string.IsNullOrWhiteSpace(key)) { log($"no API key ({keyFile} missing): canned lines only"); return; }
        if (_openRouter)
        {
            _http = new HttpClient { BaseAddress = new Uri(options.OpenRouterUrl), Timeout = TimeSpan.FromSeconds(30) };
            _http.DefaultRequestHeaders.Authorization = new("Bearer", key);
            _http.DefaultRequestHeaders.Add("X-Title", "Sol RO3 AI players");
        }
        else _anthropic = new AnthropicClient { ApiKey = key, Timeout = TimeSpan.FromSeconds(30), MaxRetries = 1 };
        log($"chat model {_model} via {(_openRouter ? "OpenRouter" : "the Claude API")}, at most {options.MaxCallsPerDay} calls a day");
    }

    public bool Enabled => _anthropic != null || _http != null;

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
        if (!Enabled || !Reserve()) return null;
        if (!await _parallel.WaitAsync(TimeSpan.FromSeconds(5), ct)) return null;
        try
        {
            string? text = _openRouter ? await OpenRouterAsync(system, conversation, log, ct) : await AnthropicAsync(system, conversation, ct);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log($"chat model call failed: {e.GetType().Name}: {e.Message}");
            return null;
        }
        finally { _parallel.Release(); }
    }

    private async Task<string?> AnthropicAsync(string system, string conversation, CancellationToken ct)
    {
        var request = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = 1024,
            OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
            System = system,
            Messages = [new() { Role = Role.User, Content = conversation }],
        };
        // Opus/Sonnet/Fable: let the API re-serve a safety decline on a fallback model instead of stopping.
        if (!_model.Contains("haiku", StringComparison.OrdinalIgnoreCase))
            request = request with { Betas = ["server-side-fallback-2026-07-01"], Fallbacks = new Default() };
        var response = await _anthropic!.Beta.Messages.Create(request, ct);
        if (response.StopReason == "refusal") return null;
        return string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
    }

    /// <summary>OpenRouter's OpenAI-style chat completions (POST chat/completions).</summary>
    private async Task<string?> OpenRouterAsync(string system, string conversation, Action<string> log, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 1024,
            ["reasoning"] = new JsonObject { ["effort"] = "low" },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = conversation },
            },
        };
        using var content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var response = await _http!.PostAsync("chat/completions", content, ct);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        if (!response.IsSuccessStatusCode || json.TryGetProperty("error", out _))
        {
            string error = json.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            log($"chat model call failed: HTTP {(int)response.StatusCode} {error}");
            return null;
        }
        if (!json.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return null;
        var choice = choices[0];
        if (choice.TryGetProperty("finish_reason", out var finish) && finish.GetString() is "content_filter" or "refusal") return null;
        return choice.GetProperty("message").TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString() : null;
    }
}
