using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DigiAhan.CDR.Receiver.Models;

namespace DigiAhan.CDR.Receiver.Services;

public sealed class SellerWorkspaceAccessService
{
    private readonly IConfiguration _configuration;

    public SellerWorkspaceAccessService(IConfiguration configuration)
        => _configuration = configuration;

    public SellerIdentity? Authenticate(HttpContext context)
    {
        var supplied = context.Request.Headers["X-Seller-Token"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(supplied))
            return null;

        var options = _configuration
            .GetSection("SellerWorkspace")
            .Get<SellerWorkspaceOptions>() ?? new SellerWorkspaceOptions();

        if (!options.Enabled)
            return null;

        foreach (var agent in options.Agents)
        {
            if (string.IsNullOrWhiteSpace(agent.AccessToken) ||
                !FixedTimeEquals(supplied, agent.AccessToken))
                continue;

            var extensions = agent.Extensions
                .Where(value => Regex.IsMatch(value ?? string.Empty, @"^\d{3}$"))
                .Distinct(StringComparer.Ordinal)
                .Take(20)
                .ToArray();

            if (extensions.Length == 0 || string.IsNullOrWhiteSpace(agent.Key))
                return null;

            return new SellerIdentity(
                agent.Key.Trim(),
                string.IsNullOrWhiteSpace(agent.DisplayName) ? agent.Key.Trim() : agent.DisplayName.Trim(),
                extensions,
                agent.ProductGroups.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray());
        }

        return null;
    }

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
