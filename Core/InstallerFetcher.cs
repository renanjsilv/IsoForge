using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace IsoForge.Core;

public enum AppId { SevenZip, AnyDesk, OfficeOdt, AdobeReader, FortiClient }

public record FetchResult(AppId Id, string Name, string? LocalPath, string Version, string SilentArgs, bool IsOffice, string? Error = null);

/// <summary>
/// Baixa automaticamente a versão mais recente dos instaladores conhecidos para uma
/// pasta gerenciada e evita re-baixar quando já está atualizado. Reporta progresso
/// (0..100) via IProgress&lt;double&gt; quando há Content-Length.
/// </summary>
public class InstallerFetcher
{
    static readonly HttpClient Http = CreateHttp();
    static HttpClient CreateHttp()
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        h.Timeout = TimeSpan.FromMinutes(30);
        h.DefaultRequestHeaders.UserAgent.ParseAdd("IsoForge/1.0");
        return h;
    }

    const string FortiClientUrl = "https://tidatora.blob.core.windows.net/files/FortiClientVPN.msi";

    public string BaseFolder { get; }

    public InstallerFetcher()
    {
        string programData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "IsoForge", "Installers");
        try
        {
            Directory.CreateDirectory(programData);
            var probe = Path.Combine(programData, ".w");
            File.WriteAllText(probe, "x"); File.Delete(probe);
            BaseFolder = programData;
        }
        catch
        {
            BaseFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IsoForge", "Installers");
            Directory.CreateDirectory(BaseFolder);
        }
    }

    string FolderFor(AppId id) => Path.Combine(BaseFolder, id.ToString());

    public async Task<FetchResult> EnsureAsync(AppId id, IProgress<string> log, CancellationToken ct, IProgress<double>? percent = null)
    {
        try
        {
            return id switch
            {
                AppId.SevenZip => await SevenZipAsync(log, ct, percent),
                AppId.AnyDesk => await AnyDeskAsync(log, ct, percent),
                AppId.OfficeOdt => await OfficeOdtAsync(log, ct, percent),
                AppId.AdobeReader => await AdobeAsync(log, ct, percent),
                AppId.FortiClient => await FortiClientAsync(log, ct, percent),
                _ => throw new ArgumentOutOfRangeException(nameof(id))
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Report($"Falha ao obter {id}: {ex.Message}");
            return new FetchResult(id, id.ToString(), null, "", "", false, ex.Message);
        }
    }

    // ---- 7-Zip: MSI x64 mais recente (versão no nome do arquivo) ----
    async Task<FetchResult> SevenZipAsync(IProgress<string> log, CancellationToken ct, IProgress<double>? pct)
    {
        var page = await Http.GetStringAsync("https://www.7-zip.org/download.html", ct);
        var vers = Regex.Matches(page, @"a/7z(\d+)-x64\.msi").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        if (vers.Count == 0) throw new InvalidOperationException("não achei o MSI x64 na página do 7-Zip.");
        var v = vers.Max();
        var name = $"7z{v}-x64.msi";
        var dir = FolderFor(AppId.SevenZip);
        var path = Path.Combine(dir, name);
        var ver = $"{v / 100}.{v % 100:00}";
        if (!File.Exists(path))
        {
            CleanFolder(dir);
            log.Report($"Baixando 7-Zip {ver}...");
            await DownloadAsync($"https://www.7-zip.org/a/{name}", path, ct, pct);
        }
        return new FetchResult(AppId.SevenZip, "7-Zip", path, ver, "/qn /norestart", false);
    }

    // ---- AnyDesk: URL sempre-mais-recente; re-baixa se o servidor mudou ----
    async Task<FetchResult> AnyDeskAsync(IProgress<string> log, CancellationToken ct, IProgress<double>? pct)
    {
        var dir = FolderFor(AppId.AnyDesk);
        var path = Path.Combine(dir, "AnyDesk.exe");
        if (await RemoteChangedAsync("https://download.anydesk.com/AnyDesk.exe", path, ct))
        {
            log.Report("Baixando AnyDesk (mais recente)...");
            await DownloadAsync("https://download.anydesk.com/AnyDesk.exe", path, ct, pct);
        }
        return new FetchResult(AppId.AnyDesk, "AnyDesk", path, "mais recente",
            "--install \"C:\\Program Files (x86)\\AnyDesk\" --silent --create-shortcuts --create-desktop-icon --start-with-win", false);
    }

    // ---- Office Deployment Tool (setup.exe sempre-mais-recente) ----
    async Task<FetchResult> OfficeOdtAsync(IProgress<string> log, CancellationToken ct, IProgress<double>? pct)
    {
        var dir = FolderFor(AppId.OfficeOdt);
        var path = Path.Combine(dir, "setup.exe");
        var raw = Path.Combine(dir, "odt.exe");
        if (await RemoteChangedAsync("https://go.microsoft.com/fwlink/?linkid=2264705", raw, ct) || !File.Exists(path))
        {
            log.Report("Baixando Office Deployment Tool...");
            await DownloadAsync("https://go.microsoft.com/fwlink/?linkid=2264705", raw, ct, pct);
            var psi = new System.Diagnostics.ProcessStartInfo(raw, $"/quiet /extract:\"{dir}\"") { UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p != null) await p.WaitForExitAsync(ct);
            if (!File.Exists(path)) File.Copy(raw, path, true);
        }
        return new FetchResult(AppId.OfficeOdt, "Office 365 (ODT)", path, "mais recente", "", true);
    }

    // ---- Adobe Acrobat Reader (offline pt-BR x64) via API oficial ----
    async Task<FetchResult> AdobeAsync(IProgress<string> log, CancellationToken ct, IProgress<double>? pct)
    {
        var api = "https://rdc.adobe.io/reader/products?lang=pt&site=enterprise&os=Windows%2011&country=BR&nativeOs=Windows%2011&api_key=dc-get-adobereader-cdn";
        var json = await Http.GetStringAsync(api, ct);
        var m = Regex.Match(json, "\"version\"\\s*:\\s*\"([\\d.]+)\"");
        if (!m.Success) throw new InvalidOperationException("não consegui ler a versão do Adobe Reader na API.");
        var ver = m.Groups[1].Value;
        var build = ver.Replace(".", "");
        var name = $"AcroRdrDCx64{build}_pt_BR.exe";
        var dir = FolderFor(AppId.AdobeReader);
        var path = Path.Combine(dir, name);
        if (!File.Exists(path))
        {
            CleanFolder(dir);
            log.Report($"Baixando Adobe Reader {ver} (~700 MB)...");
            await DownloadAsync($"https://ardownload2.adobe.com/pub/adobe/acrobat/win/AcrobatDC/{build}/{name}", path, ct, pct);
        }
        return new FetchResult(AppId.AdobeReader, "Adobe Acrobat Reader", path, ver, "/sAll /rs /msi EULA_ACCEPT=YES", false);
    }

    // ---- FortiClient VPN (MSI, link direto da Datora) ----
    async Task<FetchResult> FortiClientAsync(IProgress<string> log, CancellationToken ct, IProgress<double>? pct)
    {
        var dir = FolderFor(AppId.FortiClient);
        var path = Path.Combine(dir, "FortiClientVPN.msi");
        if (await RemoteChangedAsync(FortiClientUrl, path, ct))
        {
            log.Report("Baixando FortiClient VPN...");
            await DownloadAsync(FortiClientUrl, path, ct, pct);
        }
        return new FetchResult(AppId.FortiClient, "FortiClient", path, "mais recente", "/qn /norestart", false);
    }

    // ------------------------------------------------------------------
    static void CleanFolder(string dir)
    {
        if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir)) { try { File.Delete(f); } catch { } }
        Directory.CreateDirectory(dir);
    }

    async Task<bool> RemoteChangedAsync(string url, string localPath, CancellationToken ct)
    {
        if (!File.Exists(localPath)) return true;
        var meta = localPath + ".meta";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await Http.SendAsync(req, ct);
            var tag = $"{resp.Content.Headers.LastModified?.ToString()}|{resp.Content.Headers.ContentLength}";
            var prev = File.Exists(meta) ? await File.ReadAllTextAsync(meta, ct) : "";
            if (tag == prev && tag.Length > 1) return false;
            await File.WriteAllTextAsync(meta, tag, ct);
            return true;
        }
        catch { return false; }
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
                    var pct = readTotal * 100.0 / total;
                    if (pct - last >= 1 || pct >= 100) { percent.Report(pct); last = pct; }
                }
            }
            percent?.Report(100);
        }
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(tmp, dest);
    }
}
