using System.Net;
using System.Net.Http.Json;
using DigiAhan.CDR.Receiver.Models;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class LegacyAgentBridgeService
{
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly ILogger<LegacyAgentBridgeService> _logger;

    public LegacyAgentBridgeService(HttpClient http, IConfiguration configuration, ILogger<LegacyAgentBridgeService> logger)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(2);
        _logger = logger;
        var configured = configuration["SellerWorkspace:LegacyAgentBaseUrl"] ?? "http://127.0.0.1:5088";
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) ||
            !(uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("SellerWorkspace:LegacyAgentBaseUrl must be a loopback URL.");
        _baseUri = uri;
    }

    public async Task<AgentCustomerCard?> GetCurrentAsync(SellerIdentity seller, CancellationToken ct)
    {
        var cards = await Task.WhenAll(seller.Extensions.Select(extension => GetExtensionAsync(extension, ct)));
        return cards.Where(card => card is not null).OrderByDescending(card => card!.EventTimeUtc).FirstOrDefault();
    }

    private async Task<AgentCustomerCard?> GetExtensionAsync(string extension, CancellationToken ct)
    {
        try
        {
            var url = new Uri(_baseUri, $"/api/agent/{Uri.EscapeDataString(extension)}/current");
            using var response = await _http.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NoContent) return null;
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<AgentEventEnvelope>(cancellationToken: ct))?.Card;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Legacy agent bridge timed out for extension {Extension}.", extension);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Legacy agent bridge failed for extension {Extension}.", extension);
            return null;
        }
    }
}
