using System.IO;
using System.Net.Http;
using System.Xml.Linq;

namespace IsoForge.Core;

/// <summary>
/// Driver packs da Lenovo (catálogo oficial de deployment SCCM/MDT):
/// download.lenovo.com/cdrt/td/catalogv2.xml — um pack (.exe auto-extraível) por modelo/SO.
/// </summary>
public class LenovoDriverCatalog : IDriverPackCatalog
{
    const string CatalogUrl = "https://download.lenovo.com/cdrt/td/catalogv2.xml";

    static readonly HttpClient Http = CreateHttp();
    static HttpClient CreateHttp()
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        h.Timeout = TimeSpan.FromMinutes(60);
        h.DefaultRequestHeaders.UserAgent.ParseAdd("IsoForge/1.0");
        return h;
    }

    public string BaseFolder { get; }
    List<DriverPackModel>? _modelCache;

    public LenovoDriverCatalog()
    {
        BaseFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IsoForge", "Drivers", "Lenovo");
        Directory.CreateDirectory(BaseFolder);
    }

    /// <summary>Baixa o catálogo (cache 24h) e devolve os modelos com pack SCCM de Windows 11.</summary>
    public async Task<List<DriverPackModel>> FetchModelsAsync(IProgress<string> log, CancellationToken ct)
    {
        if (_modelCache != null) return _modelCache;

        var xml = Path.Combine(BaseFolder, "catalogv2.xml");
        bool fresh = File.Exists(xml) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(xml)) < TimeSpan.FromHours(24);
        if (!fresh)
        {
            log.Report("Baixando catálogo de drivers da Lenovo...");
            await DownloadAsync(CatalogUrl, xml, ct, null);
        }
        else log.Report("Catálogo de drivers da Lenovo em cache (recente).");

        log.Report("Lendo modelos do catálogo...");
        var doc = XDocument.Load(xml);
        var list = new List<DriverPackModel>();
        foreach (var model in doc.Descendants().Where(e => e.Name.LocalName == "Model"))
        {
            var name = ((string?)model.Attribute("name") ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            // Pack SCCM de Windows 11 (os="win11").
            var sccm = model.Elements().FirstOrDefault(e => e.Name.LocalName == "SCCM"
                && string.Equals((string?)e.Attribute("os"), "win11", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(e.Value));
            if (sccm == null) continue;
            var url = sccm.Value.Trim();
            var md5 = (string?)sccm.Attribute("md5") ?? "";
            list.Add(new DriverPackModel(name, url, md5, 0));
        }

        _modelCache = list
            .GroupBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return _modelCache;
    }

    /// <summary>Baixa o pack (.exe), confere MD5 e extrai os .inf (auto-extrator da Lenovo). Devolve a pasta.</summary>
    public async Task<string> DownloadAndExtractAsync(DriverPackModel model, IProgress<string> log, IProgress<double>? pct, CancellationToken ct)
    {
        var safe = string.Concat(model.Label.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
        var dir = Path.Combine(BaseFolder, safe);
        if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "pack.exe");
        var outDir = Path.Combine(dir, "x"); // sem espaços (o /DIR do extrator não gosta de espaço)
        Directory.CreateDirectory(outDir);

        log.Report($"Baixando driver pack Lenovo: {model.Label}...");
        await DownloadAsync(model.Url, exe, ct, pct);

        if (!string.IsNullOrWhiteSpace(model.HashMd5))
        {
            var actual = Md5Of(exe);
            if (!actual.Equals(model.HashMd5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"MD5 do pack não confere (esperado {model.HashMd5}, obtido {actual}).");
        }

        log.Report("Extraindo drivers (auto-extrator da Lenovo)...");
        // Auto-extrator da Lenovo (SCCM): /VERYSILENT /DIR=<pasta> /Extract=YES (só extrai, não instala).
        var args = $"/VERYSILENT /DIR={outDir} /Extract=YES";
        if (!RunExtract(exe, args, outDir, log))
            RunExtract(exe, args, outDir, log, elevated: true); // alguns exigem admin

        try { File.Delete(exe); } catch { }
        if (!Directory.EnumerateFiles(outDir, "*.inf", SearchOption.AllDirectories).Any())
            throw new InvalidOperationException(
                "não consegui extrair os .inf do pack Lenovo (a extração pode exigir elevação/UAC — confirme o prompt).");
        return outDir;
    }

    // Roda o auto-extrator; direto ou elevado (UAC). Retorna true se gerou algum .inf.
    static bool RunExtract(string exe, string args, string outDir, IProgress<string> log, bool elevated = false)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args) { CreateNoWindow = true };
            if (elevated) { psi.UseShellExecute = true; psi.Verb = "runas"; psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden; }
            else { psi.UseShellExecute = false; }
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } return false; }
            return Directory.EnumerateFiles(outDir, "*.inf", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) { if (!elevated) log.Report($"  extração direta falhou ({ex.Message}); tentando elevado..."); return false; }
    }

    static string Md5Of(string path)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(md5.ComputeHash(fs));
    }

    async Task DownloadAsync(string url, string dest, CancellationToken ct, IProgress<double>? percent)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var tmp = dest + ".part";
        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1L;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var fs = File.Create(tmp);
            var buffer = new byte[81920];
            long readTotal = 0; int n; double last = -1;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                readTotal += n;
                if (total > 0 && percent != null)
                {
                    var p = readTotal * 100.0 / total;
                    if (p - last >= 1 || p >= 100) { percent.Report(p); last = p; }
                }
            }
            percent?.Report(100);
        }
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(tmp, dest);
    }
}
