using System.Diagnostics;
using System.Text.Json;

namespace Decision1VsJev;

public sealed record Ticket(int Id, string Text, string Team, int Severity, bool Churn);

public sealed record Summary(
    string Engine, string Model, int N,
    double TeamAccuracy, double TeamBrier, double Coverage80, double AccuracyAt80,
    double SeverityMae, double ChurnAccuracy, double ChurnBrier,
    int OrderFlips,
    double Ms1P50, double Ms1P95, double Ms3P50, double Ms3P95, double FloorMs,
    int InputTokensP50, int Retries);

/// <summary>
/// The Microsoft quickstart (route a support ticket with one choice question), grown into an
/// engine-neutral benchmark. Per ticket, three calls through the SAME contract:
///   1. team + severity + churn in one request  → accuracy, calibration, 3-question latency
///   2. team alone                              → 1-question latency
///   3. team alone, options in reverse order    → does the answer flip? (Microsoft claims zero)
/// </summary>
public static class Bench
{
    public static readonly Dictionary<string, string> Teams = new()
    {
        ["billing"] = "Charges, invoices, refunds, taxes, or payment methods",
        ["technical"] = "Software errors, bugs, outages, or integration failures",
        ["account"] = "Sign-in, passwords, user management, or account access",
        ["sales"] = "Pricing, quotes, demos, upgrades, or new purchases",
    };

    public static readonly string[] SeverityLevels =
    [
        "Question, request, or cosmetic issue",
        "Minor: an inconvenience with a workaround",
        "Major: blocks this customer's work or costs them money",
        "Critical: outage, data loss, or security breach",
    ];

    const string TeamInstructions =
        "Which team should handle this customer support request? Select exactly one team based on the primary problem.";

    static Question Team(IReadOnlyDictionary<string, string> options) => Ask.Choice(TeamInstructions, options);

    public static readonly Dictionary<string, Question> AllThree = new()
    {
        ["team"] = Team(Teams),
        ["severity"] = Ask.Score("How severe is this issue for the customer?", SeverityLevels),
        ["churn"] = Ask.Noul("Is the customer threatening to cancel, leave, or switch to a competitor?"),
    };

    public static async Task<Summary> RunAsync(
        string engine, SystemOneClient client, HttpClient http, IReadOnlyList<Ticket> tickets,
        TimeSpan pace, string rawPath, CancellationToken ct = default)
    {
        var teamOnly = new Dictionary<string, Question> { ["team"] = Team(Teams) };
        var reversed = new Dictionary<string, Question> { ["team"] = Team(Teams.Reverse().ToDictionary()) };

        var floor = await NetworkFloorAsync(http, client.Endpoint, ct);
        await client.AskAsync("warm-up", teamOnly, ct);   // first call pays TLS + model load; don't count it

        var ms1 = new List<double>();
        var ms3 = new List<double>();
        var tokens = new List<int>();
        int teamOk = 0, churnOk = 0, flips = 0, confident = 0, confidentOk = 0;
        double teamBrier = 0, churnBrier = 0, sevErr = 0;
        string model = "";

        await using var raw = new StreamWriter(rawPath);
        foreach (var t in tickets)
        {
            var (all, t3) = await TimedAsync(() => client.AskAsync(t.Text, AllThree, ct), pace, ct);
            var (one, t1) = await TimedAsync(() => client.AskAsync(t.Text, teamOnly, ct), pace, ct);
            var (rev, _) = await TimedAsync(() => client.AskAsync(t.Text, reversed, ct), pace, ct);

            model = all.Model;
            var team = all.Answers["team"];
            var sev = all.Answers["severity"];
            var churn = all.Answers["churn"];

            ms1.Add(t1);
            ms3.Add(t3);
            tokens.Add(all.Usage.InputTokens);
            teamOk += team.Choice == t.Team ? 1 : 0;
            teamBrier += Teams.Keys.Sum(k => Sq(P(team, k) - (k == t.Team ? 1 : 0)));
            if (team.TopProbability >= 0.8) { confident++; confidentOk += team.Choice == t.Team ? 1 : 0; }
            sevErr += Math.Abs((sev.Score ?? 0) - t.Severity);
            var pChurn = churn.Noul ?? 0;
            churnOk += (pChurn >= 0.5) == t.Churn ? 1 : 0;
            churnBrier += Sq(pChurn - (t.Churn ? 1 : 0));
            flips += one.Answers["team"].Choice != rev.Answers["team"].Choice ? 1 : 0;

            await raw.WriteLineAsync(JsonSerializer.Serialize(new
            {
                engine, t.Id, t.Text, label = new { t.Team, t.Severity, t.Churn },
                answers = all.Answers, reversed_choice = rev.Answers["team"].Choice,
                ms_1q = Math.Round(t1, 1), ms_3q = Math.Round(t3, 1), all.Usage,
            }, SystemOneClient.Json));

            Console.Error.Write($"\r{engine}: {t.Id}/{tickets.Count}  ");
        }
        Console.Error.WriteLine();

        int n = tickets.Count;
        return new Summary(
            engine, model, n,
            (double)teamOk / n, teamBrier / n, (double)confident / n, confident == 0 ? 0 : (double)confidentOk / confident,
            sevErr / n, (double)churnOk / n, churnBrier / n,
            flips,
            Pct(ms1, 50), Pct(ms1, 95), Pct(ms3, 50), Pct(ms3, 95), floor,
            (int)Pct(tokens.Select(x => (double)x).ToList(), 50), client.Retries);
    }

    static double P(Answer a, string option) => a.Probabilities?.GetValueOrDefault(option) ?? 0;
    static double Sq(double x) => x * x;

    /// <summary>Nearest-rank percentile.</summary>
    public static double Pct(List<double> xs, int p)
    {
        var s = xs.Order().ToList();
        return s[Math.Clamp((int)Math.Ceiling(p / 100.0 * s.Count) - 1, 0, s.Count - 1)];
    }

    static async Task<(T, double)> TimedAsync<T>(Func<Task<T>> call, TimeSpan pace, CancellationToken ct)
    {
        if (pace > TimeSpan.Zero) await Task.Delay(pace, ct);   // stay under the deployment's RPM; not timed
        var sw = Stopwatch.StartNew();
        var r = await call();
        return (r, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Median of five GET / against the same host: what the network costs before any model runs.
    /// Subtract it from a hosted engine's latency before comparing with a local one.
    /// </summary>
    static async Task<double> NetworkFloorAsync(HttpClient http, Uri endpoint, CancellationToken ct)
    {
        var root = new Uri(endpoint, "/");
        var times = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            var sw = Stopwatch.StartNew();
            using var _ = await http.GetAsync(root, ct);
            if (i > 0) times.Add(sw.Elapsed.TotalMilliseconds);   // i == 0 pays DNS + TLS
        }
        return Pct(times, 50);
    }
}
