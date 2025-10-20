using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Hosting;
using OpenAI;
using OpenAI.Chat;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var channel = Channel.CreateUnbounded<JobMessage>();
builder.Services.AddSingleton(channel);
builder.Services.AddSingleton<JobStatusStore>();
builder.Services.AddSingleton<ReportStore>();
builder.Services.AddHostedService<FetchParseMetricsWorker>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/", () => Results.Ok(new { status = "running" }));

app.MapPost("/jobs", async (CreateJobRequest req, Channel<JobMessage> ch, JobStatusStore status) =>
{
    if (string.IsNullOrWhiteSpace(req.Url)) return Results.BadRequest(new { error = "url is required" });
    var jobId = Guid.NewGuid().ToString("N");
    await ch.Writer.WriteAsync(new JobMessage(jobId, req.Url));
    status.Set(jobId, "Queued");

    return Results.Ok(new { jobId });
});

app.MapGet("/jobs/{id}", (string id, JobStatusStore status) =>
{
    var s = status.Get(id);

    return Results.Ok(new { id, status = s ?? "Unknown" });
});

app.MapGet("/jobs/{id}/report", (string id, ReportStore reports) =>
{
    var r = reports.Get(id);

    return r is null ? Results.NotFound(new { error = "Report not ready" }) : Results.Json(r);
});

app.MapGet("/jobs", (JobStatusStore status) => Results.Json(status.List()));

app.MapGet("/reports/{id}/html", (string id, ReportStore reports) =>
{
    var r = reports.Get(id);

    if (r is null) return Results.NotFound(new { error = "Report not ready" });
    var json = JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true });
    var html = $@"<!doctype html><html><head><meta charset=""utf-8""><title>Report {WebUtility.HtmlEncode(id)}</title>
<style>body{{font-family:system-ui,Segoe UI,Arial,sans-serif;margin:24px}}pre{{background:#111;color:#eee;padding:12px;border-radius:8px;overflow:auto}}</style>
</head><body><h1>Report {WebUtility.HtmlEncode(id)}</h1><pre>{WebUtility.HtmlEncode(json)}</pre></body></html>";

    return Results.Content(html, "text/html", Encoding.UTF8);
});

app.MapGet("/reports/{id}/download", (string id, ReportStore reports) =>
{
    var r = reports.Get(id);

    if (r is null) return Results.NotFound(new { error = "Report not ready" });

    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }));

    return Results.File(bytes, "application/json", $"report-{id}.json");
});

app.Run();

record CreateJobRequest([property: JsonPropertyName("url")] string Url);
record JobMessage(string JobId, string Url);

class JobStatusStore
{
    private readonly ConcurrentDictionary<string, string> _d = new();
    public void Set(string id, string s) => _d[id] = s;
    public string? Get(string id) => _d.TryGetValue(id, out var v) ? v : null;
    public IEnumerable<object> List() => _d.Select(kv => new { id = kv.Key, status = kv.Value });
}

class ReportStore
{
    private readonly ConcurrentDictionary<string, object> _d = new();
    public void Set(string id, object report) => _d[id] = report;
    public object? Get(string id) => _d.TryGetValue(id, out var v) ? v : null;
}

class FetchParseMetricsWorker(Channel<JobMessage> ch, JobStatusStore status, ReportStore reports, IConfiguration cfg) : BackgroundService
{
    private readonly Channel<JobMessage> _channel = ch;
    private readonly JobStatusStore _status = status;
    private readonly ReportStore _reports = reports;
    private readonly IConfiguration _cfg = cfg;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var job = await _channel.Reader.ReadAsync(ct);
            var tmp = Path.Combine(Path.GetTempPath(), "repo-" + job.JobId);
            Directory.CreateDirectory(tmp);

