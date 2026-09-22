using System.Diagnostics;
using System.IO;
using System.Text;
using IsoForge.Models;

namespace IsoForge.Core;

/// <summary>
/// Pipeline completo: monta a ISO de origem, extrai o conteúdo para uma pasta
/// de trabalho, injeta autounattend.xml + $OEM$ (apps e scripts) e recompila
/// uma ISO bootável (UEFI + BIOS) com o oscdimg.
/// </summary>
public class IsoPipeline
{
    /// <summary>
    /// Quem extrai os icones dos instaladores para a tela de progresso. E um ponto
    /// de extensao, e nao uma chamada direta, por um motivo concreto: a extracao usa
    /// as APIs de imagem do WPF (ver AppIconExtractor), e o projeto de teste compila
    /// estes mesmos fontes SEM WPF — ligar UseWPF nele derrubaria os implicit usings
    /// e deixaria Path ambiguo com System.Windows.Shapes.Path em todo o arquivo.
    ///
    /// A aplicacao liga isto na abertura; sem ninguem ligado, a ISO sai sem PNG e a
    /// tela usa o glifo de reserva — que e degradacao aceitavel, nao falha.
    /// </summary>
    public static Func<BuildConfig, string, int>? ExtratorDeIcones { get; set; }

    readonly IProgress<string> _log;
    readonly IProgress<int>? _pct;

    public IsoPipeline(IProgress<string> log, IProgress<int>? percent = null)
    {
        _log = log;
        _pct = percent;
    }

    void Log(string msg) => _log.Report(msg);
    void Pct(int p) => _pct?.Report(p);

    // ------------------------------------------------------------------
    // Build completo
    // ------------------------------------------------------------------
    /// <summary>
    /// Grava num pendrive uma ISO que JÁ existe.
    ///
    /// É o caminho de quem acabou de gerar a imagem e quer a mídia agora: monta a ISO,
    /// copia o conteúdo para o pendrive formatado e parte o install.wim se preciso. Não
    /// refaz o provisionamento — seriam outros 20 minutos para produzir exatamente os
    /// mesmos arquivos que já estão dentro do .iso.
    /// </summary>
    public async Task GravarIsoEmPendriveAsync(string isoPath, UsbDisco alvo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(isoPath) || !File.Exists(isoPath))
            throw new InvalidOperationException($"A ISO não foi encontrada em {isoPath}.");

        var (podeGravar, motivo) = await UsbWriter.Conferir(alvo, ct);
        if (!podeGravar) throw new InvalidOperationException(motivo!);

        Log($"Gravando {Path.GetFileName(isoPath)} em {alvo.Rotulo}...");
        Pct(5);

        var (drive, rotulo) = await MountAsync(isoPath, ct);
        try
        {
            Log($"ISO montada em {drive}: (rótulo: {rotulo})");
            Pct(15);
            // A origem é a raiz da unidade montada: o conteúdo da ISO já é a árvore final.
            await new UsbWriter(Log, Pct).GravarDeAsync(alvo, drive + ":\\", rotulo, ct);
        }
        finally
        {
            await DismountAsync(isoPath);
            Log("ISO desmontada.");
        }

