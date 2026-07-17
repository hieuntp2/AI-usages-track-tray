using System.Text.Json;

namespace AiUsageTray.Providers.Claude;

/// <summary>
/// Parses the JSON emitted by `claude auth status --json`. Pure/stateless so it can be tested
/// against captured fixtures without launching the CLI. Reads only the behavior-governing fields
/// and never surfaces email/orgId/orgName.
/// </summary>
public static class ClaudeAuthStatusParser
{
    public static bool TryParse(string json, out ClaudeAuthStatus? status, out string? error)
    {
        status = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Auth status output was empty.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Auth status output was not a JSON object.";
                return false;
            }

            var loggedIn = root.TryGetProperty("loggedIn", out var loggedInEl)
                && loggedInEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                && loggedInEl.GetBoolean();

            status = new ClaudeAuthStatus(
                LoggedIn: loggedIn,
                AuthMethod: ReadString(root, "authMethod"),
                SubscriptionType: ReadString(root, "subscriptionType"));
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Invalid auth status JSON: {ex.Message}";
            return false;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