            try
            {
                _status.Set(job.JobId, "Fetching");
                await Run("git", $"clone --depth 1 {job.Url} .", tmp, ct);

                _status.Set(job.JobId, "Parsing");
                var files = Directory.GetFiles(tmp, "*.*", SearchOption.AllDirectories)
                    .Where(IsCodeFile).Take(400).ToList();

                _status.Set(job.JobId, "Metrics");
                var metrics = new List<FileMetric>();

                foreach (var f in files)
                {
                    var code = await File.ReadAllTextAsync(f, ct);
                    metrics.Add(new FileMetric
                    {
                        Path = MakeRelative(tmp, f),
                        Lines = code.Count(c => c == '\n') + 1,
                        Cyclomatic = Cyclomatic(code),
                        Cognitive = Cognitive(code)
                    });
                }

                _status.Set(job.JobId, "Summarizing");
                var top = metrics.OrderByDescending(m => m.Cyclomatic).Take(3).ToList();

                var aiSummary = await TrySummarizeWithAzureAsync(_cfg, job.Url, files.Count, metrics, top, ct);
                var summary = aiSummary ?? new
                {
                    overview = "Basic static analysis is completed.",
                    risks = top.Select(m => new { file = m.Path, reason = $"High cyclomatic = {m.Cyclomatic}" }),
                    actions = new[] { "Split long methods", "Add unit tests to risky files" }
                };
                var summarySource = aiSummary != null ? "azure" : "fallback";

                _status.Set(job.JobId, "Saving");
                var report = new
                {
                    jobId = job.JobId,
                    repo = new { url = job.Url, files = files.Count },
                    metrics = new
                    {
                        cyclomatic = new
                        {
                            avg = metrics.Count == 0 ? 0 : Math.Round(metrics.Average(m => m.Cyclomatic), 2),
                            max = metrics.Count == 0 ? 0 : metrics.Max(m => m.Cyclomatic)
                        },
                        cognitive = new
                        {
                            avg = metrics.Count == 0 ? 0 : Math.Round(metrics.Average(m => m.Cognitive), 2),
                            max = metrics.Count == 0 ? 0 : metrics.Max(m => m.Cognitive)
                        }
                    },
                    topRiskFiles = top,
                    summary,
                    summarySource
                };

                _reports.Set(job.JobId, report);
                _status.Set(job.JobId, "Completed");
            }
            catch (Exception ex)
            {
                _status.Set(job.JobId, "Failed");
                _reports.Set(job.JobId, new { jobId = job.JobId, error = ex.Message });
            }
            finally
            {
                TryDelete(tmp);
            }
        }
    }

    static string BuildPrompt(string repoUrl, int fileCount, List<FileMetric> metrics, List<FileMetric> top)
    {
        var brief =
            $"Repo: {repoUrl}\nFiles: {fileCount}\n" +
            $"Max CC: {metrics.DefaultIfEmpty().Max(m => m?.Cyclomatic ?? 0)}, " +
            $"Max Cog: {metrics.DefaultIfEmpty().Max(m => m?.Cognitive ?? 0)}\n" +
            "Top risky files:\n" +
            string.Join("\n", top.Select(t => $"- {t.Path} (lines={t.Lines}, cc={t.Cyclomatic}, cog={t.Cognitive})"));

        return
            $@"You are a code review assistant. Return strictly valid JSON (no code fences).
Schema:
{{""overview"": string,""risks"": 
[ {{ ""file"": string, ""reason"": string }} ],
""actions"": [ string ]
}}
Context:
{brief}
Rules:
- Up to 3 short actions. Start with a verb.
- Base risks on given metrics and file names only.";
    }

    static async Task<object?> TrySummarizeWithAzureAsync(
        IConfiguration cfg,
        string repoUrl, int fileCount, List<FileMetric> metrics, List<FileMetric> top,
        CancellationToken ct)
    {
        var endpoint = cfg["AZURE_OPENAI_ENDPOINT"];
        var key = cfg["AZURE_OPENAI_APIKEY"];
        var deployment = cfg["AZURE_OPENAI_DEPLOYMENT"];

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(deployment))
            return null;

        try
        {
            var client = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(key));
            var chat = client.GetChatClient(deployment);

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage("You are a concise code reviewer. Reply valid JSON only."),
                new UserChatMessage(BuildPrompt(repoUrl, fileCount, metrics, top))
            };

            var options = new ChatCompletionOptions { Temperature = 0.2f, MaxOutputTokenCount = 2048 };

            var resp = await chat.CompleteChatAsync(messages, options, ct);
            var content = resp.Value.Content.Count > 0 ? resp.Value.Content[0].Text : "{}";
            var json = ExtractJson(content);

            return JsonDocument.Parse(json).Deserialize<object>();
        }
        catch
        {
            return null;
        }
    }

    static string ExtractJson(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "{}";

        var t = s.Trim();

        if (t.StartsWith("```"))
        {
            var start = t.IndexOf('{'); var end = t.LastIndexOf('}');

            if (start >= 0 && end >= start) t = t.Substring(start, end - start + 1);
        }

        return t;
    }

    static readonly string[] IgnoreFolders = { "/node_modules/", "/bin/", "/obj/", "/dist/", "/build/", "/wwwroot/lib/", "/vendor/", "/.git/" };
   
    static bool IsIgnoredPath(string path)
    {
        var p = path.Replace('\\', '/');

        return IgnoreFolders.Any(f => p.Contains(f, StringComparison.OrdinalIgnoreCase));
    }
    
    static bool IsMinified(string path) =>
        path.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase);

    static bool IsCodeFile(string p) =>
        (p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
         p.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
         p.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
         p.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
        && !IsIgnoredPath(p) && !IsMinified(p);

    static string MakeRelative(string root, string full) =>
        full.StartsWith(root) ? full[(root.Length)..].TrimStart(Path.DirectorySeparatorChar) : full;

    static int Cyclomatic(string s)
    {
        string[] keys = { "if(", "if (", "for(", "for (", "while(", "while (", "case ", "catch(", "&&", "||", " ? " };
        
        return keys.Sum(k => Count(s, k)) + 1;
    }

    static int Cognitive(string s)
    {
        int depth = 0, max = 0;

        foreach (char c in s)
        {
            if (c == '{') { depth++; max = Math.Max(max, depth); }
            else if (c == '}') { depth = Math.Max(0, depth - 1); }
        }

        return max;
    }

    static int Count(string s, string k)
    {
        int c = 0, i = 0;

        while ((i = s.IndexOf(k, i, StringComparison.Ordinal)) >= 0) { c++; i += k.Length; }

        return c;
    }

    static async Task Run(string exe, string args, string workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = new Process { StartInfo = psi };
        p.Start();
        await p.WaitForExitAsync(ct);

        if (p.ExitCode != 0)
        {
            var err = await p.StandardError.ReadToEndAsync();
            throw new Exception($"{exe} {args} failed: {err}");
        }
    }

    static void TryDelete(string path)
    {
        try { Directory.Delete(path, true); } catch { }
    }

    class FileMetric
    {
        public string Path { get; set; } = "";
        public int Lines { get; set; }
        public int Cyclomatic { get; set; }
        public int Cognitive { get; set; }
    }
}
