using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace IsoForge.Core;

/// <summary>
/// Auto-atualização: ao abrir, consulta o último release do repositório no GitHub e,
/// se houver versão mais nova, baixa o instalador e o executa. Assim, cada nova versão
/// publicada no GitHub chega automaticamente ao usuário.
/// </summary>
public static class Updater
{
    public const string Owner = "renanjsilv";
    public const string Repo = "IsoForge";

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public record UpdateInfo(Version Version, string Tag, string DownloadUrl, string Notes);

    static string SkipFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IsoForge", "skipped-version.txt");

    /// <summary>Marca uma versão para não ser mais oferecida (até surgir uma maior).</summary>
    public static void SkipVersion(Version v)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SkipFile)!);
            File.WriteAllText(SkipFile, v.ToString());
        }
        catch { }
    }

    /// <summary>Esta versão (ou anterior) foi marcada como "pular"?</summary>
    public static bool IsSkipped(Version v)
    {
        try
        {
            if (File.Exists(SkipFile) && Version.TryParse(File.ReadAllText(SkipFile).Trim(), out var s))
                return Norm(s) >= Norm(v);
        }
        catch { }
        return false;
    }

    static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("IsoForge-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Retorna a atualização disponível, ou null se já está atualizado / sem internet.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = NewClient(TimeSpan.FromSeconds(15));
        var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        var json = await http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
        var notes = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";

        // Procura um asset .exe (o instalador).
        string? dl = null;
        if (root.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    dl = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                    break;
                }
            }
        if (dl == null) return null;

        var cur = Norm(CurrentVersion);
        var lat = Norm(latest);
        return lat > cur ? new UpdateInfo(lat, tag, dl, notes) : null;
    }

    static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Baixa o instalador para a pasta temporária e retorna o caminho.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? pct = null, CancellationToken ct = default)
    {
        using var http = NewClient(TimeSpan.FromMinutes(10));
        var dest = Path.Combine(Path.GetTempPath(), $"IsoForge-Setup-{info.Tag}.exe");

        using var resp = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var fs = File.Create(dest))
        {
            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total > 0) pct?.Report(read * 100.0 / total);
            }
        }
        return dest;
    }

    /// <summary>Executa o instalador baixado (que fecha/atualiza o app) e encerra o IsoForge.</summary>
    public static void RunInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
        System.Windows.Application.Current.Shutdown();
    }
}
