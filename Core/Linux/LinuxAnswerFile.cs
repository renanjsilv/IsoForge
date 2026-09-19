using System.Text;
using IsoForge.Models;

namespace IsoForge.Core.Linux;

/// <summary>Um arquivo gerado, com o caminho relativo à raiz da ISO.</summary>
/// <param name="RelativePath">Caminho dentro da ISO (ex.: <c>isoforge/user-data</c>).</param>
/// <param name="Content">Conteúdo do arquivo.</param>
public record AnswerFile(string RelativePath, string Content);

/// <summary>
/// Ponto único que traduz a <see cref="BuildConfig"/> nos arquivos que a distro escolhida
/// entende (autoinstall, preseed, kickstart, AutoYaST ou archinstall), no payload comum do
/// IsoForge e nos parâmetros de boot que fazem o instalador encontrá-los.
/// </summary>
public static class LinuxAnswerFile
{
    /// <summary>Pasta do payload dentro da ISO (scripts, serviço, imagens, arquivo de resposta).</summary>
    public const string PayloadFolderOnIso = "isoforge";

    /// <summary>Unidade systemd que roda a pós-instalação no primeiro boot.</summary>
    public const string FirstBootServiceName = "isoforge-firstboot.service";

    /// <summary>Rótulo do volume "seed" lido pelo cloud-init (Ubuntu).</summary>
    public const string SeedLabelCloudInit = "cidata";

    /// <summary>Rótulo do volume "seed" lido automaticamente pelo Anaconda (Fedora/Rocky/Alma).</summary>
    public const string SeedLabelKickstart = "OEMDRV";

    /// <summary>
    /// A distro consegue achar o arquivo de resposta numa segunda mídia, sem reempacotar a ISO?
    /// Só Ubuntu (volume <c>cidata</c>) e a família RHEL (volume <c>OEMDRV</c>) fazem isso sozinhas.
    /// </summary>
    public static bool SeedSupported(TargetOs os) => os is
        TargetOs.Ubuntu or TargetOs.UbuntuServer or TargetOs.Fedora or TargetOs.RockyLinux or TargetOs.AlmaLinux;

    /// <summary>Rótulo que a ISO "seed" precisa ter para ser encontrada automaticamente.</summary>
    public static string SeedLabel(TargetOs os) => OsCatalog.FamilyOf(os) == OsFamily.RedHat
        ? SeedLabelKickstart
        : SeedLabelCloudInit;

    /// <summary>Modo de entrega efetivo (força reempacotamento onde o seed não funciona).</summary>
    public static LinuxDeliveryMode EffectiveDelivery(BuildConfig c) =>
        c.Linux.Delivery == LinuxDeliveryMode.SeedIso && SeedSupported(c.Os)
            ? LinuxDeliveryMode.SeedIso
            : LinuxDeliveryMode.Repack;

