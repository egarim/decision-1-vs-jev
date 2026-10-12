using System.Text.Json;
using Decision1VsJev;

// dotnet run --project src/Decision1VsJev -- demo  [decision1|jev|openjev ...]
// dotnet run --project src/Decision1VsJev -- bench [decision1|jev|openjev ...]
var cmd = args.FirstOrDefault() ?? "help";
var engines = args.Skip(1).DefaultIfEmpty("decision1").ToArray();
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

switch (cmd)
{
    case "demo":
        foreach (var e in engines) await Demo(e, Create(e));
        break;

    case "bench":
        var tickets = File.ReadLines("data/tickets.jsonl")
            .Select(l => JsonSerializer.Deserialize<Ticket>(l, SystemOneClient.Json)!).ToList();
        Directory.CreateDirectory("results");
        var rows = new List<Summary>();
        foreach (var e in engines)
            rows.Add(await Bench.RunAsync(e, Create(e), http, tickets, Pace(e), $"results/{e}.jsonl"));
        Print(rows);
        break;

    default:
        Console.WriteLine("usage: demo|bench [decision1|jev|openjev ...]");
        break;
}

SystemOneClient Create(string engine) => engine switch
{
    "decision1" => Env("AZURE_FOUNDRY_TOKEN") is { } token
        ? SystemOneClient.FoundryEntra(http, Need("AZURE_FOUNDRY_ENDPOINT"), Deployment(), token)
        : SystemOneClient.Foundry(http, Need("AZURE_FOUNDRY_ENDPOINT"), Deployment(), Need("AZURE_FOUNDRY_KEY")),
    "jev" => SystemOneClient.Jev(http, Need("TYPESAFE_API_KEY")),
    "openjev" => SystemOneClient.OpenJev(http, Env("OPENJEV_URL") ?? "http://127.0.0.1:8000"),
    _ => throw new ArgumentException($"unknown engine '{engine}' (decision1 | jev | openjev)"),
};

// Hosted engines are rate-limited per minute (a capacity-60 Foundry deployment = 60 requests/min).
// ponytail: fixed pause between calls; a token bucket if you ever bench at higher capacity.
TimeSpan Pace(string engine) =>
    engine == "openjev" ? TimeSpan.Zero : TimeSpan.FromMilliseconds(int.Parse(Env("BENCH_PACE_MS") ?? "1100"));

string Deployment() => Env("DECISION_DEPLOYMENT") ?? "decision-1";
static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
static string Need(string name) => Env(name) ?? throw new InvalidOperationException($"set {name}");

// The example from Microsoft's docs: route + rate + check repeat contact, three questions, one request.
static async Task Demo(string engine, SystemOneClient client)
{
    var r = await client.AskAsync(
        new { message = "Checkout is down again. This is my third request.", customer_contact_count = 3 },
        new Dictionary<string, Question>
        {
            ["team"] = Ask.Choice("Which team should handle this request?", new Dictionary<string, string>
            {
                ["billing"] = "Charges, invoices, and refunds",
                ["engineering"] = "Bugs, errors, and outages",
                ["support"] = "How-to and account questions",
            }),
            ["severity"] = Ask.Score("How severe is this issue?", "Cosmetic", "Minor", "Major", "Critical"),
            ["repeat_contact"] = Ask.Noul("Has the customer contacted support before?"),
        });

    var team = r.Answers["team"];
    Console.WriteLine($"[{engine}] model={r.Model} tokens in={r.Usage.InputTokens} out={r.Usage.OutputTokens}");
    Console.WriteLine($"  team           = {team.Choice} (p={team.TopProbability:F3})");
    Console.WriteLine($"  severity       = {r.Answers["severity"].Score:F2} on 0..3");
    Console.WriteLine($"  repeat_contact = {r.Answers["repeat_contact"].Noul:F3}");
}

static void Print(List<Summary> rows)
{
    string Col(Func<Summary, string> f) => string.Join(" | ", rows.Select(f));
    Console.WriteLine($"| | {Col(s => $"**{s.Engine}** ({s.Model})")} |");
    Console.WriteLine($"|---|{string.Concat(rows.Select(_ => "---|"))}");
    void Row(string name, Func<Summary, string> f) => Console.WriteLine($"| {name} | {Col(f)} |");
    Row("tickets", s => s.N.ToString());
    Row("team accuracy", s => $"{s.TeamAccuracy:P1}");
    Row("team Brier (lower = better calibrated)", s => $"{s.TeamBrier:F3}");
    Row("answered at p ≥ 0.8 (coverage)", s => $"{s.Coverage80:P0}");
    Row("accuracy when p ≥ 0.8", s => $"{s.AccuracyAt80:P1}");
    Row("severity MAE (0..3 scale)", s => $"{s.SeverityMae:F2}");
    Row("churn accuracy (p ≥ 0.5)", s => $"{s.ChurnAccuracy:P1}");
    Row("churn Brier", s => $"{s.ChurnBrier:F3}");
    Row("answer flips when options reversed", s => $"{s.OrderFlips} / {s.N}");
    Row("latency 1 question p50 / p95", s => $"{s.Ms1P50:F0} / {s.Ms1P95:F0} ms");
    Row("latency 3 questions p50 / p95", s => $"{s.Ms3P50:F0} / {s.Ms3P95:F0} ms");
    Row("network floor (GET /, p50)", s => $"{s.FloorMs:F0} ms");
    Row("input tokens per 3-question call (p50)", s => s.InputTokensP50.ToString());
    Row("429 retries", s => s.Retries.ToString());
}
