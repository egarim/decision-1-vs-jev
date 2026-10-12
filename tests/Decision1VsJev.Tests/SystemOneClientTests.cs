using System.Net;
using System.Text;
using System.Text.Json;
using Decision1VsJev;

namespace Decision1VsJev.Tests;

/// <summary>Hermetic: no network, no key. The fixture is a real Microsoft-Decision-1 response (2026-10-12).</summary>
public class SystemOneClientTests
{
    const string MicrosoftResponse = """
    {"model":"microsoft-decision-1","answers":{"repeat_contact":{"noul":0.9947798728263116,"type":"noul"},"severity":{"confidence":0.7157642749119476,"legend":{"0":"Cosmetic","1":"Minor","2":"Major","3":"Critical"},"probabilities":{"0":0.0015189260675902497,"1":0.012717812255977427,"2":0.7868232061839607,"3":0.1989400554924716},"score":2.183184391101314,"type":"score"},"team":{"choice":"engineering","confidence":0.9863012095179742,"probabilities":{"billing":0.0024561147888571516,"engineering":0.9908674730119829,"support":0.006676412199159919},"type":"choice"}},"usage":{"input_tokens":117,"output_tokens":3}}
    """;

    static readonly Dictionary<string, Question> Questions = new()
    {
        ["team"] = Ask.Choice("Which team?", new Dictionary<string, string> { ["billing"] = "Money", ["engineering"] = "Bugs" }),
        ["severity"] = Ask.Score("How severe?", "Cosmetic", "Minor", "Major", "Critical"),
        ["repeat_contact"] = Ask.Noul("Contacted before?"),
    };

    [Fact]
    public async Task Parses_all_three_answer_types_from_a_real_Microsoft_response()
    {
        var stub = new StubHandler((HttpStatusCode.OK, MicrosoftResponse));
        var client = SystemOneClient.Foundry(new HttpClient(stub), "https://eastus.api.cognitive.microsoft.com/", "decision-1", "k");

        var r = await client.AskAsync("Checkout is down again.", Questions);

        Assert.Equal("microsoft-decision-1", r.Model);
        Assert.Equal("engineering", r.Answers["team"].Choice);
        Assert.Equal(0.9909, r.Answers["team"].TopProbability, 4);
        Assert.Equal(2.1832, r.Answers["severity"].Score!.Value, 4);
        Assert.Equal(0.9948, r.Answers["repeat_contact"].Noul!.Value, 4);
        Assert.Equal(117, r.Usage.InputTokens);
    }

    [Fact]
    public async Task Foundry_sends_the_deployment_name_and_api_key_to_the_microsoft_provider_path()
    {
        var stub = new StubHandler((HttpStatusCode.OK, MicrosoftResponse));
        var client = SystemOneClient.Foundry(new HttpClient(stub), "https://eastus.api.cognitive.microsoft.com/", "decision-1", "secret");

        await client.AskAsync(new { message = "hi", customer_contact_count = 3 }, Questions);

        var (req, body) = stub.Sent.Single();
        Assert.Equal("https://eastus.api.cognitive.microsoft.com/providers/microsoft/v1/systemone", req.RequestUri!.ToString());
        Assert.Equal("secret", req.Headers.GetValues("api-key").Single());
        Assert.NotNull(req.Content!.Headers.ContentLength);   // Foundry 400s a chunked body
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("decision-1", root.GetProperty("model").GetString());
        Assert.Equal(3, root.GetProperty("state").GetProperty("customer_contact_count").GetInt32());
        var team = root.GetProperty("questions").GetProperty("team");
        Assert.Equal("choice", team.GetProperty("type").GetString());
        Assert.Equal("Bugs", team.GetProperty("criteria").GetProperty("engineering").GetString());   // option keys untouched
        Assert.Equal(4, root.GetProperty("questions").GetProperty("severity").GetProperty("criteria").GetArrayLength());
        Assert.False(root.GetProperty("questions").GetProperty("repeat_contact").TryGetProperty("criteria", out _));
    }

    [Fact]
    public async Task Jev_sends_a_bearer_token_to_typesafe()
    {
        var stub = new StubHandler((HttpStatusCode.OK, MicrosoftResponse));
        var client = SystemOneClient.Jev(new HttpClient(stub), "tk");

        await client.AskAsync("hi", Questions);

        var (req, body) = stub.Sent.Single();
        Assert.Equal("https://api.typesafe.ai/v1/systemone", req.RequestUri!.ToString());
        Assert.Equal("Bearer tk", req.Headers.Authorization!.ToString());
        Assert.Contains("\"model\":\"jev-latest\"", body);
    }

    [Fact]
    public async Task Retries_a_429_then_counts_it_and_returns_the_answer()
    {
        var stub = new StubHandler((HttpStatusCode.TooManyRequests, "{}"), (HttpStatusCode.OK, MicrosoftResponse));
        var client = SystemOneClient.OpenJev(new HttpClient(stub));

        var r = await client.AskAsync("hi", Questions);

        Assert.Equal("engineering", r.Answers["team"].Choice);
        Assert.Equal(1, client.Retries);
        Assert.Equal(2, stub.Sent.Count);
    }

    [Fact]
    public async Task A_rejected_request_throws_with_the_status()
    {
        var stub = new StubHandler((HttpStatusCode.UnprocessableEntity, "{\"detail\":\"bad question\"}"));
        var client = SystemOneClient.OpenJev(new HttpClient(stub));

        var ex = await Assert.ThrowsAsync<SystemOneException>(() => client.AskAsync("hi", Questions));
        Assert.Equal(422, ex.Status);
    }

    [Fact]
    public void Percentile_is_nearest_rank()
    {
        var xs = Enumerable.Range(1, 20).Select(i => (double)i).ToList();
        Assert.Equal(10, Bench.Pct(xs, 50));
        Assert.Equal(19, Bench.Pct(xs, 95));
        Assert.Equal(7, Bench.Pct([7], 95));
    }
}

/// <summary>Returns canned responses in order (429s carry Foundry's retry-after-ms: 1) and records what was sent.</summary>
sealed class StubHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
{
    int _next;
    public List<(HttpRequestMessage Request, string Body)> Sent { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Sent.Add((request, await request.Content!.ReadAsStringAsync(ct)));
        var (status, body) = responses[Math.Min(_next++, responses.Length - 1)];
        var resp = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (status == HttpStatusCode.TooManyRequests) resp.Headers.Add("retry-after-ms", "1");
        return resp;
    }
}