    // ------------------------------------------------------------------
    /// <summary>
    /// Todos os arquivos de texto gerados (arquivo de resposta + payload), com os caminhos
    /// relativos à raiz da ISO de saída. Imagens e o script personalizado são copiados
    /// separadamente pelo <see cref="LinuxIsoPipeline"/>.
    /// </summary>
    public static IReadOnlyList<AnswerFile> Generate(BuildConfig c)
    {
        var files = new List<AnswerFile>();
        var dir = PayloadFolderOnIso + "/";
        var os = OsCatalog.Get(c.Os);

        // 1. Arquivo de resposta da distro.
        switch (os.Unattend)
        {
            case UnattendKind.CloudInitAutoinstall:
                files.Add(new AnswerFile(dir + AutoinstallGenerator.FileName, AutoinstallGenerator.Generate(c)));
                files.Add(new AnswerFile(dir + AutoinstallGenerator.MetaDataFileName, AutoinstallGenerator.MetaData(c)));
                break;
            case UnattendKind.DebianPreseed:
                files.Add(new AnswerFile(dir + PreseedGenerator.FileName, PreseedGenerator.Generate(c)));
                break;
            case UnattendKind.Kickstart:
                files.Add(new AnswerFile(dir + KickstartGenerator.FileName, KickstartGenerator.Generate(c)));
                break;
            case UnattendKind.AutoYast:
                files.Add(new AnswerFile(dir + AutoYastGenerator.FileName, AutoYastGenerator.Generate(c)));
                break;
            case UnattendKind.ArchInstall:
                files.Add(new AnswerFile(dir + ArchInstallGenerator.FileName, ArchInstallGenerator.Generate(c)));
                files.Add(new AnswerFile(dir + ArchInstallGenerator.CredentialsFileName, ArchInstallGenerator.Credentials(c)));
                files.Add(new AnswerFile(dir + ArchInstallGenerator.BootstrapFileName, ArchInstallGenerator.Bootstrap(c)));
                break;
        }

        // 2. Payload comum: pos-instalacao + servico de primeiro boot + relatorio.
        files.Add(new AnswerFile(dir + LinuxPostInstall.FileName, LinuxPostInstall.Generate(c)));
        files.Add(new AnswerFile(dir + FirstBootServiceName, LinuxPostInstall.FirstBootService()));
        if (c.GenerateReport)
            files.Add(new AnswerFile(dir + LinuxReportGenerator.FileName, LinuxReportGenerator.Generate(c)));

        // 3. Instrucoes (o que anexar/como bootar) — vale ouro no modo seed.
        files.Add(new AnswerFile(dir + "LEIA-ME.txt", Readme(c)));

        // No modo seed o cloud-init exige user-data/meta-data na RAIZ do volume.
        if (EffectiveDelivery(c) == LinuxDeliveryMode.SeedIso)
        {
            if (os.Unattend == UnattendKind.CloudInitAutoinstall)
            {
                files.Add(new AnswerFile(AutoinstallGenerator.FileName, AutoinstallGenerator.Generate(c)));
                files.Add(new AnswerFile(AutoinstallGenerator.MetaDataFileName, AutoinstallGenerator.MetaData(c)));
            }
            else if (os.Unattend == UnattendKind.Kickstart)
            {
                // O Anaconda le automaticamente /ks.cfg de um volume rotulado OEMDRV.
                files.Add(new AnswerFile(KickstartGenerator.FileName, KickstartGenerator.Generate(c)));
            }
        }

        return files;
    }

    // ------------------------------------------------------------------
    /// <summary>
    /// Parâmetros de kernel acrescentados às entradas do bootloader (GRUB/isolinux) da ISO
    /// reempacotada para o instalador encontrar o arquivo de resposta sem perguntar nada.
    /// </summary>
    /// <param name="isoLabel">Rótulo do volume da ISO de saída (usado pelo Anaconda).</param>
    /// <param name="forGrub">true = escapa o ';' exigido pela sintaxe do grub.cfg.</param>
    public static string KernelParams(BuildConfig c, string isoLabel, bool forGrub)
    {
        var lx = c.Linux;
        var path = "/cdrom/" + PayloadFolderOnIso;
        switch (OsCatalog.Get(c.Os).Unattend)
        {
            case UnattendKind.CloudInitAutoinstall:
                var sep = forGrub ? "\\;" : ";";
                return $"autoinstall ds=nocloud{sep}s={path}/";

            case UnattendKind.DebianPreseed when c.Os == TargetOs.LinuxMint:
                // Ubiquity (Mint): instalacao automatica lendo o preseed da propria midia.
                return $"automatic-ubiquity noprompt file={path}/{PreseedGenerator.FileName} " +
                       $"locale={lx.LocaleId} keyboard-configuration/layoutcode={lx.KeyboardLayout}";

            case UnattendKind.DebianPreseed:
                // Sem "---" aqui: a linha original do isolinux/GRUB já traz o separador e os
                // parâmetros são inseridos antes dele (dois separadores quebrariam o d-i).
                return $"auto=true priority=critical preseed/file={path}/{PreseedGenerator.FileName} " +
                       $"locale={lx.LocaleId} keymap={lx.KeyboardLayout}";

            case UnattendKind.Kickstart:
                // O Anaconda localiza o ks.cfg pelo rotulo do volume (espacos viram \x20).
                var label = (isoLabel ?? "").Replace(" ", "\\x20");
                return $"inst.ks=hd:LABEL={label}:/{PayloadFolderOnIso}/{KickstartGenerator.FileName}";

            case UnattendKind.AutoYast:
                return $"autoyast=cd:/{PayloadFolderOnIso}/{AutoYastGenerator.FileName}";

            case UnattendKind.ArchInstall:
                // O archiso baixa/executa o script indicado (caminho relativo a midia de boot).
                return $"script=/{PayloadFolderOnIso}/{ArchInstallGenerator.BootstrapFileName}";

            default:
                return "";
        }
    }

