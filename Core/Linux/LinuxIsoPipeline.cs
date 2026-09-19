using System.IO;
using System.Text;
using IsoForge.Models;

namespace IsoForge.Core.Linux;

/// <summary>
/// Pipeline de geração da ISO para as distros Linux. Dois caminhos:
///
/// <list type="bullet">
/// <item><b>Reempacotar</b> — monta a ISO oficial, extrai, injeta o arquivo de resposta + o
/// payload do IsoForge, ajusta o GRUB/isolinux e recompila uma ISO bootável.</item>
/// <item><b>ISO seed</b> — não toca na ISO oficial: gera uma segunda ISO pequena com o rótulo
/// que o instalador procura (<c>cidata</c> no Ubuntu, <c>OEMDRV</c> na família RHEL).</item>
/// </list>
/// </summary>
public class LinuxIsoPipeline
{
    readonly IProgress<string> _log;
    readonly IProgress<int>? _pct;

    public LinuxIsoPipeline(IProgress<string> log, IProgress<int>? percent = null)
    {
        _log = log;
        _pct = percent;
    }

    void Log(string msg) => _log.Report(msg);
    void Pct(int p) => _pct?.Report(p);

    // ------------------------------------------------------------------
    // Build
    // ------------------------------------------------------------------
    public async Task BuildAsync(BuildConfig cfg, CancellationToken ct)
    {
        Validate(cfg, dryRun: false);

        var os = OsCatalog.Get(cfg.Os);
        var delivery = LinuxAnswerFile.EffectiveDelivery(cfg);
        Log($"Sistema de destino: {os.Name} ({os.Unattend}).");

        if (cfg.Linux.Delivery == LinuxDeliveryMode.SeedIso && delivery == LinuxDeliveryMode.Repack)
            Log($"Aviso: o instalador do {os.Name} não localiza o arquivo de resposta numa segunda mídia; " +
                "gerando a ISO reempacotada.");

        if (delivery == LinuxDeliveryMode.SeedIso)
            await BuildSeedAsync(cfg, ct);
        else
            await BuildRepackAsync(cfg, ct);
    }

