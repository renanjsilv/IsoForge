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
    readonly IProgress<string> _log;

    public IsoPipeline(IProgress<string> log) => _log = log;

    void Log(string msg) => _log.Report(msg);

    // ------------------------------------------------------------------
    // Build completo
    // ------------------------------------------------------------------
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

        try
        {
            var (drive, label) = await MountAsync(cfg.SourceIsoPath, ct);
            try
            {
                Log($"ISO montada em {drive}: (rótulo: {label})");
                await ExtractAsync(drive, staging, ct);
            }
            finally
            {
                await DismountAsync(cfg.SourceIsoPath);
                Log("ISO de origem desmontada.");
            }

            ClearReadOnly(new DirectoryInfo(staging));
            if (cfg.UseCapturedWim)
                SwapInstallWim(cfg.CapturedWimPath, staging);
            InjectFiles(cfg, staging);
            await BuildIsoAsync(cfg, staging, label, ct);

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

    /// <summary>Remove pastas work_* de gerações anteriores que ficaram presas.</summary>
    void CleanStaleWorkFolders(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.EnumerateDirectories(root, "work_*"))
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
        File.WriteAllText(wsbPath, TestScripts.SandboxWsb(setupDir), new UTF8Encoding(false));
        return wsbPath;
    }

    /// <summary>Windows Sandbox está disponível nesta máquina?</summary>
    public static bool IsSandboxAvailable()
        => File.Exists(Path.Combine(Environment.SystemDirectory, "WindowsSandbox.exe"));

    // ------------------------------------------------------------------
    // Validação
    // ------------------------------------------------------------------
    void Validate(BuildConfig cfg, bool dryRun)
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
                    // Copia a fonte offline completa (setup.exe + Office\Data) e aponta o SourcePath local.
                    Log("Office offline: copiando fonte local (pode levar alguns minutos)...");
                    CopyDirectory(new DirectoryInfo(cfg.OfficeSourceFolder), officeDir);
                    if (!File.Exists(Path.Combine(officeDir, "setup.exe")))
                        File.Copy(app.InstallerPath, Path.Combine(officeDir, "setup.exe"), overwrite: true);
                    // Fixa a versão baixada para o ODT NÃO consultar o CDN (senão baixa mesmo com fonte local).
                    var version = OfficeConfig.DetectVersion(officeDir);
                    var xml = OfficeConfig.WithSourcePath(cfg.OfficeConfigXml, InstallScriptGenerator.AppsDirOnDisk + "\\Office", version);
                    File.WriteAllText(Path.Combine(officeDir, "Configuration.xml"), xml, new UTF8Encoding(false));
                    var dataSize = new DirectoryInfo(officeDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                    Log(version != null
                        ? $"Office offline incluído ({dataSize / 1024.0 / 1024.0 / 1024.0:F2} GB, versão {version} fixada) — instala sem internet no 1º logon."
                        : $"Office offline incluído ({dataSize / 1024.0 / 1024.0 / 1024.0:F2} GB) — AVISO: não detectei a versão em Office\\Data; o ODT pode tentar o CDN.");
                }
                else
                {
                    File.Copy(app.InstallerPath, Path.Combine(officeDir, "setup.exe"), overwrite: true);
                    File.WriteAllText(Path.Combine(officeDir, "Configuration.xml"), cfg.OfficeConfigXml, new UTF8Encoding(false));
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
                ExtraScriptsGenerator.Appearance(wpName, lockName), new UTF8Encoding(true)); // BOM p/ PowerShell 5.1
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

        // 5. Seleção de unidade (1º logon sem auditoria, ou modo de auditoria)
        if (cfg.UseUnitSelection)
        {
            UnitSelectorGenerator.WriteTo(cfg, Path.Combine(setupDir, UnitSelectorGenerator.FileName));
            var metodo = cfg.UnitMethod == Models.UnitSelectionMethod.Audit ? "modo auditoria" : "1º logon, sem auditoria";
            Log($"Seleção de unidade ({metodo}): {cfg.Units.Count} unidade(s) — tela WPF gerada.");
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
            $"-m -o -u2 -udfver102 -l{safeLabel} " +
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

        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }
}
