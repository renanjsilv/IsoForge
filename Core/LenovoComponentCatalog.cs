using System.IO;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;

namespace IsoForge.Core;

/// <summary>
/// Drivers INDIVIDUAIS da Lenovo (catálogo do Lenovo System Update, por modelo/MTM):
///  1) catalogv2.xml -> mapeia modelo -> MTM;
///  2) catalog/&lt;MTM&gt;_Win11.xml -> lista de pacotes (location do descritor + categoria);
///  3) cada descritor -> título, .exe (nome/tamanho/URL) e comando de extração.
/// Baixa só os drivers escolhidos e extrai (auto-extrator da Lenovo).
/// </summary>
public class LenovoComponentCatalog : IDriverComponentCatalog
{
    const string PackCatalogUrl = "https://download.lenovo.com/cdrt/td/catalogv2.xml";

    static readonly HttpClient Http = CreateHttp();
    static HttpClient CreateHttp()
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        h.Timeout = TimeSpan.FromMinutes(60);
        h.DefaultRequestHeaders.UserAgent.ParseAdd("IsoForge/1.0");
        return h;
    }

    public string BaseFolder { get; }
    List<DriverModelRef>? _models;

    public LenovoComponentCatalog()
    {
        BaseFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IsoForge", "Drivers", "Lenovo");
        Directory.CreateDirectory(BaseFolder);
    }

    /// <summary>Carrega o catálogo de modelos (usa o catalogv2 p/ mapear modelo -> MTM).</summary>
    public async Task EnsureLoadedAsync(IProgress<string> log, CancellationToken ct)
    {
        if (_models != null) return;
        var xml = Path.Combine(BaseFolder, "catalogv2.xml");
        bool fresh = File.Exists(xml) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(xml)) < TimeSpan.FromHours(24);
        if (!fresh)
        {
            log.Report("Baixando catálogo da Lenovo...");
            await DownloadAsync(PackCatalogUrl, xml, ct, null);
        }
        var doc = XDocument.Load(xml);
        var list = new List<DriverModelRef>();
        foreach (var model in doc.Descendants().Where(e => e.Name.LocalName == "Model"))
        {
            var name = ((string?)model.Attribute("name") ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            bool win11 = model.Elements().Any(e => e.Name.LocalName == "SCCM"
                && string.Equals((string?)e.Attribute("os"), "win11", StringComparison.OrdinalIgnoreCase));
            if (!win11) continue;
            var mtm = model.Descendants().FirstOrDefault(e => e.Name.LocalName == "Type")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(mtm)) continue;
            list.Add(new DriverModelRef(name, mtm)); // SystemId = MTM
        }
        _models = list
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        log.Report($"{_models.Count} modelos Lenovo (Windows 11) no catálogo.");
    }

    public List<DriverModelRef> Models() => _models ?? new();

    /// <summary>Lista os drivers individuais do modelo: catálogo do MTM -> descritores (em paralelo).</summary>
    public async Task<List<DriverComponent>> DriversForModelAsync(DriverModelRef model, IProgress<string> log, CancellationToken ct)
    {
        var mtm = model.SystemId;
        log.Report($"Buscando drivers do modelo {model.Name} (MTM {mtm})...");
        var catUrl = $"https://download.lenovo.com/catalog/{mtm}_Win11.xml";
        string catXml;
        try { catXml = await Http.GetStringAsync(catUrl, ct); }
        catch { log.Report("Nenhum catálogo individual para este modelo."); return new(); }

        var pkgDoc = XDocument.Parse(catXml);
        var locations = new List<(string loc, string cat)>();
        foreach (var p in pkgDoc.Descendants().Where(e => e.Name.LocalName == "package"))
        {
            var loc = p.Elements().FirstOrDefault(e => e.Name.LocalName == "location")?.Value?.Trim();
            var cat = p.Elements().FirstOrDefault(e => e.Name.LocalName == "category")?.Value?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(loc)) continue;
            if (cat.Contains("BIOS", StringComparison.OrdinalIgnoreCase)) continue; // BIOS não é injetável
            locations.Add((loc, cat));
        }

        // Busca os descritores em paralelo (limitado) para montar a lista.
        var sem = new SemaphoreSlim(8);
        var tasks = locations.Select(async item =>
        {
            await sem.WaitAsync(ct);
            try { return await ParseDescriptorAsync(item.loc, item.cat, ct); }
            catch { return null; }
            finally { sem.Release(); }
        });
        var comps = (await Task.WhenAll(tasks)).Where(c => c != null).Select(c => c!).ToList();
        log.Report($"{comps.Count} drivers individuais para {model.Name}.");
        return comps
            .GroupBy(c => c.Url, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(c => c.Category, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    async Task<DriverComponent?> ParseDescriptorAsync(string descriptorUrl, string category, CancellationToken ct)
    {
        var xml = await Http.GetStringAsync(descriptorUrl, ct);
        var doc = XDocument.Parse(xml);
        var pkg = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Package");
        if (pkg == null) return null;
        var title = pkg.Descendants().FirstOrDefault(e => e.Name.LocalName == "Desc")?.Value?.Trim()
            ?? (string?)pkg.Attribute("name") ?? "driver";
        // .exe do instalador
        var fileEl = pkg.Descendants().FirstOrDefault(e => e.Name.LocalName == "Installer")
            ?.Descendants().FirstOrDefault(e => e.Name.LocalName == "File");
        var exeName = fileEl?.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(exeName) || !exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        long.TryParse(fileEl?.Elements().FirstOrDefault(e => e.Name.LocalName == "Size")?.Value, out var size);
        var baseUrl = descriptorUrl[..(descriptorUrl.LastIndexOf('/') + 1)];
        var url = baseUrl + exeName;
        return new DriverComponent(title, Categorize(category), url, "", size);
    }

    static string Categorize(string c)
    {
        c = c ?? "";
        if (c.Contains("Audio", StringComparison.OrdinalIgnoreCase)) return "Áudio";
        if (c.Contains("Wireless", StringComparison.OrdinalIgnoreCase) || c.Contains("WLAN", StringComparison.OrdinalIgnoreCase) || c.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)) return "Rede (Wi-Fi)";
        if (c.Contains("LAN", StringComparison.OrdinalIgnoreCase) || c.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) || c.Contains("Networking", StringComparison.OrdinalIgnoreCase) || c.Contains("WWAN", StringComparison.OrdinalIgnoreCase)) return "Rede";
        if (c.Contains("Video", StringComparison.OrdinalIgnoreCase) || c.Contains("Display", StringComparison.OrdinalIgnoreCase) || c.Contains("Graphics", StringComparison.OrdinalIgnoreCase)) return "Vídeo";
        if (c.Contains("Storage", StringComparison.OrdinalIgnoreCase) || c.Contains("SATA", StringComparison.OrdinalIgnoreCase) || c.Contains("NVMe", StringComparison.OrdinalIgnoreCase) || c.Contains("RAID", StringComparison.OrdinalIgnoreCase)) return "Armazenamento";
        if (c.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) return "Bluetooth";
        if (c.Contains("Camera", StringComparison.OrdinalIgnoreCase) || c.Contains("Card Reader", StringComparison.OrdinalIgnoreCase)) return "Câmera / Leitor";
        if (c.Contains("Mouse", StringComparison.OrdinalIgnoreCase) || c.Contains("Keyboard", StringComparison.OrdinalIgnoreCase) || c.Contains("Touchpad", StringComparison.OrdinalIgnoreCase) || c.Contains("Pointing", StringComparison.OrdinalIgnoreCase)) return "Entrada (teclado/mouse)";
        if (c.Contains("Fingerprint", StringComparison.OrdinalIgnoreCase) || c.Contains("Security", StringComparison.OrdinalIgnoreCase)) return "Segurança";
        if (c.Contains("Chipset", StringComparison.OrdinalIgnoreCase) || c.Contains("Motherboard", StringComparison.OrdinalIgnoreCase) || c.Contains("Management Engine", StringComparison.OrdinalIgnoreCase)) return "Chipset / Sistema";
        return string.IsNullOrWhiteSpace(c) ? "Outros" : c;
    }

    /// <summary>Baixa os .exe selecionados e extrai (auto-extrator Lenovo) num único passo elevado (1 UAC).</summary>
    public async Task<string> DownloadAndExtractAsync(IReadOnlyList<DriverComponent> selected, string modelLabel,
        IProgress<string> log, IProgress<double>? pct, CancellationToken ct)
    {
        var safe = string.Concat(modelLabel.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
        var root = Path.Combine(BaseFolder, "sel_" + safe);
        if (Directory.Exists(root)) { try { Directory.Delete(root, true); } catch { } }
        Directory.CreateDirectory(root);
        var dlDir = Path.Combine(root, "_dl");
        Directory.CreateDirectory(dlDir);

        var jobs = new List<(string exe, string outDir)>();
        for (int i = 0; i < selected.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var d = selected[i];
            log.Report($"[{i + 1}/{selected.Count}] Baixando {d.Name} ({d.SizeText})...");
            var exe = Path.Combine(dlDir, $"d{i}.exe");
            await DownloadAsync(d.Url, exe, ct, pct);
            var outDir = Path.Combine(root, "d" + i.ToString("00")); // sem espaços (o /DIR exige)
            Directory.CreateDirectory(outDir);
            jobs.Add((exe, outDir));
        }
        if (jobs.Count == 0) throw new InvalidOperationException("nenhum driver foi baixado.");

        log.Report("Extraindo os drivers (será pedida elevação/UAC uma vez)...");
        var helper = Path.Combine(root, "extract.ps1");
        File.WriteAllText(helper, BuildExtractScript(jobs), new UTF8Encoding(true));
        await Task.Run(() => RunElevated("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{helper}\""), ct);

        int ok = jobs.Count(j => Directory.EnumerateFiles(j.outDir, "*.inf", SearchOption.AllDirectories).Any());
        try { Directory.Delete(dlDir, true); } catch { }
        try { File.Delete(helper); } catch { }
        log.Report($"Drivers extraídos: {ok}/{jobs.Count}.");
        if (ok == 0) throw new InvalidOperationException(
            "nenhum driver foi extraído (a extração dos pacotes Lenovo exige elevação/UAC — confirme o prompt).");
        return root;
    }

    // Auto-extrator da Lenovo: <exe> /VERYSILENT /DIR=<out> /EXTRACT=YES (só extrai, não instala).
    static string BuildExtractScript(List<(string exe, string outDir)> jobs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='SilentlyContinue'");
        foreach (var (exe, outDir) in jobs)
        {
            sb.AppendLine($"$e='{exe.Replace("'", "''")}'; $o='{outDir.Replace("'", "''")}'");
            sb.AppendLine("Start-Process -FilePath $e -ArgumentList '/VERYSILENT',\"/DIR=$o\",'/EXTRACT=YES' -Wait -WindowStyle Hidden");
        }
        return sb.ToString();
    }

    static void RunElevated(string exe, string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
            { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit();
        }
        catch { }
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
