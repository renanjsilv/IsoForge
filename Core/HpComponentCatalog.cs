using System.IO;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;

namespace IsoForge.Core;

/// <summary>
/// Drivers INDIVIDUAIS da HP (HP Image Assistant): mapeia modelo -> SysID (via o catálogo de
/// packs) e baixa o arquivo de referência da plataforma
/// (hpia.hpcloud.hp.com/ref/&lt;SysID&gt;/&lt;SysID&gt;_64_10.0.2009.cab), que lista cada driver
/// (SoftPaq .exe) com categoria e URL. Baixa só os escolhidos e extrai (SoftPaq /s /e /f).
/// </summary>
public class HpComponentCatalog : IDriverComponentCatalog
{
    const string PackCatalogUrl = "https://hpia.hpcloud.hp.com/downloads/driverpackcatalog/HPClientDriverPackCatalog.cab";

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

    public HpComponentCatalog()
    {
        BaseFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IsoForge", "Drivers", "HP");
        Directory.CreateDirectory(BaseFolder);
    }

    /// <summary>Carrega os modelos (nome -> SysID) do catálogo de packs da HP.</summary>
    public async Task EnsureLoadedAsync(IProgress<string> log, CancellationToken ct)
    {
        if (_models != null) return;
        var dir = BaseFolder;
        var cab = Path.Combine(dir, "hpcatalog.cab");
        var exDir = Path.Combine(dir, "_x");
        var xmlPath = DellDriverCatalog.FreshXml(cab, exDir, TimeSpan.FromHours(24));
        if (xmlPath == null)
        {
            log.Report("Baixando catálogo da HP...");
            await DownloadAsync(PackCatalogUrl, cab, ct, null);
            if (Directory.Exists(exDir)) { try { Directory.Delete(exDir, true); } catch { } }
            Directory.CreateDirectory(exDir);
            Expand(cab, exDir);
            xmlPath = Directory.GetFiles(exDir).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault()
                ?? throw new InvalidOperationException("não encontrei o XML do catálogo da HP.");
        }

        var doc = XDocument.Load(xmlPath);
        var list = new List<DriverModelRef>();
        foreach (var p in doc.Descendants().Where(e => e.Name.LocalName == "ProductOSDriverPack"))
        {
            var os = El(p, "OSName");
            if (!os.Contains("Windows 11", StringComparison.OrdinalIgnoreCase)) continue;
            var name = El(p, "SystemName");
            var sysId = El(p, "SystemId").Split(',').FirstOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(sysId)) continue;
            list.Add(new DriverModelRef(name, sysId));
        }
        _models = list
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        log.Report($"{_models.Count} modelos HP (Windows 11) no catálogo.");
    }

    public List<DriverModelRef> Models() => _models ?? new();

    // Releases do Windows 11 (mais nova -> mais antiga). O nome do .cab de referência usa o
    // OSReleaseIdFilename em minúsculo; nem todo modelo publica todas as releases, então
    // testamos da mais nova para a mais antiga e usamos a primeira que existir.
    static readonly string[] Win11Releases = { "25h2", "24h2", "23h2", "22h2", "21h2", "2009" };

    /// <summary>Lista os drivers do modelo lendo o arquivo de referência da plataforma (SysID).</summary>
    public async Task<List<DriverComponent>> DriversForModelAsync(DriverModelRef model, IProgress<string> log, CancellationToken ct)
    {
        var sysid = model.SystemId.ToLowerInvariant();
        log.Report($"Buscando drivers do modelo {model.Name} (plataforma {sysid})...");
        var dir = Path.Combine(BaseFolder, "_ref_" + sysid);
        if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
        Directory.CreateDirectory(dir);
        var cab = Path.Combine(dir, "ref.cab");

        string? refUrl = null;
        foreach (var rel in Win11Releases)
        {
            var u = $"https://hpia.hpcloud.hp.com/ref/{sysid}/{sysid}_64_10.0.{rel}.cab";
            if (await UrlExistsAsync(u, ct)) { refUrl = u; break; }
        }
        if (refUrl == null) { log.Report("Nenhum arquivo de referência (drivers individuais) para este modelo."); return new(); }
        try { await DownloadAsync(refUrl, cab, ct, null); }
        catch { log.Report("Nenhum arquivo de referência (drivers individuais) para este modelo."); return new(); }
        // expand.exe recusa extrair se o .cab estiver na pasta de destino ("expandir em si mesmo"):
        // extraímos numa subpasta separada e lemos o maior arquivo (o XML de referência).
        var exDir = Path.Combine(dir, "x");
        Directory.CreateDirectory(exDir);
        Expand(cab, exDir);
        // expand nomeia a saída pelo nome do .cab de origem, então o XML sai como "ref.cab":
        // pegamos simplesmente o maior arquivo extraído.
        var xmlPath = Directory.GetFiles(exDir).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
        if (xmlPath == null) { log.Report("Não consegui ler o arquivo de referência da HP."); return new(); }

        var doc = XDocument.Load(xmlPath);
        var comps = new List<(DriverComponent c, Version v)>();
        foreach (var u in doc.Descendants().Where(e => e.Name.LocalName == "UpdateInfo"))
        {
            var cat = El(u, "Category");
            if (!cat.StartsWith("Driver -", StringComparison.OrdinalIgnoreCase)) continue; // só drivers
            var supported = El(u, "SupportedOS");
            if (supported.Length > 0 && !supported.Contains("W11", StringComparison.OrdinalIgnoreCase)) continue;
            var name = El(u, "Name");
            var url = El(u, "Url");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
            long.TryParse(El(u, "Size"), out var size);
            comps.Add((new DriverComponent(name, Categorize(cat), url, El(u, "MD5"), size), ParseVersion(El(u, "Version"))));
        }
        // A referência lista várias versões do mesmo driver; mantém só a mais nova por nome+categoria.
        var result = comps
            .GroupBy(t => (t.c.Category, t.c.Name), TupleCmp)
            .Select(g => g.OrderByDescending(t => t.v).First().c)
            .OrderBy(c => c.Category, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        log.Report($"{result.Count} drivers individuais para {model.Name}.");
        return result;
    }

    static readonly IEqualityComparer<(string, string)> TupleCmp =
        EqualityComparer<(string, string)>.Create(
            (a, b) => string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase),
            t => (t.Item1.ToLowerInvariant(), t.Item2.ToLowerInvariant()).GetHashCode());

    static Version ParseVersion(string s)
    {
        var cleaned = new string((s ?? "").Where(ch => char.IsDigit(ch) || ch == '.').ToArray()).Trim('.');
        var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries).Take(4).ToArray();
        return Version.TryParse(string.Join('.', parts.Length >= 2 ? parts : parts.Append("0")), out var v) ? v : new Version(0, 0);
    }

    static string Categorize(string c) => c.Replace("Driver -", "").Trim() switch
    {
        "Network" => "Rede",
        "Graphics" => "Vídeo",
        "Audio" => "Áudio",
        "Chipset" => "Chipset / Sistema",
        "Enabling" => "Chipset / Sistema",
        "Storage" => "Armazenamento",
        "Keyboard, Mouse and Input Devices" => "Entrada (teclado/mouse)",
        var other => string.IsNullOrWhiteSpace(other) ? "Outros" : other
    };

    /// <summary>Baixa os SoftPaqs selecionados e extrai (/s /e /f) num único passo elevado (1 UAC).</summary>
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
            var outDir = Path.Combine(root, "d" + i.ToString("00"));
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
            "nenhum driver foi extraído (a extração dos SoftPaqs HP exige elevação/UAC — confirme o prompt).");
        return root;
    }

    // SoftPaq da HP: spXXXXX.exe /s /e /f <pasta> (só extrai, não instala).
    static string BuildExtractScript(List<(string exe, string outDir)> jobs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='SilentlyContinue'");
        foreach (var (exe, outDir) in jobs)
        {
            sb.AppendLine($"$e='{exe.Replace("'", "''")}'; $o='{outDir.Replace("'", "''")}'");
            sb.AppendLine("Start-Process -FilePath $e -ArgumentList '/s','/e','/f',$o -Wait -WindowStyle Hidden");
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

    static async Task<bool> UrlExistsAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return resp.IsSuccessStatusCode;
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
