using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace IsoForge.Core;

public enum AppId { SevenZip, AnyDesk, OfficeOdt, AdobeReader, FortiClient, FortiClientLatest, Chrome, Firefox, NotepadPlus, VcRedist }

public record FetchResult(AppId Id, string Name, string? LocalPath, string Version, string SilentArgs, bool IsOffice, string? Error = null, bool RequiresInternet = false);

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
                AppId.FortiClientLatest => await FortiClientLatestAsync(log, ct, percent),
                AppId.Chrome => await ChromeAsync(log, ct, percent),
                AppId.Firefox => await FirefoxAsync(log, ct, percent),
                AppId.NotepadPlus => await NotepadPlusAsync(log, ct, percent),
                AppId.VcRedist => await VcRedistAsync(log, ct, percent),
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

    /// <summary>
    /// O MSI x64 na página do 7-Zip. SEM o prefixo <c>a/</c> de propósito.
    /// </summary>
    /// <remarks>
    /// A PÁGINA MUDOU DE FORMATO e a regex antiga (<c>a/7z(\d+)-x64\.msi</c>) passou a
    /// casar só a seção HISTÓRICA. Medido em 18/09/2026 em
    /// https://www.7-zip.org/download.html (HTTP 200, 19.945 bytes): a seção da versão
    /// ATUAL escreve a URL absoluta do GitHub —
    /// <c>href="https://github.com/ip7z/7zip/releases/download/26.03/7z2603-x64.msi"</c> —
    /// enquanto só as versões velhas (23.01, 19.00, 16.04, 9.20) ainda usam o href
    /// relativo <c>a/…</c>. A regex antiga achava 2301/1900/1604/920, escolhia o máximo
    /// 2301 e baixava o 7-Zip 23.01 (junho de 2023) SEM ERRO NENHUM — HEAD nele devolve
    /// 200 — enquanto a página anunciava "Download 7-Zip 26.03 (2026-09-03)". Sem o
    /// prefixo, a mesma regex casa os dois formatos: 5 capturas, máximo 2603.
    /// </remarks>
    internal static readonly Regex SeteZipMsiRegex = new(@"7z(\d+)-x64\.msi", RegexOptions.Compiled);

    /// <summary>
    /// Piso de sanidade. Se a página mudar de formato OUTRA VEZ e sobrar apenas a seção
    /// histórica, é melhor falhar com uma mensagem legível do que entregar em silêncio um
    /// 7-Zip de anos atrás. 2603 é o que a página servia em 18/09/2026.
    /// </summary>
    internal const int SeteZipBuildMinimo = 2603;

    internal static string VersaoSeteZip(int build) => $"{build / 100}.{build % 100:00}";

    /// <summary>Maior build de MSI x64 anunciado na página (lança se não achar ou se for velho demais).</summary>
    internal static int SeteZipBuildDaPagina(string html)
    {
        var vers = SeteZipMsiRegex.Matches(html).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        if (vers.Count == 0)
            throw new InvalidOperationException(
                "não achei nenhum MSI x64 na página do 7-Zip (https://www.7-zip.org/download.html) — o formato da página mudou.");
        var v = vers.Max();
        if (v < SeteZipBuildMinimo)
            throw new InvalidOperationException(
                $"a página do 7-Zip só ofereceu a versão {VersaoSeteZip(v)}, anterior à {VersaoSeteZip(SeteZipBuildMinimo)} "
                + "que já era publicada — o formato da página mudou e eu estaria baixando uma versão velha em silêncio.");
        return v;
    }

    async Task<FetchResult> SevenZipAsync(IProgress<string> log, CancellationToken ct, IProgress<double>? pct)
    {
        var page = await BaixarTextoAsync("https://www.7-zip.org/download.html", ct);
        var v = SeteZipBuildDaPagina(page);
        var name = $"7z{v}-x64.msi";
        var dir = FolderFor(AppId.SevenZip);
        var path = Path.Combine(dir, name);
        var ver = VersaoSeteZip(v);
        if (!File.Exists(path))
        {
            CleanFolder(dir);
            log.Report($"Baixando 7-Zip {ver} (mais recente anunciada na página)...");
            // O caminho /a/ continua valendo para a versão nova: medido, ele responde 302
            // para release-assets.githubusercontent.com e entrega 2.007.040 bytes que
            // começam com D0 CF 11 E0 A1 B1 1A E1 (MSI válido).
            await DownloadAsync($"https://www.7-zip.org/a/{name}", path, ct, pct);
        }
        return new FetchResult(AppId.SevenZip, "7-Zip", path, ver, "/qn /norestart", false);
    }

    // ---- AnyDesk: URL sempre-mais-recente; re-baixa se o servidor mudou ----
    async Task<FetchResult> AnyDeskAsync(IProgress<string> log, CancellationToken ct, IProgress<double>? pct)
    {
        var dir = FolderFor(AppId.AnyDesk);
        var path = Path.Combine(dir, "AnyDesk.exe");
        await BaixarSeMudouAsync("https://download.anydesk.com/AnyDesk.exe", path,
                                 "Baixando AnyDesk (mais recente)...", log, ct, pct);
        return new FetchResult(AppId.AnyDesk, "AnyDesk", path, "mais recente",
            "--install \"C:\\Program Files (x86)\\AnyDesk\" --silent --create-shortcuts --create-desktop-icon --start-with-win", false);
    }

    /// <summary>Versão de produto de um executável (null se não existir ou não tiver).</summary>
    static string? VersaoDe(string caminho)
    {
        try
        {
            if (!File.Exists(caminho)) return null;
            var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(caminho).FileVersion;
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch { return null; }
    }

    // ---- Office Deployment Tool (setup.exe sempre-mais-recente) ----
    async Task<FetchResult> OfficeOdtAsync(IProgress<string> log, CancellationToken ct, IProgress<double>? pct)
    {
        var dir = FolderFor(AppId.OfficeOdt);
        var path = Path.Combine(dir, "setup.exe");   // o ODT de verdade
        var raw = Path.Combine(dir, "odt.exe");      // o auto-extrator baixado

        // A EXTRACAO NUNCA ERA VERIFICADA e o setup.exe ficava travado numa versao velha
        // PARA SEMPRE. Medido na maquina do usuario: odt.exe = 16.0.20326.20144 (recem
        // baixado) e setup.exe = 16.0.20131.20112 — se a extracao tivesse funcionado os
        // dois teriam a mesma versao. O motivo: o codigo nao lia o exit code do extrator e
        // o unico resguardo era 'if (!File.Exists(path))', que nunca disparava porque ja
        // havia um setup.exe antigo ali; e como o RemoteChangedAsync ja tinha gravado o
        // .meta do download, o bloco inteiro nao rodava mais nas proximas vezes. Esse
        // mesmo binario velho ia para a fonte offline e para dentro da ISO.
        //
        // Agora: (1) re-extrai tambem quando as versoes divergem, o que conserta sozinho a
        // pasta ja estragada; (2) apaga o setup.exe ANTES; (3) confere o exit code;
        // (4) confere a versao depois. E NAO ha mais o fallback File.Copy(raw, path): o
        // auto-extrator NAO e o ODT — rodar 'odt.exe /configure x.xml' nao instala Office
        // nenhum, so re-extrai. Embarcar isso na ISO em silencio e pior que falhar aqui.
        // A MICROSOFT TROCOU O QUE ESSE LINK SERVE. Ele entregava um auto-extrator que,
        // com /quiet /extract:DIR, cuspia o setup.exe do ODT. Hoje entrega o PROPRIO
        // setup.exe do ODT (7,07 MB). Medido nesta maquina: rodar o arquivo baixado com
        // /quiet /extract:DIR imprime a AJUDA do ODT ("Office Deployment Tool / Setup
        // [mode] [path]"), sai com codigo 0 e nao cria arquivo nenhum. O codigo antigo
        // lia exit 0, nao achava o setup.exe e concluia "a extracao falhou" — e a
        // interface traduzia isso para "Verifique a internet", com a internet perfeita.
        //
        // As duas formas passam a ser aceitas, e as duas sao VERIFICADAS: o ODT de verdade
        // se identifica no recurso de versao com OriginalFilename=Bootstrapper.exe. Isso
        // preserva a razao do resguardo anterior (nunca embarcar na ISO um binario que nao
        // e o ODT) sem depender de um formato de distribuicao que a Microsoft mudou.
        //
        // O download do raw agora é do BaixarSeMudouAsync: quando o remoto muda, ele
        // baixa de verdade. Antes, o "if (!File.Exists(raw) || VersaoDe(raw) == null)"
        // pulava o download sempre que já houvesse um odt.exe legível — e como o .meta
        // tinha acabado de ser gravado, a mudança nunca mais era notada.
        var baixou = await BaixarSeMudouAsync("https://go.microsoft.com/fwlink/?linkid=2264705", raw,
                                              "Baixando Office Deployment Tool...", log, ct, pct);
        var precisaObter = baixou
                           || !File.Exists(path)
                           || !EhOdt(path)
                           || VersaoDe(path) != VersaoDe(raw);
        if (precisaObter)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* segue: a copia sobrescreve */ }

            if (EhOdt(raw))
            {
                // Forma atual: o arquivo baixado JA e o ODT.
                log.Report("O arquivo baixado ja e o Office Deployment Tool; sem extracao.");
                File.Copy(raw, path, overwrite: true);
            }
            else
            {
                // Forma antiga: auto-extrator.
                log.Report("Extraindo o Office Deployment Tool...");
                var psi = new System.Diagnostics.ProcessStartInfo(raw, $"/quiet /extract:\"{dir}\"") { UseShellExecute = false, CreateNoWindow = true };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) throw new InvalidOperationException("Não consegui executar o extrator do Office Deployment Tool.");
                await p.WaitForExitAsync(ct);
                if (p.ExitCode != 0)
                    throw new InvalidOperationException($"A extração do Office Deployment Tool falhou (código {p.ExitCode}). "
                        + $"Apague a pasta {dir} e tente de novo.");
                if (!File.Exists(path))
                    throw new InvalidOperationException($"A extração do Office Deployment Tool não produziu o setup.exe em {dir}.");
            }

            // Vale para as duas formas: o que vai para a ISO tem de SER o ODT.
            if (!EhOdt(path))
                throw new InvalidOperationException(
                    $"O arquivo obtido em {path} não é o Office Deployment Tool "
                    + $"(esperado OriginalFilename=Bootstrapper.exe, veio \"{IdentidadeDe(path) ?? "?"}\"). "
                    + "A Microsoft pode ter mudado o pacote de distribuição.");

            var vSetup = VersaoDe(path);
            var vRaw = VersaoDe(raw);
            if (vSetup != null && vRaw != null && vSetup != vRaw)
                throw new InvalidOperationException(
                    $"O setup.exe obtido ({vSetup}) não bate com o Office Deployment Tool baixado ({vRaw}). "
                    + $"Apague a pasta {dir} e tente de novo — um ODT mais antigo que o payload não instala o Office offline.");
            log.Report($"Office Deployment Tool pronto (versão {vSetup ?? "?"}).");
        }
        return new FetchResult(AppId.OfficeOdt, "Office 365 (ODT)", path, VersaoDe(path) ?? "mais recente", "", true);
    }

    /// <summary>
    /// O <c>OriginalFilename</c> do recurso de versão. É como o ODT se identifica:
    /// <c>Bootstrapper.exe</c>, tanto no arquivo que a Microsoft serve hoje quanto no
    /// setup.exe que o auto-extrator antigo produzia.
    /// </summary>
    internal static string? IdentidadeDe(string caminho)
    {
        try { return System.Diagnostics.FileVersionInfo.GetVersionInfo(caminho).OriginalFilename; }
        catch { return null; }
    }

    /// <summary>
    /// O arquivo É o Office Deployment Tool? Verificado pelo recurso de versão, não pelo
    /// tamanho nem pelo nome — os dois já mudaram.
    /// </summary>
    internal static bool EhOdt(string caminho) =>
        File.Exists(caminho)
        && string.Equals(IdentidadeDe(caminho), "Bootstrapper.exe", StringComparison.OrdinalIgnoreCase);

    // ---- Adobe Acrobat Reader (offline pt-BR x64) via API oficial ----
    async Task<FetchResult> AdobeAsync(IProgress<string> log, CancellationToken ct, IProgress<double>? pct)
    {
        var api = "https://rdc.adobe.io/reader/products?lang=pt&site=enterprise&os=Windows%2011&country=BR&nativeOs=Windows%2011&api_key=dc-get-adobereader-cdn";
        var json = await BaixarTextoAsync(api, ct);
        var m = Regex.Match(json, "\"version\"\\s*:\\s*\"([\\d.]+)\"");
        if (!m.Success)
            throw new InvalidOperationException($"não consegui ler a versão do Adobe Reader na API ({api}). Veio: {Trecho(json)}");
        var ver = m.Groups[1].Value;
        var build = ver.Replace(".", "");
        var name = $"AcroRdrDCx64{build}_pt_BR.exe";
        var dir = FolderFor(AppId.AdobeReader);
        var path = Path.Combine(dir, name);
        var url = $"https://ardownload2.adobe.com/pub/adobe/acrobat/win/AcrobatDC/{build}/{name}";

        // A API IGNORA lang/os/country. Medido em 18/09/2026: com lang=pt&country=BR ela
        // responde 159 bytes cujo displayName é "Reader 2026.002.21901 English for Windows
        // (32 Bit)" e fileSize 614 — o pacote INGLÊS 32 bits. O código só aproveita o
        // campo "version", e hoje escapa porque a Adobe publica o mesmo build em todos os
        // idiomas e arquiteturas (HEAD na URL pt-BR x64 = 200, 788.152.912 bytes, MZ
        // válido). Se ela escalonar o lançamento, essa URL dá 404 — e 404 anônimo era
        // exatamente o que a tela traduzia como "verifique a internet". Por isso o HEAD
        // antes de baixar: a falha passa a dizer build, idioma, arquitetura e endereço.
        var displayName = Regex.Match(json, "\"displayName\"\\s*:\\s*\"([^\"]*)\"") is { Success: true } d
            ? d.Groups[1].Value : "(sem displayName)";
        if (!File.Exists(path))
        {
            long? tamanho;
            using (var req = new HttpRequestMessage(HttpMethod.Head, url))
            using (var resp = await Http.SendAsync(req, ct))
            {
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"a Adobe não publicou o build {ver} em pt-BR x64 nesse endereço — "
                        + MensagemHttp((int)resp.StatusCode, resp.ReasonPhrase, url)
                        + $". A API anunciou \"{displayName}\", que pode estar à frente do pacote pt-BR x64.");
                tamanho = resp.Content.Headers.ContentLength;
            }
            CleanFolder(dir);
            // O tamanho real, não o "~700 MB" chutado: medido 788.152.912 bytes (751,6 MB).
            log.Report($"API da Adobe anunciou \"{displayName}\"; baixando o build {ver} em pt-BR x64 ({EmMb(tamanho)})...");
            await DownloadAsync(url, path, ct, pct);
        }
        return new FetchResult(AppId.AdobeReader, "Adobe Acrobat Reader", path, ver, "/sAll /rs /msi EULA_ACCEPT=YES", false);
    }

    // ---- FortiClient VPN 7.4.1 (MSI offline, espelho da Datora) ----
    async Task<FetchResult> FortiClientAsync(IProgress<string> log, CancellationToken ct, IProgress<double>? pct)
    {
        var dir = FolderFor(AppId.FortiClient);
        var path = Path.Combine(dir, "FortiClientVPN.msi");
        // A fonte aqui NÃO é da Fortinet: é um blob de terceiro (tidatora.blob.core.windows.net).
        // Pode sumir ou virar página de erro sem aviso — daí a checagem de conteúdo no
        // DownloadAsync e o log quando o HEAD falha.
        await BaixarSeMudouAsync(FortiClientUrl, path, "Baixando FortiClient VPN 7.4.1 (offline)...", log, ct, pct);
        return new FetchResult(AppId.FortiClient, "FortiClient", path, "7.4.1", "/qn /norestart", false);
    }

    // ---- FortiClient VPN (mais recente, direto do repositório oficial da Fortinet) ----
    // links.fortinet.com/forticlient/win/vpnagent -> FortiClientVPNInstaller.exe (online installer).
    async Task<FetchResult> FortiClientLatestAsync(IProgress<string> log, CancellationToken ct, IProgress<double>? pct)
    {
        const string url = "https://links.fortinet.com/forticlient/win/vpnagent";
        var dir = FolderFor(AppId.FortiClientLatest);
        var path = Path.Combine(dir, "FortiClientVPNInstaller.exe");
        await BaixarSeMudouAsync(url, path, "Baixando FortiClient VPN (mais recente, oficial Fortinet)...", log, ct, pct);
        // Online installer: baixa o cliente da Fortinet durante a instalação -> exige internet no 1º logon.
        return new FetchResult(AppId.FortiClientLatest, "FortiClient", path, "mais recente", "/quiet", false, RequiresInternet: true);
    }

    // ---- Google Chrome (Enterprise MSI oficial, URL sempre-mais-recente) ----
    async Task<FetchResult> ChromeAsync(IProgress<string> log, CancellationToken ct, IProgress<double>? pct)
    {
        const string url = "https://dl.google.com/tag/s/dl/chrome/install/googlechromestandaloneenterprise64.msi";
        var dir = FolderFor(AppId.Chrome);
        var path = Path.Combine(dir, "GoogleChromeEnterprise64.msi");
        await BaixarSeMudouAsync(url, path, "Baixando Google Chrome (mais recente)...", log, ct, pct);
        return new FetchResult(AppId.Chrome, "Google Chrome", path, "mais recente", "/qn /norestart", false);
    }

    // ---- Mozilla Firefox (MSI oficial pt-BR, URL sempre-mais-recente) ----
    async Task<FetchResult> FirefoxAsync(IProgress<string> log, CancellationToken ct, IProgress<double>? pct)
    {
        const string url = "https://download.mozilla.org/?product=firefox-msi-latest-ssl&os=win64&lang=pt-BR";
        var dir = FolderFor(AppId.Firefox);
        var path = Path.Combine(dir, "FirefoxSetup.msi");
        await BaixarSeMudouAsync(url, path, "Baixando Mozilla Firefox (mais recente)...", log, ct, pct);
        return new FetchResult(AppId.Firefox, "Mozilla Firefox", path, "mais recente", "/qn /norestart", false);
    }

    // ---- Notepad++ (última release oficial no GitHub, instalador x64) ----
    async Task<FetchResult> NotepadPlusAsync(IProgress<string> log, CancellationToken ct, IProgress<double>? pct)
    {
        const string apiGh = "https://api.github.com/repos/notepad-plus-plus/notepad-plus-plus/releases/latest";
        var json = await BaixarTextoAsync(apiGh, ct);
        var ver = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([\\d.]+)\"") is { Success: true } t ? t.Groups[1].Value : "mais recente";
        var asset = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"(https://[^\"]*?Installer\\.x64\\.exe)\"");
        // Diz QUAL release foi lida e o que se procurava: se o Notepad++ renomear o asset
        // (hoje "npp.<versão>.Installer.x64.exe"), a falha tem de apontar para o nome, não
        // virar um "verifique a internet".
        if (!asset.Success)
            throw new InvalidOperationException(
                $"a release {ver} do Notepad++ não tem nenhum asset terminado em \"Installer.x64.exe\" ({apiGh}) — "
                + "o nome do instalador mudou.");
        var url = asset.Groups[1].Value;
        var dir = FolderFor(AppId.NotepadPlus);
        var name = Path.GetFileName(new Uri(url).LocalPath);
        var path = Path.Combine(dir, name);
        if (!File.Exists(path))
        {
            CleanFolder(dir);
            log.Report($"Baixando Notepad++ {ver}...");
            await DownloadAsync(url, path, ct, pct);
        }
        return new FetchResult(AppId.NotepadPlus, "Notepad++", path, ver, "/S", false);
    }

    // ---- Visual C++ 2015-2022 Redistributable x64 (oficial Microsoft, sempre-mais-recente) ----
    async Task<FetchResult> VcRedistAsync(IProgress<string> log, CancellationToken ct, IProgress<double>? pct)
    {
        const string url = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
        var dir = FolderFor(AppId.VcRedist);
        var path = Path.Combine(dir, "vc_redist.x64.exe");
        await BaixarSeMudouAsync(url, path, "Baixando Visual C++ Redistributable (mais recente)...", log, ct, pct);
        return new FetchResult(AppId.VcRedist, "Visual C++ 2015-2022 (x64)", path, "mais recente", "/install /quiet /norestart", false);
    }

    // ------------------------------------------------------------------
    static void CleanFolder(string dir)
    {
        if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir)) { try { File.Delete(f); } catch { } }
        Directory.CreateDirectory(dir);
    }

    // ---------------- frescor: HEAD, tag e o .meta ----------------
    //
    // O .meta é o ÚNICO sinal de que a cópia local está atualizada. Ele era gravado ANTES
    // do download (dentro do antigo RemoteChangedAsync), e isso tem duas consequências
    // medidas: (1) um download interrompido deixava o arquivo VELHO no disco com a tag
    // NOVA ao lado — na execução seguinte "nada mudou", nada era baixado e o instalador
    // antigo ia para a ISO como se fosse o mais recente, para sempre e em silêncio;
    // (2) no cache real do usuário os carimbos denunciavam a ordem invertida
    // (Chrome\...msi.meta às 17:40:05 e o .msi às 17:40:27; OfficeOdt\odt.exe.meta
    // reescrito um DIA depois do odt.exe). Agora a tag só é gravada depois do File.Move.

    internal static string CaminhoMeta(string destino) => destino + ".meta";

    /// <summary>
    /// A decisão "preciso baixar?" isolada do HTTP, para poder ser testada sem rede.
    /// Sem cópia local, baixa. Tag vazia ("|", servidor sem Last-Modified nem
    /// Content-Length), baixa — rebaixar é melhor do que servir um arquivo velho achando
    /// que é o novo.
    /// </summary>
    internal static bool MudouPelaTag(bool existeLocal, string tagRemota, string tagLocal)
        => !existeLocal || tagRemota != tagLocal || tagRemota.Length <= 1;

    /// <summary>
    /// A tag (<c>Last-Modified|Content-Length</c>) do remoto quando ele mudou;
    /// <c>null</c> quando a cópia local já está atualizada.
    /// </summary>
    async Task<string?> TagSeMudouAsync(string url, string localPath, IProgress<string> log, CancellationToken ct)
    {
        string tag;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await Http.SendAsync(req, ct);
            tag = $"{resp.Content.Headers.LastModified?.ToString()}|{resp.Content.Headers.ContentLength}";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // ERA 'catch { return false; }', MUDO. Qualquer falha de HEAD (DNS, proxy,
            // URL morta) virava "não mudou" e a tela dizia "{app} pronto" servindo o
            // cache. Continuar usando o cache é a decisão certa; o que faltava era DIZER.
            log.Report($"Não consegui checar atualização em {url}: {ex.Message}");
            if (!File.Exists(localPath)) return "";   // sem cache não há o que reaproveitar: tenta baixar e o GET explica
            log.Report($"Usando a cópia local de {Path.GetFileName(localPath)} ({File.GetLastWriteTime(localPath):dd/MM/yyyy HH:mm}).");
            return null;
        }
        var meta = CaminhoMeta(localPath);
        var prev = File.Exists(meta) ? await File.ReadAllTextAsync(meta, ct) : "";
        return MudouPelaTag(File.Exists(localPath), tag, prev) ? tag : null;
    }

    /// <summary>Grava a tag SÓ DEPOIS de o arquivo inteiro estar no lugar.</summary>
    static async Task ConfirmarTagAsync(string destino, string tag, CancellationToken ct)
    {
        try { await File.WriteAllTextAsync(CaminhoMeta(destino), tag, ct); }
        catch { /* sem .meta a próxima execução rebaixa: falha segura */ }
    }

    /// <summary>Baixa quando o remoto mudou (e só então confirma a tag). Devolve se baixou.</summary>
    async Task<bool> BaixarSeMudouAsync(string url, string destino, string mensagem,
                                        IProgress<string> log, CancellationToken ct, IProgress<double>? pct)
    {
        var tag = await TagSeMudouAsync(url, destino, log, ct);
        if (tag == null) return false;
        log.Report(mensagem);
        await DownloadAsync(url, destino, ct, pct);
        await ConfirmarTagAsync(destino, tag, ct);
        return true;
    }

    // ---------------- o que chegou é mesmo um instalador? ----------------

    internal static string MensagemHttp(int status, string? motivo, string url) =>
        $"HTTP {status}{(string.IsNullOrWhiteSpace(motivo) ? "" : " " + motivo)} em {url}";

    internal static string EmMb(long? bytes) =>
        bytes is > 0 ? $"{bytes.Value / 1024.0 / 1024.0:N1} MB" : "tamanho não informado";

    internal static string Trecho(string texto, int max = 200) =>
        texto.Length <= max ? texto : texto[..max] + "…";

    static readonly byte[] MagicoOle = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    internal static byte[] PrimeirosBytes(string caminho, int n = 8)
    {
        using var fs = File.OpenRead(caminho);
        var buf = new byte[n];
        var lidos = fs.Read(buf, 0, n);
        return buf[..lidos];
    }

    static string Hex(byte[] b) => string.Join(" ", b.Select(x => x.ToString("X2")));

    /// <summary>
    /// Confere que os bytes baixados são do tipo que a extensão promete: <c>.exe</c> tem
    /// de começar com <c>MZ</c> e <c>.msi</c> com a assinatura OLE. Vale para os 10
    /// instaladores porque a checagem é do FORMATO, não do fornecedor (medido no cache do
    /// usuário: os MSIs não têm recurso de versão e o AnyDesk.exe não tem
    /// OriginalFilename — exigir identidade de fabricante reprovaria arquivo bom).
    /// </summary>
    /// <remarks>
    /// É o buraco por onde o Office passou, na forma geral: HTTP 200 era aceito como
    /// prova de que o arquivo certo chegou. Se a fonte passar a devolver 200 com HTML
    /// (portal de consentimento de CDN, página de erro, redirecionamento para landing
    /// page), o HTML era salvo como .exe/.msi e a ISO saía com um instalador quebrado,
    /// sem nenhum erro.
    /// </remarks>
    internal static void ConferirConteudo(string arquivo, string extensao, string url)
    {
        var inicio = PrimeirosBytes(arquivo);
        var tamanho = new FileInfo(arquivo).Length;
        bool ok = extensao.ToLowerInvariant() switch
        {
            ".exe" => inicio.Length >= 2 && inicio[0] == 0x4D && inicio[1] == 0x5A,           // MZ
            ".msi" => inicio.Length >= 8 && inicio[..8].SequenceEqual(MagicoOle),             // composto OLE
            _ => true
        };
        if (ok) return;
        var texto = System.Text.Encoding.ASCII.GetString(inicio).TrimStart().ToLowerInvariant();
        var pista = texto.StartsWith("<!do") || texto.StartsWith("<htm") || texto.StartsWith("<?xm") || texto.StartsWith("{")
            ? " — parece uma página HTML/JSON (erro do servidor, portal de consentimento ou redirecionamento), não o instalador"
            : "";
        throw new InvalidOperationException(
            $"o que chegou de {url} não é um {extensao} válido: {tamanho:N0} bytes começando com {Hex(inicio)}{pista}.");
    }

    async Task DownloadAsync(string url, string dest, CancellationToken ct, IProgress<double>? percent)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        // O .meta descreve o arquivo que ESTÁ no disco. Some antes de mexer no destino
        // para que uma queda no meio do download não deixe o par (arquivo velho + tag
        // nova), que congelava o instalador antigo como "atual".
        try { File.Delete(CaminhoMeta(dest)); } catch { }
        var tmp = dest + ".part";
        using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            // EnsureSuccessStatusCode diz apenas "status code does not indicate success:
            // 404 (Not Found)" — sem a URL. Com 10 fontes e vários redirecionamentos, a
            // mensagem tem de dizer ONDE falhou (medido: a URL de um build inexistente do
            // Adobe Reader devolve 404 de verdade, e essa frase anônima era tudo o que
            // sobrava para o usuário).
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException(MensagemHttp((int)resp.StatusCode, resp.ReasonPhrase,
                    (resp.RequestMessage?.RequestUri ?? new Uri(url)).ToString()));
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
        // Confere ANTES de virar o arquivo bom: um .part reprovado é apagado e o que já
        // estava no disco continua intacto.
        try { ConferirConteudo(tmp, Path.GetExtension(dest), url); }
        catch { try { File.Delete(tmp); } catch { } throw; }
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(tmp, dest);
    }

    /// <summary>
    /// GET de texto (HTML/JSON) com o motivo legível. <c>GetStringAsync</c> lança
    /// "response status code does not indicate success" sem dizer QUAL endereço falhou —
    /// e no GitHub o caso comum é o 403 por limite de chamadas (medido: a resposta da
    /// api.github.com veio com x-ratelimit-remaining 56 de 60 por hora, sem autenticação),
    /// que sem isso chega ao usuário como se fosse falta de internet.
    /// </summary>
    async Task<string> BaixarTextoAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var extra = "";
            if ((int)resp.StatusCode == 403 && resp.Headers.TryGetValues("x-ratelimit-remaining", out var r) && r.FirstOrDefault() == "0")
                extra = " — a API do GitHub permite só 60 chamadas por hora sem autenticação e o limite foi atingido; tente de novo mais tarde.";
            throw new HttpRequestException(MensagemHttp((int)resp.StatusCode, resp.ReasonPhrase, url) + extra);
        }
        return await resp.Content.ReadAsStringAsync(ct);
    }
}
