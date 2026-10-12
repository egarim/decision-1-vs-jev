# Microsoft-Decision-1 vs Jev

Microsoft shipped **[Microsoft-Decision-1](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/use-foundry-models-microsoft-decision)**
in Foundry on 2026-10-09: a "System One" model that doesn't write text, it **picks**. You give it
state plus typed questions (`noul` = is this true?, `choice` = which one?, `score` = how much?) and
it returns probabilities. That's exactly what TypeSafe's **Jev** does, and it's not a coincidence
of style: **it's the same API.** Same `POST .../v1/systemone`, same request body, same answer
fields, same price ($0.042 per million input tokens, output free).

This repo is the Microsoft quickstart ported to C#, plus a benchmark that runs the same labeled
tickets through Microsoft-Decision-1, Jev, and [open-jev](https://github.com/daseinlabs/open-jev)
(a self-hosted Jev reproduction on Apple Silicon) using **one client**.

Background articles: [Jev, the model that answers in types](https://jocheojeda.com/2026/09/22/jev-typesafe-system-one-model-structured-output/) ·
[System One in C#](https://jocheojeda.com/2026/09/22/system-one-in-csharp-jev-and-local-harness/) ·
[Jev alternatives](https://jocheojeda.com/2026/09/23/jev-alternatives-what-actually-replaces-it/) ·
[How Jev works vs. an LLM](https://jocheojeda.com/2026/09/27/how-jev-works-vs-llm-essay-multiple-choice/)

## Same contract, three servers

| | Jev (TypeSafe) | Microsoft-Decision-1 | open-jev |
|---|---|---|---|
| URL | `https://api.typesafe.ai/v1/systemone` | `<foundry-endpoint>/providers/microsoft/v1/systemone` | `http://127.0.0.1:8000/v1/systemone` |
| Auth | `Authorization: Bearer <key>` | `api-key: <key>` or Entra `Bearer` | none (or `Bearer` if `OPENJEV_API_KEY` set) |
| `model` | `jev-latest` | **your deployment name** | ignored |
| Answers | `choice` / `score` / `noul` + `probabilities`, `confidence`, `usage` | identical (checked against Jev's [OpenAPI spec](https://api.typesafe.ai/openapi.json)) | identical |
| Base model | not disclosed | Qwen3.5-9B, built by Microsoft's Office of the CTO ([launch coverage](https://www.beri.net/article/microsoft-decision-1-foundry-decision-model-pricing-benchmarks-vs-jev-clef-gpt-6-luna-decisions)) | whatever you load (here Qwen3-4B-Instruct, 4-bit MLX) |
| Price | $0.042 / M input, output free | $0.042 / M input, output free | your electricity |

So the whole client is one class with three factories ([`SystemOne.cs`](src/Decision1VsJev/SystemOne.cs)):

```csharp
var client = SystemOneClient.Foundry(http, endpoint, deployment: "decision-1", apiKey);
// or SystemOneClient.Jev(http, typesafeKey)
// or SystemOneClient.OpenJev(http)

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

r.Answers["team"].Choice;            // "engineering"
r.Answers["team"].TopProbability;    // 0.991
r.Answers["severity"].Score;         // 2.18  (probability-weighted level index, 0..3)
r.Answers["repeat_contact"].Noul;    // 0.995
```

## Results

48 labeled support tickets (English, Spanish, Russian; [data/tickets.jsonl](data/tickets.jsonl)),
three questions each: **team** (choice of 4), **severity** (score 0..3), **churn** (noul: threatening to
leave?). Run 2026-10-12 from a Mac Studio (M1 Max) in Arizona; Foundry in East US, GlobalStandard.

| | **decision1** (microsoft-decision-1) | **openjev** (open-jev) |
|---|---|---|
| tickets | 48 | 48 |
| team accuracy | 100.0% | 85.4% |
| team Brier (lower = better calibrated) | 0.001 | 0.227 |
| answered at p ≥ 0.8 (coverage) | 100% | 96% |
| accuracy when p ≥ 0.8 | 100.0% | 89.1% |
| severity MAE (0..3 scale) | 0.15 | 0.78 |
| churn accuracy (p ≥ 0.5) | 100.0% | 97.9% |
| churn Brier | 0.002 | 0.012 |
| answer flips when options reversed | 0 / 48 | 7 / 48 |
| latency 1 question p50 / p95 | 217 / 361 ms | 238 / 246 ms |
| latency 3 questions p50 / p95 | 256 / 560 ms | 562 / 628 ms |
| network floor (GET /, p50) | 74 ms | 0 ms |
| input tokens per 3-question call (p50) | 193 | 238 |
| 429 retries | 0 | 0 |

What that says:

- **Decision-1 didn't miss on this set.** Every route, every churn call, and every severity guess
  within one level is right. More interesting than the 100%: its two *least* confident answers
  (p = 0.87 and 0.88) are exactly the two tickets I wrote to be ambiguous ("switch from monthly to
  annual billing": billing or sales? "SSO login loop": account or technical?). The probability
  drops where a human would hesitate. That's what you threshold on.
- **Order doesn't move it.** Reversing the option list flipped 0 of 48 answers (Microsoft claims
  zero flips on shuffled options; this agrees). open-jev flipped 7.
- **Extra questions are almost free.** One question = 217 ms, three questions in the same request
  = 256 ms (p50, including a 74 ms network floor, so about 143 → 182 ms on Microsoft's side). open-jev
  pays one prefill per question: 238 → 562 ms. Batch your questions on Decision-1; minimize them
  on open-jev.
- **The free local option is usable but overconfident.** open-jev with a 4B model routes 85% right,
  but it was wrong at p = 1.00 three times (an invoice-address ticket sent to account, a data-leak
  report sent to account, an add-on purchase sent to technical), and it rates almost everything a
  severity of 1+. A wrong answer with p = 1.00 sails through any threshold, so the abstain trick
  doesn't protect you.
- **Cost:** about 193 input tokens per 3-question call, so roughly $0.000008 per call, or about
  120,000 three-question decisions per dollar. This whole benchmark (145 calls) cost about a tenth
  of a cent.
- **Caveat on the 100%:** 48 tickets is too small and too clean to separate two good models. It
  tells you Decision-1 handles routine support triage. It can't tell you whether Decision-1 beats Jev;
  for that, run `bench jev` on the same file.

## Run it

```bash
dotnet test        # 6 hermetic tests, no network, no key (fixture = a real Decision-1 response)
```

**Microsoft-Decision-1.** Deploy it (portal: Model catalog → Microsoft-Decision-1 → Deploy, or):

```bash
az cognitiveservices account deployment create -g <rg> -n <foundry-account> \
  --deployment-name decision-1 --model-name Microsoft-Decision-1 --model-format Microsoft \
  --model-version 1 --sku-name GlobalStandard --sku-capacity 60

export AZURE_FOUNDRY_ENDPOINT=https://<your-endpoint>     # from the deployment page
export AZURE_FOUNDRY_KEY=<key>                             # or AZURE_FOUNDRY_TOKEN=<entra token>
export DECISION_DEPLOYMENT=decision-1
dotnet run --project src/Decision1VsJev -- demo decision1
dotnet run --project src/Decision1VsJev -- bench decision1
```

**open-jev** (Apple Silicon):

```bash
git clone https://github.com/daseinlabs/open-jev && cd open-jev && uv sync
.venv/bin/openjev serve --port 8000 --model mlx-community/Qwen3-4B-Instruct-2507-4bit
dotnet run --project src/Decision1VsJev -- bench openjev
```

**Jev** (early access): `export TYPESAFE_API_KEY=...` then `bench jev`. Same code path.

`bench` prints the table above and writes every answer to `results/<engine>.jsonl`, so you can
check any number in it.

## Traps I hit (so you don't)

1. **"microsoft" is a reserved word** in Azure resource names. A deployment called
   `microsoft-decision-1` fails with `ContainsReservedWord`. Name it `decision-1`.
2. **No custom subdomain → no Entra.** A Foundry/AI Services account created without a custom
   subdomain only has the regional endpoint (`https://eastus.api.cognitive.microsoft.com`), and
   that endpoint takes `api-key`, not an Entra bearer token. The docs' Entra sample assumes a
   `<name>.services.ai.azure.com` endpoint.
3. **Content-Length is mandatory.** .NET's `JsonContent` streams the body chunked; the Foundry
   gateway answers `400 no_content_length_header`. Serialize to a `StringContent`.
4. **Capacity changes don't reach the gateway.** I deployed at capacity 1 (= 1 request/minute),
   raised it to 60, and the control plane said 60 while every response kept saying
   `x-ratelimit-limit-requests: 1`. That was still true 10 minutes later, and still true after
   deleting and recreating the deployment **under the same name**. A new deployment name got 60
   immediately. Deploy at the capacity you want from the start.
5. **429s carry `retry-after-ms`,** not the standard `Retry-After`. A client that only reads
   `Retry-After` backs off on its own schedule and can give up before the window resets.

## Honest caveats

- **48 tickets, labeled by me.** Some are deliberately ambiguous (is "upgrade to Business for
  audit logs" sales or billing?). It's a sanity check on *your kind* of workload, not a
  leaderboard. Swap in your own labeled data before deciding anything.
- **Network is in the hosted numbers.** The bench machine is a Mac Studio (M1 Max) in Arizona;
  the Foundry resource is in East US. The "network floor" row is what a bare `GET /` to the same
  host costs; subtract it before comparing with a local engine.
- **open-jev here runs a 4B model, Decision-1 is 9B.** Different sizes, different training. The
  point of including open-jev is "what do you get for free on your own Mac with the same API".
- **Jev itself is not in the table** because I don't have an early-access key. The code is ready:
  `TYPESAFE_API_KEY=... dotnet run --project src/Decision1VsJev -- bench jev`.
