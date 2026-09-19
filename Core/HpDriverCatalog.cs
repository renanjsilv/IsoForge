using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace IsoForge.Core;

/// <summary>
/// Driver packs da HP (catálogo oficial MEMCM/SCCM):
/// hpia.hpcloud.hp.com/downloads/driverpackcatalog/HPClientDriverPackCatalog.cab
/// Um SoftPaq (.exe auto-extraível) por modelo/SO.
/// </summary>
public class HpDriverCatalog : IDriverPackCatalog
{
    const string CatalogUrl = "https://hpia.hpcloud.hp.com/downloads/driverpackcatalog/HPClientDriverPackCatalog.cab";

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

    public HpDriverCatalog()
    {
        BaseFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IsoForge", "Drivers", "HP");
        Directory.CreateDirectory(BaseFolder);
    }

    public async Task<List<DriverPackModel>> FetchModelsAsync(IProgress<string> log, CancellationToken ct)
    {
        if (_modelCache != null) return _modelCache;

        var dir = BaseFolder;
        var cab = Path.Combine(dir, "hpcatalog.cab");
        var exDir = Path.Combine(dir, "_x");
        var xmlPath = DellDriverCatalog.FreshXml(cab, exDir, TimeSpan.FromHours(24));
        if (xmlPath == null)
        {
            log.Report("Baixando catálogo de drivers da HP...");
            await DownloadAsync(CatalogUrl, cab, ct, null);
            if (Directory.Exists(exDir)) { try { Directory.Delete(exDir, true); } catch { } }
            Directory.CreateDirectory(exDir);
            Expand(cab, exDir);
            xmlPath = Directory.GetFiles(exDir).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault()
                ?? throw new InvalidOperationException("não encontrei o XML do catálogo da HP.");
        }
        else log.Report("Catálogo de drivers da HP em cache (recente).");

        log.Report("Lendo modelos do catálogo...");
        var doc = XDocument.Load(xmlPath);

        // SoftPaqs: Id -> (Url, Size, MD5)
        var softpaqs = new Dictionary<string, (string url, long size, string md5)>(StringComparer.OrdinalIgnoreCase);
        foreach (var sp in doc.Descendants().Where(e => e.Name.LocalName == "SoftPaq"))
        {
            var id = El(sp, "Id"); var url = El(sp, "Url");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url)) continue;
            long.TryParse(El(sp, "Size"), out var size);
            softpaqs[id] = (url, size, El(sp, "MD5") ?? "");
        }

        var list = new List<DriverPackModel>();
        foreach (var p in doc.Descendants().Where(e => e.Name.LocalName == "ProductOSDriverPack"))
        {
            var os = El(p, "OSName") ?? "";
            if (!os.Contains("Windows 11", StringComparison.OrdinalIgnoreCase)) continue;
            var arch = El(p, "Architecture") ?? "";
            if (arch.Length > 0 && !arch.Contains("64")) continue;
            var name = El(p, "SystemName");
            var spId = El(p, "SoftPaqId");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(spId)) continue;
            if (!softpaqs.TryGetValue(spId, out var sp)) continue;
            list.Add(new DriverPackModel(name.Trim(), sp.url, sp.md5, sp.size));
        }

        _modelCache = list
            .GroupBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(m => m.SizeBytes).First()) // maior pack = mais recente/completo
            .OrderBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return _modelCache;
    }

    public async Task<string> DownloadAndExtractAsync(DriverPackModel model, IProgress<string> log, IProgress<double>? pct, CancellationToken ct)
    {
        var safe = string.Concat(model.Label.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
        var dir = Path.Combine(BaseFolder, safe);
        if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "pack.exe");
        var outDir = Path.Combine(dir, "x"); // sem espaços
        Directory.CreateDirectory(outDir);

        log.Report($"Baixando driver pack HP: {model.Label} ({model.SizeText})...");
        await DownloadAsync(model.Url, exe, ct, pct);

        if (!string.IsNullOrWhiteSpace(model.HashMd5))
        {
            var actual = Md5Of(exe);
            if (!actual.Equals(model.HashMd5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"MD5 do pack não confere (esperado {model.HashMd5}, obtido {actual}).");
        }

        log.Report("Extraindo drivers (SoftPaq da HP)...");
        // SoftPaq da HP: spXXXXX.exe /s /e /f <pasta> (só extrai, não instala).
        var args = $"/s /e /f \"{outDir}\"";
        if (!RunExtract(exe, args, outDir)) RunExtract(exe, args, outDir, elevated: true);

        try { File.Delete(exe); } catch { }
        if (!Directory.EnumerateFiles(outDir, "*.inf", SearchOption.AllDirectories).Any())
            throw new InvalidOperationException(
                "não consegui extrair os .inf do SoftPaq HP (a extração pode exigir elevação/UAC — confirme o prompt).");
        return outDir;
    }

    static bool RunExtract(string exe, string args, string outDir, bool elevated = false)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args) { CreateNoWindow = true };
            if (elevated) { psi.UseShellExecute = true; psi.Verb = "runas"; psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden; }
            else psi.UseShellExecute = false;
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(600_000)) { try { p.Kill(true); } catch { } return false; }
            return Directory.EnumerateFiles(outDir, "*.inf", SearchOption.AllDirectories).Any();
        }
        catch { return false; }
    }

    static string El(XElement parent, string localName) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value?.Trim() ?? "";

    static void Expand(string cabPath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var psi = new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "expand.exe"),
            $"\"{cabPath}\" -F:* \"{destDir}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("não consegui iniciar o expand.exe.");
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"expand.exe falhou (código {p.ExitCode}).");
    }

    static string Md5Of(string path)
    {
        using var md5 = MD5.Create();
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
