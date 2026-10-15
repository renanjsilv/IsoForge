using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace IsoForge.Core;

/// <summary>
/// Busca a lista de driver packs da Dell direto do catálogo oficial de deployment
/// (downloads.dell.com/catalog/DriverPackCatalog.cab) e baixa/extrai o pack de um modelo.
/// É o mesmo catálogo usado por SCCM/MDT: um arquivo por modelo com todos os .inf.
/// </summary>
public class DellDriverCatalog : IDriverPackCatalog
{
    const string CatalogUrl = "https://downloads.dell.com/catalog/DriverPackCatalog.cab";

    static readonly HttpClient Http = CreateHttp();
    static HttpClient CreateHttp()
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        h.Timeout = TimeSpan.FromMinutes(60);
        h.DefaultRequestHeaders.UserAgent.ParseAdd("IsoForge/1.0");
        return h;
    }

    public string BaseFolder { get; }

    public DellDriverCatalog()
    {
        BaseFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IsoForge", "Drivers");
        Directory.CreateDirectory(BaseFolder);
    }

    List<DriverPackModel>? _modelCache;

    /// <summary>Baixa o catálogo, extrai o XML e devolve os modelos com pack de Windows 11 x64.</summary>
    public async Task<List<DriverPackModel>> FetchModelsAsync(IProgress<string> log, CancellationToken ct)
    {
        if (_modelCache != null) return _modelCache; // cache em memória (mesma sessão)

        var dir = Path.Combine(BaseFolder, "_catalog");
        Directory.CreateDirectory(dir);
        var cab = Path.Combine(dir, "catalog.cab");
        var exDir = Path.Combine(dir, "x");

        // O .cab traz o DriverPackCatalog.xml. Obs.: o expand nomeia a saída pelo basename do .cab
        // (não pelo nome interno), então lemos o maior arquivo extraído.
        var xmlPath = FreshXml(cab, exDir, TimeSpan.FromHours(24));
        if (xmlPath == null)
        {
            log.Report("Baixando catálogo de drivers da Dell...");
            await DownloadAsync(CatalogUrl, cab, ct, null);
            if (Directory.Exists(exDir)) { try { Directory.Delete(exDir, true); } catch { } }
            Directory.CreateDirectory(exDir);
            Expand(cab, exDir);
            xmlPath = Directory.GetFiles(exDir).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault()
                ?? throw new InvalidOperationException("não encontrei o XML dentro do catálogo da Dell.");
        }
        else log.Report("Catálogo de drivers em cache (recente).");

        log.Report("Lendo modelos do catálogo...");
        var doc = XDocument.Load(xmlPath);
        var root = doc.Root!;
        var baseLocation = (string?)root.Attribute("baseLocation") ?? "downloads.dell.com";

        // Ignora namespace: casa pelos nomes locais (o XML da Dell pode ou não ter xmlns).
        XElement[] pkgs = root.Descendants().Where(e => e.Name.LocalName == "DriverPackage").ToArray();
        var list = new List<DriverPackModel>();
        foreach (var pkg in pkgs)
        {
            // Só Windows 11 x64.
            bool win11 = pkg.Descendants().Any(e => e.Name.LocalName == "OperatingSystem"
                && string.Equals((string?)e.Attribute("osCode"), "Windows11", StringComparison.OrdinalIgnoreCase)
                && (((string?)e.Attribute("osArch"))?.Contains("64") ?? true));
            if (!win11) continue;

            var path = (string?)pkg.Attribute("path");
            if (string.IsNullOrWhiteSpace(path)) continue;
            var md5 = (string?)pkg.Attribute("hashMD5") ?? "";
            long.TryParse((string?)pkg.Attribute("size"), out var size);
            var url = $"https://{baseLocation}/{path}";

            // Um pack pode cobrir vários modelos: cria uma entrada por modelo.
            foreach (var brand in pkg.Descendants().Where(e => e.Name.LocalName == "Brand"))
            {
                var brandName = DisplayOf(brand);
                foreach (var model in brand.Elements().Where(e => e.Name.LocalName == "Model"))
                {
                    // O atributo 'name' traz o nome amigável (ex.: "XPS 13 9380", "Latitude 5440").
                    // Se faltar, cai para "Marca Modelo" (ex.: "XPS Notebook 9380").
                    var nameAttr = ((string?)model.Attribute("name") ?? "").Trim();
                    var label = !string.IsNullOrWhiteSpace(nameAttr)
                        ? nameAttr
                        : string.Join(" ", new[] { brandName, DisplayOf(model) }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                    if (string.IsNullOrWhiteSpace(label)) continue;
                    list.Add(new DriverPackModel(label, url, md5, size));
                }
            }
        }

        // Dedup por modelo (mantém o primeiro) e ordena por nome.
        _modelCache = list
            .GroupBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return _modelCache;
    }

    /// <summary>
    /// Se o .cab em disco é recente (&lt; maxAge) e já existe o XML extraído, devolve o caminho do
    /// XML (pula download + expand). Caso contrário devolve null.
    /// </summary>
    internal static string? FreshXml(string cabPath, string exDir, TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(cabPath)) return null;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(cabPath) >= maxAge) return null;
            if (!Directory.Exists(exDir)) return null;
            return Directory.GetFiles(exDir).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Baixa o pack do modelo, confere o MD5 e extrai os .inf numa pasta. Devolve a pasta.</summary>
    public async Task<string> DownloadAndExtractAsync(DriverPackModel model, IProgress<string> log, IProgress<double>? pct, CancellationToken ct)
    {
        var safe = string.Concat(model.Label.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
        var dir = Path.Combine(BaseFolder, safe);
        var cab = Path.Combine(dir, "driverpack.cab");
        var extract = Path.Combine(dir, "extracted");

        if (Directory.Exists(extract)) { try { Directory.Delete(extract, true); } catch { } }
        Directory.CreateDirectory(dir);

        log.Report($"Baixando driver pack: {model.Label} ({model.SizeText})...");
        await DownloadAsync(model.Url, cab, ct, pct);

        if (!string.IsNullOrWhiteSpace(model.HashMd5))
        {
            log.Report("Conferindo integridade (MD5)...");
            var actual = Md5Of(cab);
            if (!actual.Equals(model.HashMd5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"MD5 do driver pack não confere (esperado {model.HashMd5}, obtido {actual}).");
        }

        log.Report("Extraindo drivers (.inf)...");
        Directory.CreateDirectory(extract);
        Expand(cab, extract);
        try { File.Delete(cab); } catch { } // libera espaço; o extraído é o que importa
        return extract;
    }

    // ------------------------------------------------------------------
    static string DisplayOf(XElement el)
    {
        var disp = el.Elements().FirstOrDefault(e => e.Name.LocalName == "Display");
        var text = (disp?.Value ?? (string?)el.Attribute("name") ?? "").Trim();
        return text;
    }

    static void Expand(string cabPath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var psi = new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "expand.exe"),
            $"\"{cabPath}\" -F:* \"{destDir}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("não consegui iniciar o expand.exe para extrair o .cab.");
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"expand.exe falhou (código {p.ExitCode}) ao extrair {Path.GetFileName(cabPath)}.");
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
