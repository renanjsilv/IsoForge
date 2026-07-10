using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace IsoForge.Core;

/// <summary>Um driver individual (Dell Update Package) aplicável a um modelo, para download avulso.</summary>
public record DellDriver(string Name, string Category, string Url, string HashMd5, long SizeBytes)
{
    public string SizeText => SizeBytes >= 1024L * 1024 ? $"{SizeBytes / 1024.0 / 1024.0:0} MB" : $"{SizeBytes / 1024.0:0} KB";
}

/// <summary>Referência a um modelo (nome + systemID) para filtrar componentes.</summary>
public record DellModelRef(string Name, string SystemId);

/// <summary>
/// Lê o catálogo de COMPONENTES individuais da Dell (CatalogPC.cab): cada driver é um DUP .EXE
/// separado, pequeno, com URL própria. Permite baixar só os drivers escolhidos (economiza banda)
/// e extraí-los para .inf (via o extrator silencioso do próprio DUP: /s /e=).
/// </summary>
public class DellComponentCatalog
{
    const string CatalogUrl = "https://downloads.dell.com/catalog/CatalogPC.cab";

    static readonly HttpClient Http = CreateHttp();
    static HttpClient CreateHttp()
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        h.Timeout = TimeSpan.FromMinutes(60);
        h.DefaultRequestHeaders.UserAgent.ParseAdd("IsoForge/1.0");
        return h;
    }

    public string BaseFolder { get; }
    List<ParsedComponent>? _cache;

    public DellComponentCatalog()
    {
        BaseFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IsoForge", "Drivers");
        Directory.CreateDirectory(BaseFolder);
    }

    internal record ParsedComponent(string Name, string Category, string Url, string HashMd5, long Size, List<DellModelRef> Models);

    /// <summary>Baixa o catálogo (uma vez), extrai o XML e faz o parse dos componentes de driver Win11 x64.</summary>
    public async Task EnsureLoadedAsync(IProgress<string> log, CancellationToken ct)
    {
        if (_cache != null) return; // cache em memória (mesma sessão)
        var dir = Path.Combine(BaseFolder, "_pccatalog");
        Directory.CreateDirectory(dir);
        var cab = Path.Combine(dir, "catalogpc.cab");
        var exDir = Path.Combine(dir, "x");

        // Reaproveita o XML em disco se o .cab é recente (< 24h) — pula download + expand.
        var xmlPath = DellDriverCatalog.FreshXml(cab, exDir, TimeSpan.FromHours(24));
        if (xmlPath == null)
        {
            log.Report("Baixando catálogo de componentes da Dell (CatalogPC)...");
            await DownloadAsync(CatalogUrl, cab, ct, null);
            if (Directory.Exists(exDir)) { try { Directory.Delete(exDir, true); } catch { } }
            Directory.CreateDirectory(exDir);
            Expand(cab, exDir);
            xmlPath = Directory.GetFiles(exDir).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault()
                ?? throw new InvalidOperationException("não encontrei o XML dentro do CatalogPC.");
        }
        else log.Report("Catálogo de componentes em cache (recente).");

        log.Report("Lendo componentes de driver (Windows 11 x64)...");
        _cache = await Task.Run(() => ParseComponents(xmlPath), ct);
        log.Report($"{_cache.Count} componentes de driver Windows 11 x64 no catálogo.");
    }

    /// <summary>Modelos que têm ao menos um driver Win11 x64 (um por NOME — vários systemID
    /// da Dell podem compartilhar o mesmo nome, por isso deduplica por nome, não por systemID).</summary>
    public List<DellModelRef> Models()
    {
        if (_cache == null) return new();
        return _cache.SelectMany(c => c.Models)
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Drivers individuais aplicáveis a um modelo pelo NOME (une todos os systemID desse nome).</summary>
    public List<DellDriver> DriversForName(string modelName)
    {
        if (_cache == null) return new();
        return _cache
            .Where(c => c.Models.Any(m => m.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase)))
            .Select(c => new DellDriver(c.Name, c.Category, c.Url, c.HashMd5, c.Size))
            .GroupBy(d => d.Url, StringComparer.OrdinalIgnoreCase).Select(g => g.First()) // mesmo driver em vários systemID
            .OrderBy(d => d.Category, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Parse em streaming (baixo consumo de memória) do CatalogPC.xml.</summary>
    internal static List<ParsedComponent> ParseComponents(string xmlPath)
    {
        var list = new List<ParsedComponent>();
        var baseLocation = "downloads.dell.com";
        var settings = new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true, DtdProcessing = DtdProcessing.Ignore };
        using var reader = XmlReader.Create(xmlPath, settings);
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "SoftwareComponent")
            {
                var el = (XElement)XNode.ReadFrom(reader); // lê só este componente (subtree)
                var comp = Interpret(el, baseLocation);
                if (comp != null) list.Add(comp);
            }
            else
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Manifest")
                {
                    var bl = reader.GetAttribute("baseLocation");
                    if (!string.IsNullOrWhiteSpace(bl)) baseLocation = bl;
                }
                reader.Read();
            }
        }
        return list;
    }

    static ParsedComponent? Interpret(XElement el, string baseLocation)
    {
        // Só drivers (não BIOS/firmware/app).
        var type = El(el, "ComponentType")?.Attribute("value")?.Value;
        if (!string.Equals(type, "DRVR", StringComparison.OrdinalIgnoreCase)) return null;

        // Só Windows 11 x64. ATENÇÃO: os códigos da Dell enganam — W11AP/AH/A1 = ARM64, W11S5 = SE,
        // W11TM = IoT; o Windows 11 x64 "normal" é W21H4/W21P4 (Display "Windows 11"). Por isso
        // filtramos pelo Display "Windows 11" e excluímos ARM64 (à prova de futuras versões).
        bool win11 = el.Descendants().Where(e => e.Name.LocalName == "OperatingSystem").Any(os =>
        {
            var disp = DisplayIn(os) ?? "";
            return disp.Contains("Windows 11", StringComparison.OrdinalIgnoreCase)
                && !disp.Contains("ARM", StringComparison.OrdinalIgnoreCase);
        });
        if (!win11) return null;

        var path = (string?)el.Attribute("path");
        if (string.IsNullOrWhiteSpace(path)) return null;
        var md5 = (string?)el.Attribute("hashMD5") ?? "";
        long.TryParse((string?)el.Attribute("size"), out var size);
        var url = $"https://{baseLocation}/{path}";
        var name = DisplayIn(El(el, "Name")) ?? Path.GetFileName(path);
        var catEl = El(el, "Category");
        var category = CategoryOf((string?)catEl?.Attribute("value"), DisplayIn(catEl));

        var models = new List<DellModelRef>();
        foreach (var brand in el.Descendants().Where(e => e.Name.LocalName == "Brand"))
        {
            var brandName = (DisplayIn(brand) ?? "").Trim();
            var brandFirst = brandName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            foreach (var m in brand.Elements().Where(e => e.Name.LocalName == "Model"))
            {
                var sysId = (string?)m.Attribute("systemID");
                if (string.IsNullOrWhiteSpace(sysId)) continue;
                var modelName = (DisplayIn(m) ?? "").Replace('-', ' ').Trim();
                // Rótulo = Marca + Modelo (no CatalogPC o modelo costuma ser só o número, ex.: "Latitude" + "3550").
                string label;
                if (string.IsNullOrWhiteSpace(modelName))
                    label = brandName.Length > 0 ? brandName : sysId;
                else if (brandFirst.Length > 0 && !modelName.Contains(brandFirst, StringComparison.OrdinalIgnoreCase))
                    label = $"{brandName} {modelName}".Trim();
                else
                    label = modelName;
                models.Add(new DellModelRef(label, sysId));
            }
        }
        if (models.Count == 0) return null;
        return new ParsedComponent(name, category, url, md5, size, models);
    }

    static XElement? El(XElement parent, string localName) => parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
    static string? DisplayIn(XElement? el)
    {
        if (el == null) return null;
        var disp = el.Elements().FirstOrDefault(e => e.Name.LocalName == "Display");
        var v = (disp?.Value ?? "").Trim();
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    static string CategoryOf(string? code, string? display) => (code ?? "").ToUpperInvariant() switch
    {
        "NI" => "Rede",
        "VI" => "Vídeo",
        "AU" => "Áudio",
        "CS" => "Chipset / Sistema",
        "SA" or "SF" => "Armazenamento",
        "CM" => "Comunicação / Modem",
        "IN" => "Entrada (teclado/mouse)",
        "SE" => "Segurança",
        _ => string.IsNullOrWhiteSpace(display) ? "Outros" : display
    };

    /// <summary>Baixa os DUPs selecionados, confere o MD5 e extrai os .inf (dup /s /e=) numa pasta única.</summary>
    public async Task<string> DownloadAndExtractAsync(IReadOnlyList<DellDriver> selected, string modelLabel,
        IProgress<string> log, IProgress<double>? pct, CancellationToken ct)
    {
        var safe = string.Concat(modelLabel.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');
        var root = Path.Combine(BaseFolder, "sel_" + safe);
        if (Directory.Exists(root)) { try { Directory.Delete(root, true); } catch { } }
        Directory.CreateDirectory(root);

        // FASE 1 — baixa todos os DUPs (sem elevação).
        var dupDir = Path.Combine(root, "_dup");
        Directory.CreateDirectory(dupDir);
        var jobs = new List<(string dup, string outDir)>();
        for (int i = 0; i < selected.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var d = selected[i];
            log.Report($"[{i + 1}/{selected.Count}] Baixando {d.Name} ({d.SizeText})...");
            var dup = Path.Combine(dupDir, $"d{i}.exe");
            await DownloadAsync(d.Url, dup, ct, pct);
            if (!string.IsNullOrWhiteSpace(d.HashMd5) && !Md5Of(dup).Equals(d.HashMd5, StringComparison.OrdinalIgnoreCase))
            {
                log.Report($"  AVISO: MD5 não confere em {d.Name}; pulando.");
                continue;
            }
            var outDir = Path.Combine(root, "d" + i.ToString("00")); // caminho SEM espaços (o /drivers= exige)
            Directory.CreateDirectory(outDir);
            jobs.Add((dup, outDir));
        }
        if (jobs.Count == 0) throw new InvalidOperationException("nenhum DUP foi baixado. Verifique a conexão.");

        // FASE 2 — extrai TODOS de uma vez num único processo elevado (os DUPs da Dell exigem admin).
        // Um único prompt de UAC. O extrator tenta /drivers= (só .inf) e cai para /e= se preciso.
        log.Report("Extraindo os drivers (será pedida elevação/UAC uma vez)...");
        var helper = Path.Combine(root, "extract.ps1");
        File.WriteAllText(helper, BuildExtractScript(jobs), new UTF8Encoding(true));
        bool ran = await Task.Run(() => RunElevated("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{helper}\""), ct);
        if (!ran) log.Report("  AVISO: a elevação (UAC) foi negada ou falhou.");

        int ok = jobs.Count(j => Directory.EnumerateFiles(j.outDir, "*.inf", SearchOption.AllDirectories).Any());
        try { Directory.Delete(dupDir, true); } catch { }
        try { File.Delete(helper); } catch { }
        log.Report($"Drivers extraídos: {ok}/{jobs.Count}.");
        if (ok == 0) throw new InvalidOperationException(
            "nenhum driver foi extraído (a extração dos pacotes Dell exige elevação/UAC — confirme o prompt). " +
            "Se preferir não usar UAC, use o modo 'pack completo'.");
        return root;
    }

    // Script que extrai cada DUP: tenta /drivers= (só .inf) e, se não gerar .inf, cai para /e=.
    static string BuildExtractScript(List<(string dup, string outDir)> jobs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='SilentlyContinue'");
        foreach (var (dup, outDir) in jobs)
        {
            var d = dup.Replace("'", "''");
            var o = outDir.Replace("'", "''");
            sb.AppendLine($"$dup='{d}'; $out='{o}'");
            sb.AppendLine("Start-Process -FilePath $dup -ArgumentList '/s',\"/drivers=$out\" -Wait -WindowStyle Hidden");
            sb.AppendLine("if (-not (Get-ChildItem $out -Recurse -Filter *.inf -ErrorAction SilentlyContinue)) {");
            sb.AppendLine("  Start-Process -FilePath $dup -ArgumentList '/s',\"/e=$out\" -Wait -WindowStyle Hidden");
            sb.AppendLine("}");
        }
        return sb.ToString();
    }

    // Executa um comando elevado (UAC). Retorna false se o usuário negar a elevação.
    static bool RunElevated(string exe, string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
            { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit();
            return true;
        }
        catch { return false; } // 1223 = UAC cancelado
    }

    // ------------------------------------------------------------------
    static void Expand(string cabPath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var psi = new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "expand.exe"),
            $"\"{cabPath}\" -F:* \"{destDir}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("não consegui iniciar o expand.exe.");
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
