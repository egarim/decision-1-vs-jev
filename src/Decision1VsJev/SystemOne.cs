using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Decision1VsJev;

/// <summary>
/// One typed question. <c>criteria</c> is a {name: description} map for choice, an ordered list
/// for score, and an optional {"true": ..., "false": ...} map for noul. Build with <see cref="Ask"/>.
/// </summary>
public sealed record Question(string Type, string Instructions, object? Criteria = null);

public static class Ask
{
    public static Question Choice(string instructions, IReadOnlyDictionary<string, string> options) =>
        new("choice", instructions, options);

    /// <param name="levels">Lowest to highest. The answer's score is the probability-weighted level index.</param>
    public static Question Score(string instructions, params string[] levels) =>
        new("score", instructions, levels);

    public static Question Noul(string instructions, string? whenTrue = null, string? whenFalse = null)
    {
        var criteria = new Dictionary<string, string>();
        if (whenTrue is not null) criteria["true"] = whenTrue;
        if (whenFalse is not null) criteria["false"] = whenFalse;
        return new("noul", instructions, criteria.Count == 0 ? null : criteria);
    }
}

/// <summary>
/// One answer. Which fields are set depends on <see cref="Type"/>: choice → Choice + Probabilities,
/// score → Score + Probabilities (keyed "0".."n"), noul → Noul (probability of true).
/// ponytail: one flat record instead of a polymorphic hierarchy — the servers put "type" last, which
/// System.Text.Json's discriminator handling dislikes, and three nullable fields are all it needs.
/// </summary>
public sealed record Answer(
    string Type,
    string? Choice,
    double? Score,
    double? Noul,
    double? Confidence,
    Dictionary<string, double>? Probabilities)
{
    public double TopProbability => Probabilities is { Count: > 0 } p ? p.Values.Max() : Noul ?? 0;
}

public sealed record Usage(int InputTokens, int OutputTokens);

public sealed record SystemOneResponse(string Model, Dictionary<string, Answer> Answers, Usage Usage);

public sealed class SystemOneException(int status, string body)
    : Exception($"System One API returned {status}: {body}")
{
    public int Status { get; } = status;
}

/// <summary>
/// A client for the System One contract: <c>POST .../v1/systemone</c> with
/// <c>{model, state, questions}</c>. TypeSafe's Jev defined it; Microsoft-Decision-1 and open-jev
/// serve the same body and the same answers. Only the URL, the auth header and the meaning of
/// <c>model</c> differ, so those are the only things the factories set.
/// </summary>
public sealed class SystemOneClient(HttpClient http, Uri endpoint, string model, string authHeader, string authValue)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,   // input_tokens, output_tokens
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const int MaxAttempts = 5;

    public Uri Endpoint { get; } = endpoint;

    /// <summary>429s absorbed so far. A benchmark should report it: a retried call's latency is not the model's.</summary>
    public int Retries { get; private set; }

    /// <summary>Microsoft Foundry. <paramref name="deployment"/> is YOUR deployment name, not "Microsoft-Decision-1".</summary>
    public static SystemOneClient Foundry(HttpClient http, string resourceEndpoint, string deployment, string apiKey) =>
        new(http, new Uri(resourceEndpoint.TrimEnd('/') + "/providers/microsoft/v1/systemone"), deployment, "api-key", apiKey);

    /// <summary>Foundry with a Microsoft Entra token (needs a resource with a custom subdomain).</summary>
    public static SystemOneClient FoundryEntra(HttpClient http, string resourceEndpoint, string deployment, string bearerToken) =>
        new(http, new Uri(resourceEndpoint.TrimEnd('/') + "/providers/microsoft/v1/systemone"), deployment, "Authorization", "Bearer " + bearerToken);

    /// <summary>TypeSafe's hosted Jev.</summary>
    public static SystemOneClient Jev(HttpClient http, string apiKey, string model = "jev-latest") =>
        new(http, new Uri("https://api.typesafe.ai/v1/systemone"), model, "Authorization", "Bearer " + apiKey);

    /// <summary>daseinlabs/open-jev on your own Mac (MLX). Auth is only enforced if OPENJEV_API_KEY is set on the server.</summary>
    public static SystemOneClient OpenJev(HttpClient http, string baseUrl = "http://127.0.0.1:8000", string apiKey = "local") =>
        new(http, new Uri(baseUrl.TrimEnd('/') + "/v1/systemone"), "open-jev", "Authorization", "Bearer " + apiKey);

    /// <param name="state">Text, or any object (serialized as JSON).</param>
    public async Task<SystemOneResponse> AskAsync(
        object state, IReadOnlyDictionary<string, Question> questions, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            // StringContent, not JsonContent: JsonContent streams chunked, and the Foundry gateway
            // rejects any body without Content-Length (400 no_content_length_header).
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { model, state, questions }, Json), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation(authHeader, authValue);

            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxAttempts)
            {
                Retries++;
                // Foundry says how long in a non-standard retry-after-ms header; the others use Retry-After.
                var wait = resp.Headers.TryGetValues("retry-after-ms", out var ms) && double.TryParse(ms.First(), out var m)
                    ? TimeSpan.FromMilliseconds(m)
                    : resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1 << attempt);
                await Task.Delay(wait, ct).ConfigureAwait(false);
                continue;
            }
            if (!resp.IsSuccessStatusCode)
                throw new SystemOneException((int)resp.StatusCode, body);

            return JsonSerializer.Deserialize<SystemOneResponse>(body, Json)
                ?? throw new SystemOneException((int)resp.StatusCode, "empty body");
        }
    }
}
