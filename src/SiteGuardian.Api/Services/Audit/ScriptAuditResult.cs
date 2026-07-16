using System.Text.Json;
using System.Text.Json.Serialization;

namespace SiteGuardian.Api.Services.Audit;

/// <summary>
/// Miroir de la dataclass <c>AuditResult</c> de web_audit_agent.py
/// (export JSON en snake_case via <c>asdict</c>).
/// </summary>
public class ScriptAuditResult
{
    public string Url { get; set; } = string.Empty;
    public string FinalUrl { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public int Score { get; set; }
    public string Grade { get; set; } = string.Empty;
    public List<string> Issues { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public int ResponseTimeMs { get; set; }
    public int PageSizeKb { get; set; }
    public int ResourceCount { get; set; }
    public int ImagesCount { get; set; }
    public int ImagesWithoutAlt { get; set; }
    public string Title { get; set; } = string.Empty;
    public int TitleLength { get; set; }
    public string MetaDescription { get; set; } = string.Empty;
    public int MetaDescriptionLength { get; set; }
    public int H1Count { get; set; }
    public bool HasCanonical { get; set; }
    public bool HasLang { get; set; }
    public bool HasViewport { get; set; }
    public bool HasOgTags { get; set; }
    public bool RobotsTxt { get; set; }
    public bool SitemapXml { get; set; }
    public bool Https { get; set; }
    public bool RedirectsToHttps { get; set; }
    public bool SslValid { get; set; }
    public int? SslDaysLeft { get; set; }
    public List<string> SecurityHeadersPresent { get; set; } = new();
    public List<string> SecurityHeadersMissing { get; set; } = new();
    public List<string> A11yIssues { get; set; } = new();
    public int InputsWithoutLabel { get; set; }
    public int ButtonsWithoutName { get; set; }
    public int LinksWithoutText { get; set; }
    public int HeadingSkips { get; set; }
    public int LinksChecked { get; set; }
    public List<string> BrokenLinks { get; set; } = new();
    public string Error { get; set; } = string.Empty;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static List<ScriptAuditResult> ParseList(string json) =>
        JsonSerializer.Deserialize<List<ScriptAuditResult>>(json, JsonOptions) ?? new();
}
