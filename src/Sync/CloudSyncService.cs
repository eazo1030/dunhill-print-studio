using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Dunhill.PrintStudio.Pplz;
using Dunhill.PrintStudio.Services;

using Dunhill.PrintStudio.Models;
namespace Dunhill.PrintStudio.Sync;

/// <summary>
/// Bidirectional sync with the dunhill-inventory-service on Vercel (Phase 1+).
///
/// Pull:   pending cloud jobs → GET  /api/jobs/pending?agent=&lt;name&gt;
/// EPC:    POST /api/epc/next → deterministic counter-per-(fabric,color,yardage) 24-hex
/// Print:  PrintService.PrintLabelJobAsync() with a LabelSpec built from the
///         cloud payload. The EPC from /api/epc/next is passed as epcHex so
///         the printer writes exactly the pre-allocated EPC (no local random).
/// Push:   result            → POST /api/print-events
/// Auth:   Bearer token in the Authorization header (PRINT_API_TOKEN).
///
/// Lifecycle (one poll cycle):
///   1. GET /api/jobs/pending → server atomically claims oldest queued job
///      and returns { job: {...}, queue_empty: bool }. Empty queue → sleep.
///   2. POST /api/epc/next { fabric, color, yardage } → returns
///      { epc, counter_value, namespace }. If the call fails (network down
///      or 5xx), fall back to local random generation so a single transient
///      failure doesn't stall the whole queue.
///   3. Convert job payload to LabelSpec (FabricName, Color, Yardage, Po=Epc).
///   4. PrintService.PrintLabelJobAsync() — sends PPLZ to the connected
///      Postek transport (spooler/USB/TCP/browser-print), reads back the
///      EPC it just wrote to the inlay, returns a PrintJobOutcome.
///   5. POST /api/print-events with { job_id, epc, state, error? }.
///
/// Failure handling:
///   - Network errors retry with exponential backoff (1, 2, 4, 8, 16 s),
///     capped at 60s. The cloud-side job stays 'claimed' (not finished) so
///     a future health-check pass can decide whether to retry or release.
///   - 401 disables sync and fires AuthRequired. Operator re-enters token.
///   - Print failures fire /api/print-events with state='failed' + error so
///     the website sees it as a red toast instead of waiting forever.
/// </summary>
public sealed class CloudSyncService : BackgroundService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<CloudSyncService> _log;
    private readonly SyncConfig _cfg;
    private readonly PrintService _printer;
    private string _baseUrl = "https://dunhill-inventory-service.vercel.app";
    private string _authToken = "ec4c38b989417e2c55f48d4c7b4122074a5318c6b3c51bbba813d1509bf62c0f";
    private string _agentName = Environment.MachineName;
    private int _backoffSeconds = 0;

    public event EventHandler<CloudEvent>? OnEvent;

    public CloudSyncService(
        IHttpClientFactory httpFactory,
        ILogger<CloudSyncService> log,
        SyncConfig cfg,
        PrintService printer)
    {
        _httpFactory = httpFactory;
        _log = log;
        _cfg = cfg;
        _printer = printer;
    }

    public void Configure(string baseUrl, string authToken, string? agentName = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _authToken = authToken;
        if (!string.IsNullOrWhiteSpace(agentName))
            _agentName = agentName.Trim();
    }

    /// <summary>True when there's a valid token and polling is enabled.</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(_authToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("CloudSyncService started. Agent='{Agent}' polling {Url} every {Sec}s",
            _agentName, _baseUrl, _cfg.PollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (IsConfigured)
                    await PollOnceAsync(stoppingToken);
                else
                    _log.LogDebug("Auth token empty; skipping poll.");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cloud poll iteration failed.");
            }

            var sleepSeconds = _backoffSeconds > 0
                ? _backoffSeconds
                : _cfg.PollIntervalSeconds;
            try { await Task.Delay(TimeSpan.FromSeconds(sleepSeconds), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private void ApplyBackoff(bool hard)
    {
        if (hard)
        {
            // Hard error (network down, server 5xx) — exponential up to 60s
            _backoffSeconds = _backoffSeconds == 0 ? 1 : Math.Min(_backoffSeconds * 2, 60);
        }
        else
        {
            // Soft success path — reset to normal interval
            _backoffSeconds = 0;
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var http = _httpFactory.CreateClient("dunhill");
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _authToken);

        // 1. Pull one job (server-side atomic claim)
        HttpResponseMessage resp;
        try
        {
            resp = await http.GetAsync(
                $"{_baseUrl}/api/jobs/pending?agent={Uri.EscapeDataString(_agentName)}", ct);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "Network error reaching {Url}", _baseUrl);
            OnEvent?.Invoke(this, new CloudEvent(CloudEventKind.ConnectionLost, null, ex.Message));
            ApplyBackoff(hard: true);
            return;
        }

        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _log.LogWarning("Cloud returned 401 — re-auth needed.");
            OnEvent?.Invoke(this, new CloudEvent(CloudEventKind.AuthRequired, null, null));
            return;
        }
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("Cloud pending returned HTTP {Code}", (int)resp.StatusCode);
            ApplyBackoff(hard: true);
            return;
        }
        ApplyBackoff(hard: false);

        var pending = await resp.Content.ReadFromJsonAsync<PendingResponse>(cancellationToken: ct);
        if (pending?.Job == null) return; // queue empty

        var job = pending.Job;
        _log.LogInformation("Claimed job {Id}: {Fabric} / {Color} / {Yardage}",
            job.Id, job.Fabric, job.Color, job.Yardage);

        OnEvent?.Invoke(this, new CloudEvent(CloudEventKind.JobReceived, job.Id, job));

        // 2.5. Get deterministic EPC from cloud (counter-per-(fabric,color,yardage)).
        //      If the call fails for any reason, fall back to local random so a
        //      single transient failure doesn't stall the queue.
        string? epc = await TryGetDeterministicEpcAsync(http, job, ct);

        // 3. Build LabelSpec from cloud payload
        var spec = new LabelSpec(
            Sku: job.Sku ?? "",
            Name: job.Fabric,
            Qty: 1,
            EncodeRfid: true,
            FabricName: $"{job.Fabric} — {job.Color}",
            Yardage: job.Yardage != null ? $"{job.Yardage:0.##} YD" : null,
            Po: job.Ul,
            DatePrinted: DateTime.UtcNow.ToString("yyyy-MM-dd")
        );

        // Label dims match what the website's print-studio.js uses (862×236 = 73×20mm @ 300 DPI).
        var dims = new LabelDimensions(WidthDots: 862, HeightDots: 236, GapDots: 24);

        // 4. Actually print with the deterministic EPC (or null → local random fallback)
        PrintJobOutcome outcome;
        try
        {
            outcome = await _printer.PrintLabelJobAsync(spec, dims, epcHex: epc, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PrintLabelJobAsync threw for job {Id}", job.Id);
            outcome = new PrintJobOutcome(PrintJobStatus.Failed, null, ex.Message);
        }

        // 5. Report result
        var state = outcome.IsSuccess ? "succeeded" : "failed";
        var reportedEpc = outcome.ReadbackHex ?? epc;
        var error = outcome.IsSuccess ? null : outcome.Error ?? "print_failed";

        await ReportCompletionAsync(job.Id, state, reportedEpc, error, ct);

        OnEvent?.Invoke(this, new CloudEvent(
            CloudEventKind.JobCompleted, job.Id,
            new { job_id = job.Id, state, epc = reportedEpc, error }));
    }

    /// <summary>
    /// Call POST /api/epc/next to get a deterministic 24-hex EPC for this (fabric, color, yardage).
    /// Returns null on any failure — the caller falls back to PrintService's local random EPC.
    /// </summary>
    private async Task<string?> TryGetDeterministicEpcAsync(HttpClient http, PrintJobPayload job, CancellationToken ct)
    {
        try
        {
            var req = new
            {
                fabric = job.Fabric,
                color = job.Color,
                yardage = job.Yardage,
            };
            var resp = await http.PostAsJsonAsync($"{_baseUrl}/api/epc/next", req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("EPC next endpoint returned HTTP {Code}; falling back to local random",
                    (int)resp.StatusCode);
                return null;
            }
            var payload = await resp.Content.ReadFromJsonAsync<EpcNextResponse>(cancellationToken: ct);
            if (payload == null || string.IsNullOrEmpty(payload.Epc))
            {
                _log.LogWarning("EPC next endpoint returned empty body; falling back to local random");
                return null;
            }
            _log.LogDebug("Allocated EPC {Epc} (counter={Counter}, namespace={Ns})",
                payload.Epc, payload.CounterValue, payload.Namespace);
            return payload.Epc;
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "Network error fetching EPC from cloud; falling back to local random");
            return null;
        }
        catch (TaskCanceledException)
        {
            // propagate cancellation — let the caller handle it
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Unexpected error fetching EPC; falling back to local random");
            return null;
        }
    }

    public async Task ReportCompletionAsync(
        string jobId, string state, string? epc, string? error, CancellationToken ct = default)
    {
        var http = _httpFactory.CreateClient("dunhill");
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _authToken);

        var payload = new
        {
            job_id = jobId,
            state,
            epc,
            error,
        };
        try
        {
            var resp = await http.PostAsJsonAsync($"{_baseUrl}/api/print-events", payload, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("print-events for {JobId} returned HTTP {Code}: {Body}",
                    jobId, (int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
            }
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "Network error reporting {JobId}", jobId);
        }
    }
}

public sealed record SyncConfig(
    int PollIntervalSeconds = 5,
    int MaxRetries = 5,
    int RetryBackoffSeconds = 10,
    int InventoryPushIntervalSeconds = 30
);

// Wire format from /api/jobs/pending
public sealed record PendingResponse(
    [property: JsonPropertyName("job")] PrintJobPayload? Job,
    [property: JsonPropertyName("queue_empty")] bool QueueEmpty
);

public sealed record PrintJobPayload(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("sku")] string? Sku,
    [property: JsonPropertyName("fabric")] string Fabric,
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("yardage")] double? Yardage,
    [property: JsonPropertyName("ul")] string? Ul,
    [property: JsonPropertyName("epc")] string? Epc
);

// Wire format from /api/epc/next
public sealed record EpcNextResponse(
    [property: JsonPropertyName("epc")] string Epc,
    [property: JsonPropertyName("counter_value")] long CounterValue,
    [property: JsonPropertyName("namespace")] string Namespace
);

public enum CloudEventKind { JobReceived, AuthRequired, JobCompleted, ConnectionLost }

public sealed record CloudEvent(
    CloudEventKind Kind,
    string? JobId,
    object? Payload
);