        Pct(100);
        Log("");
        Log($"✔ Pendrive pronto: {alvo.Rotulo}");
    }

    /// <summary>
    /// Grava o Windows personalizado direto num pendrive, sem passar por arquivo .iso.
    ///
    /// Faz exatamente o mesmo caminho do <see cref="BuildAsync"/> — monta a ISO de
    /// origem, extrai, injeta o Setup — e troca só o último passo: em vez de o oscdimg
    /// empacotar a árvore num .iso, o <see cref="UsbWriter"/> formata o pendrive e copia
    /// a árvore nele. Para quem vai instalar numa máquina, a ISO era um intermediário
    /// que custava o tempo do oscdimg e alguns GB de disco sem servir para nada.
    /// </summary>
    public async Task BuildToUsbAsync(BuildConfig cfg, UsbDisco alvo, CancellationToken ct)
    {
        // Sem ISO de saída: a validação de caminho/gravação do .iso não se aplica aqui.
        var paraUsb = cfg.Clone();
        paraUsb.OutputIsoPath = "";
        Validate(paraUsb, dryRun: true);
        if (string.IsNullOrWhiteSpace(cfg.SourceIsoPath) || !File.Exists(cfg.SourceIsoPath))
            throw new InvalidOperationException("Selecione a ISO de origem do Windows.");

        var (podeGravar, motivo) = await UsbWriter.Conferir(alvo, ct);
        if (!podeGravar) throw new InvalidOperationException(motivo!);

        var root = Path.Combine(Path.GetTempPath(), "IsoForge");
        CleanStaleWorkFolders(root);
        var staging = Path.Combine(root, $"usb_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(staging);
        Log($"Pasta de trabalho: {staging}");
        Pct(3);

        var sucesso = false;
        try
        {
            string label;
            var (drive, rotuloIso) = await MountAsync(cfg.SourceIsoPath, ct);
            try
            {
                label = rotuloIso;
                Log($"ISO montada em {drive}: (rótulo: {label})");
                Pct(10);
                await ExtractAsync(drive, staging, ct);
                Pct(50);
            }
            finally
            {
                await DismountAsync(cfg.SourceIsoPath);
                Log("ISO de origem desmontada.");
            }

            ClearReadOnly(new DirectoryInfo(staging));
            if (cfg.UseCapturedWim) SwapInstallWim(cfg.CapturedWimPath, staging);
            Pct(58);
            InjectFiles(cfg, staging);
            Pct(70);

            await new UsbWriter(Log, Pct).GravarAsync(alvo, staging, label, ct);
            sucesso = true;

            Log("");
            Log($"✔ Pendrive pronto: {alvo.Rotulo}");
            Log("  Arranque por UEFI (o Windows 11 não instala em BIOS legado de qualquer forma).");
        }
        finally
        {
            // O 100% ficava ANTES daqui. A limpeza apaga a árvore de trabalho inteira — 5 a
            // 8 GB, com reset de atributos e várias tentativas — e leva de segundos a
            // minutos. A pessoa lia "pronto", via 100%, e o programa parava de responder.
            // Cem por cento é quando não há mais nada a fazer.
            Log("Limpando os arquivos temporários...");
            ForceDeleteDirectory(staging);
            if (sucesso) Pct(100);
        }
    }

    public async Task BuildAsync(BuildConfig cfg, CancellationToken ct)
    {
        Validate(cfg, dryRun: false);

        var root = Path.Combine(Path.GetTempPath(), "IsoForge");
        // Remove pastas de trabalho de gerações anteriores que não puderam ser apagadas
        // (ex.: antivírus segurou um arquivo). Recupera o espaço acumulado.
        CleanStaleWorkFolders(root);

        var staging = Path.Combine(root, $"work_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(staging);
        Log($"Pasta de trabalho: {staging}");
        Pct(3);

        try
        {
            var (drive, label) = await MountAsync(cfg.SourceIsoPath, ct);
            try
            {
                Log($"ISO montada em {drive}: (rótulo: {label})");
                Pct(10);
                await ExtractAsync(drive, staging, ct);
                Pct(50);
            }
            finally
            {
                await DismountAsync(cfg.SourceIsoPath);
                Log("ISO de origem desmontada.");
            }

            ClearReadOnly(new DirectoryInfo(staging));
            if (cfg.UseCapturedWim)
                SwapInstallWim(cfg.CapturedWimPath, staging);
            Pct(58);
            InjectFiles(cfg, staging);
            Pct(68);
            await BuildIsoAsync(cfg, staging, label, ct);
            Pct(100);

            Log("");
            Log($"✔ ISO gerada com sucesso: {cfg.OutputIsoPath}");
            Log($"  Tamanho: {new FileInfo(cfg.OutputIsoPath).Length / 1024.0 / 1024.0 / 1024.0:F2} GB");
        }
        finally
        {
            Log("Limpando pasta de trabalho...");
            ForceDeleteDirectory(staging);
        }
    }

    // ------------------------------------------------------------------
    // Limpeza robusta da pasta de trabalho (evita acúmulo de GBs no Temp)
    // ------------------------------------------------------------------

    /// <summary>
    /// Remove pastas de trabalho de gerações anteriores que ficaram presas.
    ///
    /// Os DOIS prefixos, e isso não é detalhe: a geração de ISO cria work_*, mas a
    /// gravação em pendrive cria usb_*. Enquanto só o work_* era varrido, uma gravação
    /// interrompida deixava de 5 a 8 GB no %TEMP% — para sempre, porque nada mais
    /// olhava para aquela pasta.
    /// </summary>
    void CleanStaleWorkFolders(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.EnumerateDirectories(root, "work_*")
                              .Concat(Directory.EnumerateDirectories(root, "usb_*")))
            {
                Log($"Removendo pasta de trabalho antiga: {Path.GetFileName(d)}");
                ForceDeleteDirectory(d);
            }
        }
        catch { /* melhor esforço */ }
    }

    /// <summary>
    /// Apaga uma árvore de arquivos de forma resiliente: limpa atributos (somente-leitura,
    /// oculto, sistema), tenta várias vezes (arquivo travado por antivírus solta em segundos)
    /// e, por último, recorre ao "rd /s /q". Não lança exceção.
    /// </summary>
    void ForceDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;

        for (int attempt = 1; attempt <= 5; attempt++)
        {
            ResetAttributes(path);
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch when (attempt < 5)
            {
                System.Threading.Thread.Sleep(800); // deixa o antivírus/indexador soltar o arquivo
            }
            catch (Exception ex)
            {
                Log($"Aviso: limpeza direta falhou ({ex.Message}). Tentando 'rd /s /q'...");
            }
        }

        // Último recurso: apagar via shell.
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c rd /s /q \"{path}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(30000);
        }
        catch { /* melhor esforço */ }

        if (Directory.Exists(path))
            Log($"Aviso: sobraram arquivos em {path} (algum programa ainda os segura). Serão removidos na próxima geração.");
    }

    /// <summary>Zera atributos de arquivos e pastas para permitir a exclusão.</summary>
    static void ResetAttributes(string path)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        }
        catch { }
        try
        {
            foreach (var d in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
                try { new DirectoryInfo(d).Attributes = FileAttributes.Directory; } catch { }
        }
        catch { }
    }

    // ------------------------------------------------------------------
    // Dry-run: gera apenas os arquivos (autounattend + pasta Setup) para inspeção
    // ------------------------------------------------------------------
    public void DryRun(BuildConfig cfg, string outputFolder)
    {
        Validate(cfg, dryRun: true);
        Directory.CreateDirectory(outputFolder);
        InjectFiles(cfg, outputFolder);

        // Teste no Windows Sandbox: roda o install.cmd em uma máquina descartável
        var setupDir = Path.Combine(outputFolder, "sources", "$OEM$", "$1", "Setup");
        var wsbPath = Path.Combine(outputFolder, "Testar-Sandbox.wsb");
        LimparResiduoDeTeste(setupDir);
        File.WriteAllText(wsbPath, TestScripts.SandboxWsb(setupDir), new UTF8Encoding(false));

        Log("");
        Log($"✔ Arquivos gerados em: {outputFolder}");
        Log("  - autounattend.xml (raiz da ISO)");
        Log(@"  - sources\$OEM$\$1\Setup (vira C:\Setup no Windows instalado)");
        Log("  - Testar-Sandbox.wsb (teste descartável dos instaladores)");
        Log("");
        Log("TESTAR SEM FORMATAR NADA: dê dois cliques em Testar-Sandbox.wsb —");
        Log("abre o Windows Sandbox (máquina descartável) e roda o install.cmd real.");
        Log("Ao fechar o Sandbox, tudo é apagado; o install.log fica na pasta gerada.");
        Log("Se o Sandbox não estiver habilitado (PowerShell admin + reiniciar):");
        Log("  Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All");
    }

    // ------------------------------------------------------------------
    // Teste no Windows Sandbox: prepara o payload e devolve o caminho do .wsb.
    // Roda o install.cmd (apps + Office + aparência + FortiClient) numa cópia
    // descartável do Windows — igual ao que aconteceria na máquina real, sem risco.
    // ------------------------------------------------------------------
    public string PrepareSandbox(BuildConfig cfg, string outputFolder)
    {
        // Para o teste, força conta local e método "1º logon" (nada de auditoria/sysprep).
        // A seleção de unidade CONTINUA aparecendo (você valida a tela); só o reboot final
        // é pulado (SandboxTest), pois o Windows Sandbox não reinicia.
        var test = cfg.Clone();
        test.Mode = DeploymentMode.LocalAccount;
        test.UnitMethod = UnitSelectionMethod.FirstLogon;
        test.SandboxTest = true;

        Validate(test, dryRun: true);
        Directory.CreateDirectory(outputFolder);
        InjectFiles(test, outputFolder);

        var setupDir = Path.Combine(outputFolder, "sources", "$OEM$", "$1", "Setup");
        var wsbPath = Path.Combine(outputFolder, "Testar-Sandbox.wsb");

        // Teste de Office OFFLINE roda SEM rede — senão ele não testa nada: com internet
        // disponível, um pacote local incompleto simplesmente é completado pelo
        // download e o teste passa mentindo. A exceção é haver algum programa que
        // declaradamente precisa de internet; aí a rede fica, e o log diz isso.
        var precisaRede = InstallScriptGenerator.AnyNeedsInternet(test);
        var comRede = !test.OfficeOffline || precisaRede;
        if (!comRede)
            Log("Teste no Sandbox SEM rede: é a única forma de provar que o Office offline instala sem internet.");
        else if (test.OfficeOffline)
            Log("Teste no Sandbox COM rede (algum programa da lista exige internet) — atenção: isso NÃO prova que o Office offline dispensa conexão.");

        LimparResiduoDeTeste(setupDir);
        File.WriteAllText(wsbPath, TestScripts.SandboxWsb(setupDir, comRede), new UTF8Encoding(false));
        return wsbPath;
    }

    /// <summary>
    /// Apaga o resíduo da rodada ANTERIOR do teste no Sandbox.
    ///
    /// Esta pasta é mapeada dentro do Sandbox como C:\Setup com ReadOnly=false, então
    /// tudo que o install.cmd escreve em C:\Setup volta para o HOST. O marcador
    /// install.done — que versões anteriores gravavam em C:\Setup e nada apagava —
    /// fazia o script sair na terceira linha da 2ª rodada em diante: o Office "não
    /// funcionava de jeito nenhum" sem o ODT ter sido chamado nenhuma vez, e o log só
    /// ganhava uma linha. O marcador mudou para %ProgramData% (ver
    /// InstallScriptGenerator.DoneMarker); isto aqui limpa o que ficou das ISOs antigas
    /// e zera o install.log para o teste começar do zero.
    /// </summary>
    static void LimparResiduoDeTeste(string setupDir)
    {
        foreach (var residuo in new[] { "install.done", "install.log" })
        {
            var p = Path.Combine(setupDir, residuo);
            try { if (File.Exists(p)) File.Delete(p); } catch { /* melhor esforço */ }
        }
    }

    /// <summary>Windows Sandbox está disponível nesta máquina?</summary>
    public static bool IsSandboxAvailable()
        => File.Exists(Path.Combine(Environment.SystemDirectory, "WindowsSandbox.exe"));

    // ------------------------------------------------------------------
    // Validação
    // ------------------------------------------------------------------
    // internal (era private) para a suite exercitar os portões de configuração — em
    // especial o do idioma do Office — sem gerar uma ISO de 5 GB.
    internal void Validate(BuildConfig cfg, bool dryRun)
    {
        if (!dryRun)
        {
            if (string.IsNullOrWhiteSpace(cfg.SourceIsoPath) || !File.Exists(cfg.SourceIsoPath))
                throw new InvalidOperationException("Selecione a ISO de origem do Windows 11.");
            if (string.IsNullOrWhiteSpace(cfg.OutputIsoPath))
                throw new InvalidOperationException("Informe onde salvar a ISO personalizada.");
            if (Path.GetFullPath(cfg.SourceIsoPath).Equals(Path.GetFullPath(cfg.OutputIsoPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A ISO de saída não pode ser o mesmo arquivo da ISO de origem.");
            if (string.IsNullOrWhiteSpace(cfg.OscdimgPath) || !File.Exists(cfg.OscdimgPath))
                throw new InvalidOperationException(Oscdimg.InstallHint);
            if (cfg.UseCapturedWim && (string.IsNullOrWhiteSpace(cfg.CapturedWimPath) || !File.Exists(cfg.CapturedWimPath)))
                throw new InvalidOperationException("Selecione o install.wim capturado (imagem golden).");
        }

        if (cfg.OfficeOffline && cfg.Apps.Any(a => a.Kind == AppKind.Office))
        {
            if (string.IsNullOrWhiteSpace(cfg.OfficeSourceFolder) || !Directory.Exists(cfg.OfficeSourceFolder))
                throw new InvalidOperationException("Office offline ativado: selecione a pasta com o Office baixado (setup.exe + Office\\Data) ou clique em Baixar Office.");
            // Não basta a pasta existir: precisa conter Office\Data com arquivos, senão o
            // ODT baixa da internet no 1º logon (o erro 'we weren't able to download a required file').
            var dataDir = Path.Combine(cfg.OfficeSourceFolder, "Office", "Data");
            if (!Directory.Exists(dataDir) || !Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories).Any())
                throw new InvalidOperationException(
                    "Office offline: a pasta selecionada não contém 'Office\\Data' com os arquivos do Office.\n\n" +
                    "Clique em \"Baixar Office...\" para baixar a fonte offline completa (~3,5 GB) ANTES de gerar a ISO, " +
                    "ou aponte uma pasta que já tenha 'setup.exe' + 'Office\\Data'. Sem isso o Office tenta baixar da " +
                    "internet na máquina de destino e falha se não houver conexão.");
        }

        if (cfg.UseCustomUnattend)
        {
            if (string.IsNullOrWhiteSpace(cfg.CustomUnattendPath) || !File.Exists(cfg.CustomUnattendPath))
                throw new InvalidOperationException("Selecione o arquivo autounattend.xml personalizado.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(cfg.UserName))
                throw new InvalidOperationException("Informe o nome do usuário local.");
            if (cfg.UserName.Trim().Equals("Administrator", StringComparison.OrdinalIgnoreCase) ||
                cfg.UserName.Trim().Equals("Administrador", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Use outro nome de usuário: 'Administrador' é uma conta interna do Windows.");
        }

        foreach (var app in cfg.Apps)
        {
            if (string.IsNullOrWhiteSpace(app.InstallerPath))
                throw new InvalidOperationException($"O aplicativo \"{app.Name}\" está sem instalador selecionado. Selecione o arquivo ou remova a linha.");
            if (!File.Exists(app.InstallerPath))
                throw new InvalidOperationException($"Instalador não encontrado: {app.InstallerPath}");
        }

        // Fonte offline do Office: checada AQUI, nao depois de 20 minutos gerando a
        // ISO. Uma fonte incompleta era embutida em silencio e o ODT ia buscar no CDN
        // no 1o logon -> "we weren't able to download a required file" numa maquina
        // recem-instalada, que e justamente onde nao ha rede configurada.
        if (cfg.OfficeOffline && cfg.Apps.Any(a => a.Kind == AppKind.Office))
        {
            var fonte = OfficeSource.Validar(cfg.OfficeSourceFolder);
            if (!fonte.ok)
                throw new InvalidOperationException("Office offline: " + fonte.problema);

            // O XML e o payload tem de concordar sobre o IDIOMA. Foi uma discordancia
            // dessas — o XML exigindo en-us (culpa do <RemoveMSI/>) numa fonte so pt-br —
            // que fez o ODT falhar com 1603 em ~70 s dentro do Sandbox sem rede:
            //   FullCultureReachableValidator: "Current culture is unreachable","Culture":"en-us"
            // O RemoveMSI ja saiu, mas o mesmo estado se produz trocando o idioma na tela
            // depois de baixar. Barrado aqui, na maquina que gera, e nao no cliente.
            var semCultura = OfficeSource.CulturasFaltando(
                cfg.OfficeSourceFolder, OfficeConfig.Idiomas(cfg.OfficeConfigXml));
            if (semCultura.Count > 0)
                throw new InvalidOperationException(
                    "Office offline: o Configuration.xml pede o(s) idioma(s) "
                    + string.Join(", ", semCultura) + ", que NÃO está(ão) na fonte baixada.\n\n"
                    + "O Click-to-Run exige os cabs da cultura (i64<LCID>.cab / s64<LCID>.cab) e, sem "
                    + "eles e sem internet, falha com o erro 1603 logo no começo — sem instalar nada.\n\n"
                    + "Baixe o Office de novo com esse idioma selecionado, ou volte o idioma do XML "
                    + "para o que foi baixado.");
        }
    }

    // ------------------------------------------------------------------
    // Montagem / extração da ISO
    // ------------------------------------------------------------------
    async Task<(char Drive, string Label)> MountAsync(string isoPath, CancellationToken ct)
    {
        Log("Montando a ISO de origem...");
        var script =
            $"$img = Mount-DiskImage -ImagePath '{isoPath.Replace("'", "''")}' -PassThru; " +
            "$vol = $img | Get-Volume; " +
            "Write-Output ($vol.DriveLetter.ToString() + '|' + $vol.FileSystemLabel)";

        var output = await RunProcessAsync("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"", ct, captureOnly: true);

        var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Contains('|'));
        if (line == null || line.Length < 2)
            throw new InvalidOperationException($"Falha ao montar a ISO. Saída: {output}");

        var parts = line.Split('|', 2);
        return (parts[0][0], parts.Length > 1 ? parts[1] : "CCCOMA_X64FRE_PT-BR_DV9");
    }

    async Task DismountAsync(string isoPath)
    {
        var script = $"Dismount-DiskImage -ImagePath '{isoPath.Replace("'", "''")}' | Out-Null";
        try
        {
            await RunProcessAsync("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"", CancellationToken.None, captureOnly: true);
        }
        catch { /* melhor esforço */ }
    }

    async Task ExtractAsync(char drive, string staging, CancellationToken ct)
    {
        Log("Extraindo conteúdo da ISO (isso pode levar alguns minutos)...");
        // robocopy retorna 0-7 em sucesso; >= 8 é erro
        var exit = await RunProcessRawAsync("robocopy.exe",
            $"{drive}:\\ \"{staging}\" /E /R:2 /W:2 /NFL /NDL /NJH /NP", ct);
        if (exit >= 8)
            throw new InvalidOperationException($"Falha ao copiar arquivos da ISO (robocopy código {exit}).");
        Log("Extração concluída.");
    }

    static void ClearReadOnly(DirectoryInfo dir)
    {
        foreach (var file in dir.GetFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;
    }

    /// <summary>Versão de produto de um .exe (null se o arquivo não existir/não tiver).</summary>
    internal static string? VersaoDoArquivo(string caminho)
    {
        try
        {
            if (!File.Exists(caminho)) return null;
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(caminho).FileVersion;
        }
        catch { return null; }
    }

    /// <summary>
    /// Dos dois executáveis, devolve o de versão MAIOR. Na dúvida (versão ilegível),
    /// fica com o segundo — que é o ODT gerenciado pelo IsoForge, o que a ferramenta
    /// mantém atualizado.
    /// </summary>
    internal static string MaisNovo(string a, string b)
    {
        if (!File.Exists(a)) return b;
        if (!File.Exists(b)) return a;
        var va = VersaoDoArquivo(a);
        var vb = VersaoDoArquivo(b);
        if (Version.TryParse(va, out var pa) && Version.TryParse(vb, out var pb))
            return pa > pb ? a : b;
        return b;
    }

    static void CopyDirectory(DirectoryInfo source, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in source.GetFiles())
            file.CopyTo(Path.Combine(destDir, file.Name), overwrite: true);
        foreach (var sub in source.GetDirectories())
            CopyDirectory(sub, Path.Combine(destDir, sub.Name));
    }

    /// <summary>
    /// Imagem golden: substitui o sources\install.wim (ou install.esd) da ISO extraída
    /// pelo WIM capturado, que já contém o Windows com todos os apps pré-instalados.
    /// </summary>
    void SwapInstallWim(string capturedWim, string staging)
    {
        var sources = Path.Combine(staging, "sources");
        if (!Directory.Exists(sources))
            throw new InvalidOperationException("Pasta 'sources' não encontrada na ISO extraída.");

        var esd = Path.Combine(sources, "install.esd");
        if (File.Exists(esd))
        {
            File.Delete(esd);
            Log("install.esd original removido.");
        }

        var target = Path.Combine(sources, "install.wim");
        File.Copy(capturedWim, target, overwrite: true);
        var gb = new FileInfo(target).Length / 1024.0 / 1024.0 / 1024.0;
        Log($"Imagem golden: install.wim substituído pelo capturado ({gb:F2} GB) — Windows já vem com tudo instalado.");
    }

    // ------------------------------------------------------------------
    // Injeção de autounattend.xml + $OEM$
    // ------------------------------------------------------------------
    void InjectFiles(BuildConfig cfg, string staging)
    {
        // 1. autounattend.xml na raiz
        var unattendPath = Path.Combine(staging, "autounattend.xml");
        if (cfg.UseCustomUnattend)
        {
            File.Copy(cfg.CustomUnattendPath, unattendPath, overwrite: true);
            Log($"autounattend.xml personalizado copiado de: {cfg.CustomUnattendPath}");
            Log(@"Atenção: para os aplicativos instalarem, seu XML precisa executar C:\Setup\install.cmd no primeiro logon (FirstLogonCommands).");
        }
        else
        {
            UnattendGenerator.WriteTo(cfg, unattendPath);
            Log("autounattend.xml gerado (usuário local, idioma, OOBE, primeiro logon).");
        }

        // 1b. Seleção automática de disco: script na raiz da ISO, executado no WinPE.
        if (cfg.AutoSelectDisk && !cfg.GoldenReference)
        {
            File.WriteAllText(Path.Combine(staging, DiskPrepGenerator.FileName),
                DiskPrepGenerator.Generate(), new UTF8Encoding(false));
            Log("Seleção automática de disco: IsoForgeDiskPrep.cmd na raiz (nunca o pendrive).");
        }

        // 2. sources\$OEM$\$1\Setup  →  C:\Setup no Windows instalado
        var setupDir = Path.Combine(staging, "sources", "$OEM$", "$1", "Setup");
        var appsDir = Path.Combine(setupDir, "Apps");
        Directory.CreateDirectory(appsDir);

        foreach (var app in cfg.Apps)
        {
            if (app.Kind == AppKind.Office)
            {
                var officeDir = Path.Combine(appsDir, "Office");
                Directory.CreateDirectory(officeDir);

                if (cfg.OfficeOffline && !string.IsNullOrWhiteSpace(cfg.OfficeSourceFolder) && Directory.Exists(cfg.OfficeSourceFolder))
                {
                    // Copia SO o payload (a subpasta Office), nao a pasta escolhida.
                    // O usuario aponta a pasta que recebeu o download, que na pratica e
                    // Downloads: medido num caso real, isso levava 12,55 GB e 118 arquivos
                    // alheios para dentro da ISO, contra 3,64 GB de Office. Alem do peso,
                    // e vazamento — tudo que estiver na pasta viaja na imagem.
                    var origemOffice = OfficeSource.Resolver(cfg.OfficeSourceFolder)
                        ?? throw new InvalidOperationException(
                            $"Nao encontrei Office\\Data em {cfg.OfficeSourceFolder}.");

                    Log("Office offline: copiando a fonte local (pode levar alguns minutos)...");
                    CopyDirectory(new DirectoryInfo(origemOffice), Path.Combine(officeDir, "Office"));

                    // O setup.exe que vai na ISO e o MAIS NOVO entre o da pasta do usuario e
                    // o que o IsoForge mantem atualizado — antes daqui saia cegamente o da
                    // pasta do usuario. Medido: C:\...\Downloads\setup.exe = 16.0.20131.20112
                    // contra o odt.exe gerenciado = 16.0.20326.20144, e o payload a instalar
                    // era 16.0.20326.20132. Ou seja, a ISO levava um bootstrapper anterior ao
                    // proprio payload (a orientacao da Microsoft e sempre usar o ODT mais novo
                    // que o payload), com tabela LKG e tratamento offline antigos.
                    var setupOrigem = Path.Combine(cfg.OfficeSourceFolder, "setup.exe");
                    var odtEscolhido = MaisNovo(setupOrigem, app.InstallerPath);
                    File.Copy(odtEscolhido, Path.Combine(officeDir, "setup.exe"), overwrite: true);
                    var versaoOdt = VersaoDoArquivo(odtEscolhido);
                    Log($"Office offline: ODT {versaoOdt ?? "?"} ({Path.GetFileName(odtEscolhido)}) embutido.");

                    // Fixa a versao baixada para o ODT NAO consultar o CDN, e desliga o
                    // Updates: com ele ligado o Click-to-Run vai ao CDN buscar versao mais
                    // nova durante o /configure, que e justamente o que quebra o offline.
                    var version = OfficeConfig.DetectVersion(officeDir);

                    // O bootstrapper TEM de ser igual ou mais novo que o payload que ele
                    // instala (orientacao da Microsoft: use sempre o ODT mais recente).
                    // Medido no ciclo anterior: a ISO saiu com o ODT 16.0.20131.20112 para um
                    // payload 16.0.20326.20132 — duas releases atras — e ninguem foi avisado,
                    // porque o MaisNovo so escolhe entre os dois que existem na maquina. Nao
                    // foi a causa daquela falha (o payload instalou com ele), mas e um estado
                    // que ninguem escolheria de propriedade: ao menos precisa aparecer no log.
                    if (Version.TryParse(versaoOdt, out var vOdt) && Version.TryParse(version, out var vPayload)
                        && vOdt < vPayload)
                        Log($"Atenção: o ODT embutido ({versaoOdt}) é MAIS ANTIGO que o payload ({version}). "
                            + "Isso costuma funcionar, mas a orientação da Microsoft é usar o ODT mais recente. "
                            + "Clique em \"Baixar Office...\" para renovar o setup.exe da pasta da fonte.");
                    // Duas pastas de versao em Office\Data e o que um /download retomado
                    // deixa. Nao e fatal desde que a escolhida esteja completa (o XML e a
                    // checagem previa agora usam a MESMA funcao, OfficeSource.VersaoMaisNova),
                    // mas o usuario precisa saber que ha lixo de outro dia na fonte.
                    var qtdVersoes = OfficeSource.ContarVersoes(Path.Combine(officeDir, "Office", "Data"));
                    if (qtdVersoes > 1)
                        Log($"Atenção: a fonte do Office tem {qtdVersoes} pastas de versão (download retomado). "
                            + $"Usando a mais nova ({version}); se o Office falhar no destino, refaça o download numa pasta limpa.");
                    var xml = OfficeConfig.ForOfflineInstall(
                        cfg.OfficeConfigXml, InstallScriptGenerator.AppsDirOnDisk + "\\Office", version);
                    File.WriteAllText(Path.Combine(officeDir, "Configuration.xml"), xml, new UTF8Encoding(false));

                    var dataSize = new DirectoryInfo(officeDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                    Log(version != null
                        ? $"Office offline incluido ({dataSize / 1024.0 / 1024.0 / 1024.0:F2} GB, versao {version} fixada, Updates desligado) - instala sem internet no 1o logon."
                        : $"Office offline incluido ({dataSize / 1024.0 / 1024.0 / 1024.0:F2} GB) - AVISO: nao detectei a versao em Office\\Data; o ODT pode tentar o CDN.");
                }
                else
                {
                    File.Copy(app.InstallerPath, Path.Combine(officeDir, "setup.exe"), overwrite: true);
                    // Desatendido tambem no modo online: a caixa modal do ODT trava o
                    // install.cmd do mesmo jeito, com ou sem fonte local.
                    File.WriteAllText(Path.Combine(officeDir, "Configuration.xml"),
                        OfficeConfig.ForOnlineInstall(cfg.OfficeConfigXml), new UTF8Encoding(false));
                    Log("Office 365 (online): setup.exe (ODT) + Configuration.xml incluídos — baixa da internet no 1º logon.");
                }
            }
            else
            {
                var dest = Path.Combine(appsDir, Path.GetFileName(app.InstallerPath));
                File.Copy(app.InstallerPath, dest, overwrite: true);
                Log($"{app.Name}: {Path.GetFileName(app.InstallerPath)} incluído.");
            }
        }

        // 3. Aparência: papel de parede + tela de bloqueio
        if (ExtraScriptsGenerator.HasAppearance(cfg))
        {
            string? wpName = null, lockName = null;
            if (!string.IsNullOrWhiteSpace(cfg.WallpaperPath))
            {
                wpName = Path.GetFileName(cfg.WallpaperPath);
                File.Copy(cfg.WallpaperPath, Path.Combine(setupDir, wpName), overwrite: true);
                Log($"Papel de parede incluído: {wpName}");
            }
            if (!string.IsNullOrWhiteSpace(cfg.LockScreenPath))
            {
                lockName = "lockscreen" + Path.GetExtension(cfg.LockScreenPath);
                File.Copy(cfg.LockScreenPath, Path.Combine(setupDir, lockName), overwrite: true);
                Log($"Tela de bloqueio incluída: {Path.GetFileName(cfg.LockScreenPath)}");
            }
            File.WriteAllText(Path.Combine(setupDir, ExtraScriptsGenerator.AppearanceFileName),
                ExtraScriptsGenerator.Appearance(wpName, lockName, cfg.WindowsTheme, cfg.TaskbarAlign), new UTF8Encoding(true)); // BOM p/ PowerShell 5.1
        }

        // 4. FortiClient VPN
        if (ExtraScriptsGenerator.HasFortiConfig(cfg))
        {
            if (!string.IsNullOrWhiteSpace(cfg.FortiClientRegImportPath) && File.Exists(cfg.FortiClientRegImportPath))
            {
                File.Copy(cfg.FortiClientRegImportPath, Path.Combine(setupDir, ExtraScriptsGenerator.FortiRegImportName), overwrite: true);
            }
            else if (cfg.VpnTunnels.Count > 0 && cfg.VpnUseTextImport)
            {
                // Túneis digitados (import por texto EXPERIMENTAL ligado): XML importado pelo FCConfig.
                File.WriteAllText(Path.Combine(setupDir, ExtraScriptsGenerator.FortiVpnXmlName),
                    ExtraScriptsGenerator.FortiClientVpnXml(cfg), new UTF8Encoding(false));
            }
            File.WriteAllText(Path.Combine(setupDir, ExtraScriptsGenerator.FortiFileName),
                ExtraScriptsGenerator.FortiClient(cfg), new UTF8Encoding(true)); // BOM p/ PowerShell 5.1
            Log($"FortiClient VPN: {cfg.VpnTunnels.Count} túnel(is) IPsec configurado(s).");
        }

        // 4b. Wi-Fi automático: grava o perfil WLAN (SSID + senha) para conectar via netsh no 1º logon.
        if (ExtraScriptsGenerator.HasWifi(cfg))
        {
            File.WriteAllText(Path.Combine(setupDir, ExtraScriptsGenerator.WifiProfileFileName),
                ExtraScriptsGenerator.WifiProfileXml(cfg.WifiSsid, cfg.WifiPassword), new UTF8Encoding(false));
            Log($"Wi-Fi automático: perfil WLAN gerado para a rede \"{cfg.WifiSsid}\".");
        }

        // 4c. Espera por internet: se algum app precisa de rede, inclui o WaitForInternet.ps1.
        if (InstallScriptGenerator.AnyNeedsInternet(cfg))
        {
            File.WriteAllText(Path.Combine(setupDir, ExtraScriptsGenerator.WaitForInternetFileName),
                ExtraScriptsGenerator.WaitForInternet(), new UTF8Encoding(true)); // BOM p/ PowerShell 5.1
            Log("Instalação com apps que precisam de internet: WaitForInternet.ps1 incluído (espera conexão e continua sozinho).");
        }

        // 4c-2. Otimização (debloat) e relatório de provisionamento.
        if (DebloatGenerator.Has(cfg))
        {
            File.WriteAllText(Path.Combine(setupDir, DebloatGenerator.FileName),
                DebloatGenerator.Generate(cfg), new UTF8Encoding(true)); // BOM p/ PowerShell 5.1
            Log("Otimização (debloat) incluída.");
        }
        if (cfg.GenerateReport)
        {
            File.WriteAllText(Path.Combine(setupDir, ReportGenerator.FileName),
                ReportGenerator.Generate(cfg), new UTF8Encoding(true)); // BOM p/ PowerShell 5.1
            Log("Relatório de provisionamento incluído (HTML na área de trabalho).");
        }

        // 4d. Drivers do fabricante: copia os .inf para sources\$OEM$\$1\Drivers (=> C:\Drivers no
        // destino). O autounattend (offlineServicing) + pnputil no 1º logon fazem a injeção.
        if (!string.IsNullOrWhiteSpace(cfg.DriverPackPath) && Directory.Exists(cfg.DriverPackPath))
        {
            var driversDest = Path.Combine(staging, "sources", "$OEM$", "$1", "Drivers");
            var excluded = new HashSet<string>(cfg.DriverExcludedCategories, StringComparer.OrdinalIgnoreCase);
            var quais = excluded.Count == 0 ? "todos os componentes" : $"exceto: {string.Join(", ", excluded)}";
            Log($"Copiando drivers do fabricante ({cfg.DriverModelName}) — {quais}...");
            long bytes = DriverInfScanner.CopySelected(cfg.DriverPackPath, excluded, driversDest);
            Log($"Drivers incluídos ({cfg.DriverModelName}): {bytes / 1024 / 1024} MB → C:\\Drivers no 1º boot.");
        }

        // 5. Seleção de unidade (1º logon sem auditoria, ou modo de auditoria)
        if (cfg.UseUnitSelection)
        {
            UnitSelectorGenerator.WriteTo(cfg, Path.Combine(setupDir, UnitSelectorGenerator.FileName));
            var metodo = cfg.UnitMethod == Models.UnitSelectionMethod.Audit ? "modo auditoria" : "1º logon, sem auditoria";
            Log($"Seleção de unidade ({metodo}): {cfg.Units.Count} unidade(s) — tela WPF gerada.");
        }

        // 5b. Tela CHEIA de progresso (icone do programa + barra + tempo restante)
        if (cfg.FullscreenProgress && cfg.Apps.Any(a => !string.IsNullOrWhiteSpace(a.InstallerPath)))
        {
            ProgressUiGenerator.WriteTo(cfg, Path.Combine(setupDir, ProgressUiGenerator.FileName));

            var iconDir = Path.Combine(appsDir, ProgressUiGenerator.IconsSubFolder);
            var icones = ExtratorDeIcones?.Invoke(cfg, iconDir) ?? 0;
            Log($"Tela de progresso em tela cheia gerada ({icones} icone(s) extraido(s) dos instaladores).");
        }

        // 6. Script personalizado
        if (!string.IsNullOrWhiteSpace(cfg.PostScriptPath))
        {
            File.Copy(cfg.PostScriptPath, Path.Combine(setupDir, Path.GetFileName(cfg.PostScriptPath)), overwrite: true);
            Log($"Script personalizado incluído: {Path.GetFileName(cfg.PostScriptPath)}");
        }

        // install.cmd / golden.cmd / SetupComplete.cmd conforme o modo
        if (cfg.GoldenReference)
        {
            InstallScriptGenerator.WriteTo(cfg, Path.Combine(setupDir, "install.cmd"));
            // O lancador sem janela acompanha o install.cmd: o autounattend so o chama
            // quando ha tela cheia, mas grava-lo sempre evita uma ISO que aponta para um
            // arquivo inexistente se a opcao for ligada depois num perfil salvo.
            File.WriteAllText(Path.Combine(setupDir, InstallScriptGenerator.LauncherFileName),
                InstallScriptGenerator.Launcher(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(setupDir, InstallScriptGenerator.ShellStubFileName),
                InstallScriptGenerator.ShellStub(), new UTF8Encoding(false));
            InstallScriptGenerator.WriteGoldenTo(cfg, Path.Combine(setupDir, "golden.cmd"));
            Log("golden.cmd gerado (instala tudo no modo de auditoria e faz sysprep).");
        }
        else if (cfg.Mode == DeploymentMode.EntraId)
        {
            // SetupComplete.cmd roda como SYSTEM ao fim da instalação (antes do OOBE):
            // vai em sources\$OEM$\$$\Setup\Scripts (=> C:\Windows\Setup\Scripts).
            var scriptsDir = Path.Combine(staging, "sources", "$OEM$", "$$", "Setup", "Scripts");
            Directory.CreateDirectory(scriptsDir);
            File.WriteAllText(Path.Combine(scriptsDir, SetupCompleteGenerator.SetupCompleteFileName),
                SetupCompleteGenerator.SetupComplete(cfg), new UTF8Encoding(false));

            // Scripts auxiliares em C:\Setup (usuário local + remoção do admin Entra).
            File.WriteAllText(Path.Combine(setupDir, SetupCompleteGenerator.CreateUserFileName),
                SetupCompleteGenerator.CreateUser(cfg), new UTF8Encoding(true)); // BOM p/ PowerShell 5.1
            if (cfg.DemoteEntraJoiner)
            {
                File.WriteAllText(Path.Combine(setupDir, SetupCompleteGenerator.DemoteFileName),
                    SetupCompleteGenerator.Demote(), new UTF8Encoding(true)); // BOM p/ PowerShell 5.1
                File.WriteAllText(Path.Combine(setupDir, SetupCompleteGenerator.RegisterTaskFileName),
                    SetupCompleteGenerator.RegisterDemoteTask(), new UTF8Encoding(true)); // BOM p/ PowerShell 5.1
            }

            // Seleção de unidade no modo Entra ID: a tela (SelectUnit.ps1, já gravada acima) roda
            // como usuário padrão e grava a escolha; estas duas rodam via tarefa agendada no 1º logon.
            if (cfg.UseUnitSelection)
            {
                File.WriteAllText(Path.Combine(setupDir, SetupCompleteGenerator.RegisterUnitTasksFileName),
                    SetupCompleteGenerator.RegisterUnitTasks(), new UTF8Encoding(true)); // BOM p/ PowerShell 5.1
                File.WriteAllText(Path.Combine(setupDir, SetupCompleteGenerator.RenameUnitFileName),
                    SetupCompleteGenerator.RenameUnit(), new UTF8Encoding(true)); // BOM p/ PowerShell 5.1
            }
            Log("Modo Entra ID: SetupComplete.cmd gerado (cria usuário local + instala apps como SYSTEM; login corporativo/estudante no 1º boot).");
        }
        else
        {
            InstallScriptGenerator.WriteTo(cfg, Path.Combine(setupDir, "install.cmd"));
            // O lancador sem janela acompanha o install.cmd: o autounattend so o chama
            // quando ha tela cheia, mas grava-lo sempre evita uma ISO que aponta para um
            // arquivo inexistente se a opcao for ligada depois num perfil salvo.
            File.WriteAllText(Path.Combine(setupDir, InstallScriptGenerator.LauncherFileName),
                InstallScriptGenerator.Launcher(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(setupDir, InstallScriptGenerator.ShellStubFileName),
                InstallScriptGenerator.ShellStub(), new UTF8Encoding(false));
            Log("install.cmd gerado (instalação silenciosa no primeiro logon).");
        }
    }

    // ------------------------------------------------------------------
    // Recompilação da ISO com oscdimg
    // ------------------------------------------------------------------
    async Task BuildIsoAsync(BuildConfig cfg, string staging, string label, CancellationToken ct)
    {
        var etfsboot = Path.Combine(staging, "boot", "etfsboot.com");
        var efisys = Path.Combine(staging, "efi", "microsoft", "boot",
            cfg.NoPromptBoot ? "efisys_noprompt.bin" : "efisys.bin");

        if (!File.Exists(efisys) && cfg.NoPromptBoot)
        {
            Log("Aviso: efisys_noprompt.bin não existe nesta ISO; usando efisys.bin padrão.");
            efisys = Path.Combine(staging, "efi", "microsoft", "boot", "efisys.bin");
        }
        if (!File.Exists(etfsboot) || !File.Exists(efisys))
            throw new InvalidOperationException("Arquivos de boot não encontrados na ISO extraída. Essa é mesmo uma ISO oficial do Windows?");

        Log("Recompilando ISO bootável (UEFI + BIOS) com oscdimg...");
        var safeLabel = string.IsNullOrWhiteSpace(label) ? "WIN11_CUSTOM" : label;
        var args =
            // Sem "-o", de proposito. O -o faz o oscdimg calcular MD5 de TODO arquivo para
            // gravar duplicatas uma vez so. Medido no perfil de uma ISO do Windows (718 MB,
            // 901 arquivos): 3,7 s com -o contra 1,4 s sem (2,6x), e a ISO saiu do MESMO
            // tamanho -- numa imagem do Windows quase nao ha duplicatas, entao era hash
            // puro por nada.
            $"-m -u2 -udfver102 -l{safeLabel} " +
            $"-bootdata:2#p0,e,b\"{etfsboot}\"#pEF,e,b\"{efisys}\" " +
            $"\"{staging}\" \"{cfg.OutputIsoPath}\"";

        var exit = await RunProcessRawAsync(cfg.OscdimgPath, args, ct);
        if (exit != 0)
            throw new InvalidOperationException($"oscdimg falhou com código {exit}.");
    }

    // ------------------------------------------------------------------
    // Utilitários de processo
    // ------------------------------------------------------------------
    async Task<string> RunProcessAsync(string fileName, string arguments, CancellationToken ct, bool captureOnly)
    {
        var sb = new StringBuilder();
        var exit = await RunProcessCoreAsync(fileName, arguments, ct, line =>
        {
            sb.AppendLine(line);
            if (!captureOnly) Log("  " + line);
        });
        if (exit != 0)
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} falhou (código {exit}): {sb}");
        return sb.ToString();
    }

    async Task<int> RunProcessRawAsync(string fileName, string arguments, CancellationToken ct)
        => await RunProcessCoreAsync(fileName, arguments, ct, line =>
        {
            if (!string.IsNullOrWhiteSpace(line)) Log("  " + line.Trim());
        });

    static async Task<int> RunProcessCoreAsync(string fileName, string arguments, CancellationToken ct, Action<string> onLine)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };

        if (!process.Start())
            throw new InvalidOperationException($"Não foi possível iniciar {fileName}.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Cancelar precisa MATAR o filho, e a árvore dele.
        //
        // WaitForExitAsync(ct) devolve quando o token é cancelado, mas o processo continua
        // rodando: o robocopy seguiria gravando no pendrive depois de a pessoa mandar
        // parar, e o dism seguiria escrevendo .swm. Como ninguém mais o observa, ele vira
        // um processo órfão mexendo num disco que o programa já deu por encerrado.
        //
        // entireProcessTree porque o que se lança aqui costuma ter filhos — o powershell
        // que chama format, o dism que dispara seus próprios auxiliares.
        using var matar = ct.Register(static estado =>
        {
            try { ((Process)estado!).Kill(entireProcessTree: true); } catch { /* já morreu */ }
        }, process);

        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }
}