    // ------------------------------------------------------------------
    static string Readme(BuildConfig c)
    {
        var os = OsCatalog.Get(c.Os);
        var seed = EffectiveDelivery(c) == LinuxDeliveryMode.SeedIso;
        var sb = new StringBuilder();

        sb.AppendLine("IsoForge - instalacao desassistida");
        sb.AppendLine("==================================");
        sb.AppendLine();
        sb.AppendLine($"Sistema:  {LinuxPostInstall.Ascii(os.Name)}");
        sb.AppendLine($"Metodo:   {(seed ? "ISO seed (a ISO oficial continua intacta)" : "ISO reempacotada")}");
        sb.AppendLine($"Resposta: {os.AnswerFileName}");
        sb.AppendLine();

        if (seed)
        {
            sb.AppendLine("COMO USAR (modo seed)");
            sb.AppendLine("---------------------");
            sb.AppendLine("1. Faca o boot pela ISO OFICIAL da distro (sem alteracoes).");
            sb.AppendLine($"2. Anexe TAMBEM esta ISO seed (rotulo {SeedLabel(c.Os)}) como segunda unidade");
            sb.AppendLine("   de CD/DVD da VM, ou grave-a num segundo pendrive.");
            if (OsCatalog.FamilyOf(c.Os) == OsFamily.RedHat)
                sb.AppendLine("3. O Anaconda encontra o ks.cfg sozinho pelo rotulo OEMDRV. Nada a digitar.");
            else
            {
                sb.AppendLine("3. O instalador encontra a configuracao pelo rotulo cidata.");
                sb.AppendLine("   Se ele pedir confirmacao, acrescente 'autoinstall' na linha de");
                sb.AppendLine("   comando do kernel (tecla 'e' no menu do GRUB) para nao perguntar nada.");
            }
        }
        else
        {
            sb.AppendLine("COMO USAR (ISO reempacotada)");
            sb.AppendLine("----------------------------");
            sb.AppendLine("1. Grave a ISO gerada num pendrive (Rufus/balenaEtcher no modo DD/imagem)");
            sb.AppendLine("   ou anexe-a diretamente na VM.");
            sb.AppendLine("2. De o boot em modo UEFI. O instalador roda sozinho, sem perguntas.");
            sb.AppendLine();
            sb.AppendLine("ATENCAO: a ISO e reconstruida com boot UEFI. Se a maquina so tiver BIOS");
            sb.AppendLine("legado, instale o xorriso e gere de novo — o IsoForge o usa quando disponivel");
            sb.AppendLine("e assim preserva tambem o boot legado.");
        }

        sb.AppendLine();
        sb.AppendLine("APOS A INSTALACAO");
        sb.AppendLine("-----------------");
        sb.AppendLine("No primeiro boot, o servico isoforge-firstboot roda a pos-instalacao:");
        sb.AppendLine("repositorios oficiais, aplicativos, aparencia, VPN e otimizacoes.");
        sb.AppendLine($"Log completo em {LinuxPostInstall.LogPath}");
        if (c.GenerateReport)
            sb.AppendLine("Relatorio HTML na area de trabalho e em /var/log/isoforge/.");
        sb.AppendLine();
        sb.AppendLine("AVISO: a senha do usuario vai cifrada (SHA-512 crypt), mas senhas de Wi-Fi,");
        sb.AppendLine("VPN e criptografia de disco ficam em texto na midia. Trate a ISO como");
        sb.AppendLine("material sensivel.");
        return sb.ToString();
    }
}
