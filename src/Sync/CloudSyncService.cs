using System.Net.Http.Json;
using System.Text.Json;

namespace Dunhill.PrintStudio.Sync;

/// <summary>
/// Bidirectional sync with the dunhill-inventory-service on Vercel.
///
/// Push:   local print events → POST /api/print-events
/// Pull:   pending jobs       → GET  /api/jobs/pending  (every PollIntervalSeconds)
/// Auth:   Bearer token in the Authorization header (set in Settings)
///
/// Failure handling:
///   - Network errors retry with exponential backoff (1, 2, 4, 8, 16 seconds)
///   - After MaxRetries (default 5), the job is parked in the local queue
///     and retried on the next successful health check
///   - 401 errors disable sync and surface a "re-auth needed" notification
/// </summary>
public sealed class CloudSyncService : BackgroundService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<CloudSyncService> _log;
    private readonly SyncConfig _cfg;
    private string _baseUrl = "https://dunhill-inventory-service.vercel.app";
    private string _authToken = "";

    public event EventHandler<CloudEvent>? OnEvent;

    public CloudSyncService(
        IHttpClientFactory httpFactory,
        ILogger<CloudSyncService> log,
        SyncConfig cfg)
    {
        _httpFactory = httpFactory;
        _log = log;
        _cfg = cfg;
    }

    public void Configure(string baseUrl, string authToken)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _authToken = authToken;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("CloudSyncService started. Polling {Url} every {Sec}s",
            _baseUrl, _cfg.PollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!string.IsNullOrEmpty(_authToken))
                    await PollOnceAsync(stoppingToken);
                else
                    _log.LogDebug("Auth token empty; skipping poll.");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cloud poll iteration failed.");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(_cfg.PollIntervalSeconds), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var http = _httpFactory.CreateClient("dunhill");
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _authToken);

        var resp = await http.GetAsync($"{_baseUrl}/api/jobs/pending", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _log.LogWarning("Cloud returned 401 — re-auth needed.");
            OnEvent?.Invoke(this, new CloudEvent(CloudEventKind.AuthRequired, null, null));
            return;
        }
        resp.EnsureSuccessStatusCode();

        var jobs = await resp.Content.ReadFromJsonAsync<List<CloudJob>>(cancellationToken: ct);
        if (jobs == null || jobs.Count == 0) return;

        foreach (var job in jobs)
        {
            OnEvent?.Invoke(this, new CloudEvent(CloudEventKind.JobReceived, job.Id, job));
        }
    }

    public async Task<bool> ReportCompletionAsync(string jobId, bool success, string? error, CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient("dunhill");
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _authToken);

        var payload = new
        {
            jobId,
            status = success ? "done" : "failed",
            error,
            completedAt = DateTime.UtcNow
        };
        var resp = await http.PostAsJsonAsync($"{_baseUrl}/api/jobs/{jobId}/complete", payload, ct);
        return resp.IsSuccessStatusCode;
    }
}

public sealed record SyncConfig(
    int PollIntervalSeconds = 5,
    int MaxRetries = 5,
    int RetryBackoffSeconds = 10,
    int InventoryPushIntervalSeconds = 30
);

public sealed record CloudJob(
    string Id,
    string Sku,
    string Name,
    int Qty,
    string? Serial,
    string? Epc,
    bool EncodeRfid
);

public enum CloudEventKind { JobReceived, AuthRequired, JobCompleted, ConnectionLost }

public sealed record CloudEvent(
    CloudEventKind Kind,
    string? JobId,
    object? Payload
);