    // ------------------------------------------------------------------
    // Caminho 1: ISO seed (a ISO oficial fica intacta)
    // ------------------------------------------------------------------
    async Task BuildSeedAsync(BuildConfig cfg, CancellationToken ct)
    {
        var label = LinuxAnswerFile.SeedLabel(cfg.Os);
        var root = Path.Combine(Path.GetTempPath(), "IsoForge");
        var staging = Path.Combine(root, $"seed_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(staging);
        Log($"Pasta de trabalho: {staging}");
        Pct(10);

        try
        {
            WritePayload(cfg, staging);
            Pct(60);

            Log($"Compilando a ISO seed (rótulo {label})...");
            // Sem "-o", de proposito. O -o faz o oscdimg calcular MD5 de TODO arquivo para
            // gravar duplicatas uma vez so. Medido no perfil de uma ISO do Windows (718 MB,
            // 901 arquivos): 3,7 s com -o contra 1,4 s sem (2,6x), e a ISO saiu do MESMO
            // tamanho -- numa imagem do Windows quase nao ha duplicatas, entao era hash
            // puro por nada.
            await CompilarSemBootAsync(cfg, staging, label, ct);
            Pct(100);

            var kb = new FileInfo(cfg.OutputIsoPath).Length / 1024.0;
            Log("");
            Log($"✔ ISO seed gerada: {cfg.OutputIsoPath} ({kb:F0} KB)");
            Log($"  Dê o boot pela ISO oficial do {OsCatalog.NameOf(cfg.Os)} e anexe esta ISO como");
            Log("  segunda unidade de CD/DVD. As instruções completas estão em isoforge/LEIA-ME.txt.");
        }
        finally
        {
            Log("Limpando pasta de trabalho...");
            IsoTools.ForceDeleteDirectory(staging, Log);
        }
    }

    // ------------------------------------------------------------------
    // Caminho 2: reempacotar a ISO oficial
    // ------------------------------------------------------------------
    async Task BuildRepackAsync(BuildConfig cfg, CancellationToken ct)
    {
        var root = Path.Combine(Path.GetTempPath(), "IsoForge");
        CleanStaleWorkFolders(root);

        var staging = Path.Combine(root, $"work_{DateTime.Now:yyyyMMdd_HHmmss}");
        var bootDir = Path.Combine(root, $"boot_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(staging);
        Log($"Pasta de trabalho: {staging}");
        Pct(3);

        try
        {
            // Monta e copia no Windows, extrai direto do arquivo no Linux: quem decide é o
            // AbrirIsoAsync, que devolve o rótulo do volume nos dois casos.
            Pct(10);
            var isoLabel = await IsoTools.AbrirIsoAsync(cfg.SourceIsoPath, staging, Log, ct);
            Pct(50);

            // O rótulo já entra normalizado: a família RHEL localiza o ks.cfg por ele
            // (inst.ks=hd:LABEL=...), então os parâmetros de boot e a ISO de saída
            // precisam usar exatamente o mesmo texto.
            var label = SanitizeLabel(string.IsNullOrWhiteSpace(isoLabel) ? DefaultLabel(cfg) : isoLabel, cfg);
            Log($"Rótulo original: {(string.IsNullOrWhiteSpace(isoLabel) ? "(sem rótulo)" : isoLabel)}; " +
                $"rótulo da ISO gerada: {label}");

            IsoTools.ClearReadOnly(new DirectoryInfo(staging));

            WritePayload(cfg, staging);
            Pct(58);

            // Faz o instalador achar o arquivo de resposta sozinho.
            var grubParams = LinuxAnswerFile.KernelParams(cfg, label, forGrub: true);
            var isolinuxParams = LinuxAnswerFile.KernelParams(cfg, label, forGrub: false);
            Log("Ajustando as entradas de boot (GRUB / isolinux)...");
            Log($"  Parâmetros: {isolinuxParams}");
            var patch = BootloaderPatcher.Apply(staging, grubParams, isolinuxParams, Log);
            if (!patch.Any)
                Log("  Aviso: nenhuma entrada de boot foi reconhecida. A instalação pode pedir confirmação; " +
                    "nesse caso acrescente os parâmetros acima manualmente no menu do GRUB (tecla 'e').");
            else
                Log($"  {patch.Entries} entrada(s) em {patch.Files} arquivo(s) automatizada(s).");
            Pct(66);

            // As imagens de boot do Linux só existem dentro do catálogo El Torito: extrai para
            // conseguir reconstruir uma ISO bootável.
            Log("Extraindo as imagens de boot (El Torito) da ISO de origem...");
            var (bios, efi) = ElTorito.Extract(cfg.SourceIsoPath, bootDir);
            Log(bios != null ? $"  BIOS: {Path.GetFileName(bios)} ({new FileInfo(bios).Length / 1024} KB)" : "  BIOS: não encontrada (ISO só-UEFI).");
            Log(efi != null ? $"  UEFI: {Path.GetFileName(efi)} ({new FileInfo(efi).Length / 1024} KB)" : "  UEFI: não encontrada.");
            if (efi == null && bios == null)
                throw new InvalidOperationException(
                    "Não encontrei nenhuma imagem de boot na ISO de origem. Ela é mesmo uma ISO oficial " +
                    "e bootável da distribuição?");
            Pct(72);

            await BuildIsoAsync(cfg, staging, bootDir, label, bios, efi, ct);
            Pct(100);

            Log("");
            Log($"✔ ISO gerada com sucesso: {cfg.OutputIsoPath}");
            Log($"  Tamanho: {new FileInfo(cfg.OutputIsoPath).Length / 1024.0 / 1024.0 / 1024.0:F2} GB");
        }
        finally
        {
            Log("Limpando pasta de trabalho...");
            IsoTools.ForceDeleteDirectory(staging, Log);
            IsoTools.ForceDeleteDirectory(bootDir, Log);
        }
    }

    // ------------------------------------------------------------------
    // Recompilação
    // ------------------------------------------------------------------
    async Task BuildIsoAsync(BuildConfig cfg, string staging, string bootDir, string label,
        string? bios, string? efi, CancellationToken ct)
    {
        var xorriso = IsoTools.FindXorriso();
        if (xorriso != null)
        {
            Log($"xorriso encontrado ({xorriso}): gerando ISO híbrida (UEFI + BIOS legado).");
            if (await TryXorrisoAsync(xorriso, cfg, staging, bootDir, label, bios, efi, ct)) return;
            Log("Aviso: o xorriso falhou; usando o oscdimg (boot UEFI).");
        }

        if (!Plataforma.EhWindows)
            throw new InvalidOperationException(
                "Sem o xorriso não há como recompilar uma ISO bootável fora do Windows. " +
                "Instale-o: 'sudo apt install xorriso' (Debian/Ubuntu), 'sudo dnf install xorriso' " +
                "(Fedora/RHEL) ou 'sudo pacman -S libisoburn' (Arch).");

        // Sem xorriso: o oscdimg reconstrói o El Torito a partir das imagens extraídas.
        // O boot UEFI fica íntegro; o BIOS legado depende da tabela de informações do
        // isolinux, que o oscdimg não regrava — por isso o aviso.
        var parts = new List<string>();
        if (bios != null) parts.Add($"p0,e,b\"{bios}\"");
        if (efi != null) parts.Add($"pEF,e,b\"{efi}\"");
        var bootdata = $"-bootdata:{parts.Count}#" + string.Join("#", parts);

        Log("Recompilando a ISO com o oscdimg...");
        var safeLabel = SanitizeLabel(label, cfg);
        // Sem "-o", de proposito. O -o faz o oscdimg calcular MD5 de TODO arquivo para
        // gravar duplicatas uma vez so. Medido no perfil de uma ISO do Windows (718 MB,
        // 901 arquivos): 3,7 s com -o contra 1,4 s sem (2,6x), e a ISO saiu do MESMO
        // tamanho -- numa imagem do Windows quase nao ha duplicatas, entao era hash
        // puro por nada.
        var args = $"-m -u2 -udfver102 -l{safeLabel} {bootdata} \"{staging}\" \"{cfg.OutputIsoPath}\"";
        var exit = await IsoTools.RunAsync(cfg.OscdimgPath, args, Log, ct);
        if (exit != 0) throw new InvalidOperationException($"oscdimg falhou com código {exit}.");

        if (bios != null)
            Log("Aviso: a ISO foi reconstruída com o oscdimg — o boot UEFI está garantido, mas o BIOS " +
                "legado pode não funcionar. Instale o xorriso e gere de novo se precisar de boot legado.");
    }

    /// <summary>
    /// Compila uma ISO sem boot — a "seed", que só carrega o arquivo de resposta.
    /// No Windows é o oscdimg de sempre; fora dele, o xorriso.
    /// </summary>
    async Task CompilarSemBootAsync(BuildConfig cfg, string staging, string label, CancellationToken ct)
    {
        if (Plataforma.EhWindows)
        {
            // Sem "-o", de proposito. O -o faz o oscdimg calcular MD5 de TODO arquivo para
            // gravar duplicatas uma vez so -- hash puro por nada numa arvore desta.
            var args = $"-m -u2 -udfver102 -l{label} \"{staging}\" \"{cfg.OutputIsoPath}\"";
            var exit = await IsoTools.RunAsync(cfg.OscdimgPath, args, Log, ct);
            if (exit != 0) throw new InvalidOperationException($"oscdimg falhou com código {exit}.");
            return;
        }

        var xorriso = IsoTools.FindXorriso() ?? throw new InvalidOperationException(
            "xorriso não encontrado. Instale-o: 'sudo apt install xorriso' (Debian/Ubuntu), " +
            "'sudo dnf install xorriso' (Fedora/RHEL) ou 'sudo pacman -S libisoburn' (Arch).");

        var argsX = "-as mkisofs -iso-level 3 -full-iso9660-filenames -joliet -joliet-long -rational-rock " +
                    $"-volid \"{label}\" -output \"{cfg.OutputIsoPath}\" \"{staging}\"";
        var saida = await IsoTools.RunAsync(xorriso, argsX, Log, ct);
        if (saida != 0) throw new InvalidOperationException($"xorriso falhou com código {saida}.");
    }

    /// <summary>Reconstrói a ISO com o xorriso, preservando boot UEFI + BIOS (híbrida).</summary>
    async Task<bool> TryXorrisoAsync(string xorriso, BuildConfig cfg, string staging, string bootDir,
        string label, string? bios, string? efi, CancellationToken ct)
    {
        // As imagens extraídas precisam estar DENTRO da árvore para o xorriso referenciá-las.
        var inTree = Path.Combine(staging, LinuxAnswerFile.PayloadFolderOnIso, "boot");
        Directory.CreateDirectory(inTree);
        string? biosRel = null, efiRel = null;
        if (bios != null)
        {
            var dest = Path.Combine(inTree, Path.GetFileName(bios));
            File.Copy(bios, dest, true);
            biosRel = $"{LinuxAnswerFile.PayloadFolderOnIso}/boot/{Path.GetFileName(bios)}";
        }
        if (efi != null)
        {
            var dest = Path.Combine(inTree, Path.GetFileName(efi));
            File.Copy(efi, dest, true);
            efiRel = $"{LinuxAnswerFile.PayloadFolderOnIso}/boot/{Path.GetFileName(efi)}";
        }

        var sb = new StringBuilder();
        sb.Append("-as mkisofs -iso-level 3 -full-iso9660-filenames -joliet -joliet-long -rational-rock ");
        sb.Append($"-volid \"{SanitizeLabel(label, cfg)}\" ");
        if (biosRel != null)
            sb.Append($"-eltorito-boot \"{biosRel}\" -eltorito-catalog \"{LinuxAnswerFile.PayloadFolderOnIso}/boot/boot.catalog\" " +
                      "-no-emul-boot -boot-load-size 4 -boot-info-table ");
        if (efiRel != null)
        {
            if (biosRel != null) sb.Append("-eltorito-alt-boot ");
            sb.Append($"-e \"{efiRel}\" -no-emul-boot -isohybrid-gpt-basdat ");
        }
        sb.Append($"-o \"{cfg.OutputIsoPath}\" \"{staging}\"");

        var exit = await IsoTools.RunAsync(xorriso, sb.ToString(), Log, ct);
        if (exit == 0 && File.Exists(cfg.OutputIsoPath) && new FileInfo(cfg.OutputIsoPath).Length > 0)
            return true;

        try { if (File.Exists(cfg.OutputIsoPath)) File.Delete(cfg.OutputIsoPath); } catch { }
        return false;
    }

    // ------------------------------------------------------------------
    // Payload
    // ------------------------------------------------------------------

    /// <summary>
    /// Grava, sob <paramref name="root"/>, os arquivos de resposta e o payload do IsoForge
    /// (scripts, serviço systemd, imagens de aparência e script personalizado).
    /// </summary>
    public void WritePayload(BuildConfig cfg, string root)
    {
        // Scripts em UTF-8 SEM BOM e com fim de linha LF: um BOM antes do "#!" quebra o shebang
        // e CRLF quebra a execução no Linux.
        var utf8 = new UTF8Encoding(false);
        var payloadDir = Path.Combine(root, LinuxAnswerFile.PayloadFolderOnIso);
        Directory.CreateDirectory(payloadDir);

        foreach (var file in LinuxAnswerFile.Generate(cfg))
        {
            var dest = Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllText(dest, Unix(file.Content), utf8);
            Log($"  {file.RelativePath}");
        }

        // Aparência: as imagens vão junto do payload e são aplicadas no primeiro boot.
        if (!string.IsNullOrWhiteSpace(cfg.WallpaperPath) && File.Exists(cfg.WallpaperPath))
        {
            var name = Path.GetFileName(cfg.WallpaperPath);
            File.Copy(cfg.WallpaperPath, Path.Combine(payloadDir, name), true);
            Log($"  Papel de parede incluído: {name}");
        }
        if (!string.IsNullOrWhiteSpace(cfg.LockScreenPath) && File.Exists(cfg.LockScreenPath))
        {
            var name = "lockscreen" + Path.GetExtension(cfg.LockScreenPath);
            File.Copy(cfg.LockScreenPath, Path.Combine(payloadDir, name), true);
            Log($"  Tela de bloqueio incluída: {name}");
        }
        if (!string.IsNullOrWhiteSpace(cfg.PostScriptPath) && File.Exists(cfg.PostScriptPath))
        {
            var name = Path.GetFileName(cfg.PostScriptPath);
            File.WriteAllText(Path.Combine(payloadDir, name), Unix(File.ReadAllText(cfg.PostScriptPath)), utf8);
            Log($"  Script personalizado incluído: {name}");
        }

        var apps = LinuxPostInstall.SelectedRecipes(cfg);
        Log(apps.Count == 0
            ? "  Nenhum aplicativo selecionado."
            : $"  {apps.Count} aplicativo(s): {string.Join(", ", apps.Select(a => a.DisplayName))}");
    }

    /// <summary>Converte para fim de linha LF (os scripts rodam no Linux).</summary>
    static string Unix(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n");

    // ------------------------------------------------------------------
    // Dry-run: gera só os arquivos, para inspeção
    // ------------------------------------------------------------------
    public void DryRun(BuildConfig cfg, string outputFolder)
    {
        Validate(cfg, dryRun: true);
        Directory.CreateDirectory(outputFolder);
        WritePayload(cfg, outputFolder);

        var os = OsCatalog.Get(cfg.Os);
        var label = DefaultLabel(cfg);
        File.WriteAllText(Path.Combine(outputFolder, "parametros-de-boot.txt"),
            "Parametros de kernel usados na ISO reempacotada\n" +
            "==============================================\n\n" +
            "GRUB (UEFI):\n  " + LinuxAnswerFile.KernelParams(cfg, label, forGrub: true) + "\n\n" +
            "isolinux (BIOS):\n  " + LinuxAnswerFile.KernelParams(cfg, label, forGrub: false) + "\n",
            new UTF8Encoding(false));

        Log("");
        Log($"✔ Arquivos gerados em: {outputFolder}");
        Log($"  - {LinuxAnswerFile.PayloadFolderOnIso}/{os.AnswerFileName} (arquivo de resposta do instalador)");
        Log($"  - {LinuxAnswerFile.PayloadFolderOnIso}/{LinuxPostInstall.FileName} (pós-instalação do 1º boot)");
        Log($"  - {LinuxAnswerFile.PayloadFolderOnIso}/{LinuxAnswerFile.FirstBootServiceName} (serviço systemd)");
        Log("  - parametros-de-boot.txt (o que é acrescentado ao GRUB/isolinux)");
        Log("");
        Log("Para testar sem gravar nada: use o botão \"Script Hyper-V\" e rode o script numa VM.");
    }

    // ------------------------------------------------------------------
    // Validação
    // ------------------------------------------------------------------
    public void Validate(BuildConfig cfg, bool dryRun)
    {
        var os = OsCatalog.Get(cfg.Os);
        bool seed = LinuxAnswerFile.EffectiveDelivery(cfg) == LinuxDeliveryMode.SeedIso;

        if (!dryRun)
        {
            // No modo seed a ISO oficial não é tocada, então nem precisa estar selecionada.
            if (!seed)
            {
                if (string.IsNullOrWhiteSpace(cfg.SourceIsoPath) || !File.Exists(cfg.SourceIsoPath))
                    throw new InvalidOperationException($"Selecione a ISO oficial do {os.Name}.");
                if (Path.GetFullPath(cfg.SourceIsoPath).Equals(Path.GetFullPath(cfg.OutputIsoPath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A ISO de saída não pode ser o mesmo arquivo da ISO de origem.");
            }
            if (string.IsNullOrWhiteSpace(cfg.OutputIsoPath))
                throw new InvalidOperationException("Informe onde salvar a ISO personalizada.");
            // O oscdimg é ferramenta do Windows ADK; fora do Windows quem compila é o xorriso.
            if (Plataforma.EhWindows)
            {
                if (string.IsNullOrWhiteSpace(cfg.OscdimgPath) || !File.Exists(cfg.OscdimgPath))
                    throw new InvalidOperationException(Oscdimg.InstallHint);
            }
            else if (IsoTools.FindXorriso() == null)
            {
                throw new InvalidOperationException(
                    "xorriso não encontrado. Instale-o: 'sudo apt install xorriso' (Debian/Ubuntu), " +
                    "'sudo dnf install xorriso' (Fedora/RHEL) ou 'sudo pacman -S libisoburn' (Arch).");
            }
        }

        if (string.IsNullOrWhiteSpace(cfg.UserName))
            throw new InvalidOperationException("Informe o nome do usuário do Linux.");
        if (cfg.UserName.Trim().Equals("root", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use outro nome de usuário: 'root' é a conta administrativa do sistema.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(cfg.UserName.Trim(), "^[a-z_][a-z0-9_-]{0,31}$"))
            throw new InvalidOperationException(
                "Nome de usuário inválido para Linux. Use letras minúsculas, números, '-' ou '_', " +
                "começando por letra (ex.: suporte, ti-suporte).");
        if (string.IsNullOrEmpty(cfg.Password))
            throw new InvalidOperationException("Informe a senha do usuário do Linux.");

        if (cfg.Linux.DiskMode == LinuxDiskMode.EntireDiskEncrypted && string.IsNullOrEmpty(cfg.Linux.DiskPassword))
            throw new InvalidOperationException("Criptografia de disco ativada: informe a senha do LUKS.");

        if (!string.IsNullOrWhiteSpace(cfg.Linux.TargetDisk) && !cfg.Linux.TargetDisk.StartsWith("/dev/"))
            throw new InvalidOperationException("O disco de destino deve ser um caminho de dispositivo (ex.: /dev/sda, /dev/nvme0n1).");

        if (seed && !string.IsNullOrWhiteSpace(cfg.WallpaperPath))
            Log("Aviso: no modo ISO seed as imagens de aparência também são incluídas, mas o instalador " +
                "só as copia se conseguir montar a mídia seed durante a instalação.");

        if (!string.IsNullOrWhiteSpace(cfg.DriverPackPath))
            Log("Aviso: a injeção de drivers por modelo é exclusiva do Windows; no Linux os drivers " +
                "vêm no kernel. Use \"Drivers proprietários\" para NVIDIA/firmware e codecs.");
    }

    // ------------------------------------------------------------------
    void CleanStaleWorkFolders(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.EnumerateDirectories(root, "work_*"))
            {
                Log($"Removendo pasta de trabalho antiga: {Path.GetFileName(d)}");
                IsoTools.ForceDeleteDirectory(d, Log);
            }
            foreach (var d in Directory.EnumerateDirectories(root, "boot_*"))
                IsoTools.ForceDeleteDirectory(d, Log);
        }
        catch { /* melhor esforço */ }
    }

    static string DefaultLabel(BuildConfig cfg) => OsCatalog.Get(cfg.Os).ShortName.ToUpperInvariant();

    /// <summary>Rótulo aceito pelo oscdimg (ASCII, sem espaços, no máximo 32 caracteres).</summary>
    public static string SanitizeLabel(string label, BuildConfig cfg)
    {
        var ascii = LinuxPostInstall.Ascii(label).Trim();
        if (string.IsNullOrWhiteSpace(ascii)) ascii = DefaultLabel(cfg);
        var sb = new StringBuilder();
        foreach (var ch in ascii)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_');
        var result = sb.ToString();
        return result.Length > 32 ? result[..32] : result;
    }
}
