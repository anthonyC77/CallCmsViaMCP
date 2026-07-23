using System.Diagnostics;

namespace SiteGuardian.Api.Services.Audit;

/// <summary>
/// Wrapper autour de web_audit_agent.py : les contrôles déterministes (0 €)
/// existent déjà dans ce script — on ne les réécrit pas, on l'invoque
/// (`--urls … --json …`) et on parse sa sortie JSON.
/// </summary>
public class PythonAuditService
{
    private readonly string _pythonExe;
    private readonly string _scriptPath;
    private readonly ILogger<PythonAuditService> _logger;

    public PythonAuditService(string pythonExe, string scriptPath, ILogger<PythonAuditService> logger)
    {
        _pythonExe = pythonExe;
        _scriptPath = scriptPath;
        _logger = logger;
    }

    /// <summary>Construit le service depuis la configuration (usage DI).</summary>
    public static PythonAuditService FromConfiguration(
        IConfiguration config, string contentRoot, ILogger<PythonAuditService> logger) =>
        new(config["Audit:PythonExecutable"] ?? "python",
            ResolveScriptPath(config["Audit:ScriptPath"] ?? "web_audit_agent.py", contentRoot),
            logger);

    /// <summary>
    /// Cherche le script : chemin absolu, sinon en remontant les répertoires parents
    /// depuis le CWD et le content root (le script vit à la racine du repo, l'Api
    /// tourne dans src/SiteGuardian.Api ou bin/Debug/…).
    /// </summary>
    public static string ResolveScriptPath(string configured, string contentRoot)
    {
        if (Path.IsPathRooted(configured))
            return configured;

        foreach (var start in new[] { Directory.GetCurrentDirectory(), contentRoot })
        {
            var dir = new DirectoryInfo(start);
            for (var depth = 0; dir is not null && depth < 6; depth++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, configured);
                if (File.Exists(candidate)) return candidate;
            }
        }
        // On retourne le chemin configuré tel quel : l'erreur explicite sortira à l'exécution.
        return configured;
    }

    public async Task<List<ScriptAuditResult>> RunAsync(
        IReadOnlyList<string> urls, bool includeText = false, CancellationToken ct = default)
    {
        if (urls.Count == 0) return new List<ScriptAuditResult>();

        var jsonPath = Path.Combine(Path.GetTempPath(), $"siteguardian-audit-{Guid.NewGuid():N}.json");
        var psi = new ProcessStartInfo(_pythonExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(_scriptPath);
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add(string.Join(",", urls));
        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add(jsonPath);
        if (includeText)
            psi.ArgumentList.Add("--include-text");

        _logger.LogInformation("Lancement de l'audit Python : {Script} sur {Count} URL(s)", _scriptPath, urls.Count);

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Impossible de démarrer {_pythonExe}.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stderr = await stderrTask;
            await stdoutTask;

            if (process.ExitCode != 0 || !File.Exists(jsonPath))
                throw new InvalidOperationException(
                    $"web_audit_agent.py a échoué (code {process.ExitCode}). stderr: {Truncate(stderr, 2000)}");

            var json = await File.ReadAllTextAsync(jsonPath, ct);
            return ScriptAuditResult.ParseList(json);
        }
        finally
        {
            try { if (File.Exists(jsonPath)) File.Delete(jsonPath); } catch (IOException) { }
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
