using System.Text;
using IsoForge.Models;

namespace IsoForge.Core.Linux;

/// <summary>
/// Gera o <c>user-data</c> (cloud-init / autoinstall do subiquity) usado pelo Ubuntu 20.04+
/// para instalar sem nenhuma pergunta. É o equivalente Ubuntu do autounattend.xml.
/// </summary>
public static class AutoinstallGenerator
{
    public const string FileName = "user-data";
    public const string MetaDataFileName = "meta-data";

    /// <summary>meta-data do NoCloud (obrigatório existir, mesmo vazio).</summary>
    public static string MetaData(BuildConfig c)
    {
        var host = LinuxPostInstall.Slug(string.IsNullOrWhiteSpace(c.ComputerName) ? "isoforge" : c.ComputerName);
        return $"instance-id: isoforge-{host}\nlocal-hostname: {host}\n";
    }

    public static string Generate(BuildConfig c)
    {
        var lx = c.Linux;
        var sb = new StringBuilder();
        var hostname = LinuxPostInstall.Slug(string.IsNullOrWhiteSpace(c.ComputerName) ? "isoforge-pc" : c.ComputerName);
        var passwordHash = UnixCrypt.Sha512(c.Password);

        sb.AppendLine("#cloud-config");
        sb.AppendLine("# ================================================================");
        sb.AppendLine("# IsoForge - instalacao desassistida do Ubuntu (autoinstall)");
        sb.AppendLine("# Gerado automaticamente. A senha vai cifrada em SHA-512 crypt.");
        sb.AppendLine("# ================================================================");
        sb.AppendLine("autoinstall:");
        sb.AppendLine("  version: 1");
        sb.AppendLine("  interactive-sections: []");
        sb.AppendLine($"  locale: {lx.LocaleId}");
        sb.AppendLine("  keyboard:");
        sb.AppendLine($"    layout: {lx.KeyboardLayout}");
        sb.AppendLine($"    variant: {Yaml(lx.KeyboardVariant)}");
        sb.AppendLine("  timezone: " + lx.Timezone);
        sb.AppendLine("  identity:");
        sb.AppendLine($"    hostname: {hostname}");
        sb.AppendLine($"    realname: {Yaml(string.IsNullOrWhiteSpace(lx.FullName) ? c.UserName : lx.FullName)}");
        sb.AppendLine($"    username: {c.UserName}");
        sb.AppendLine($"    password: {Yaml(passwordHash)}");

        sb.AppendLine("  ssh:");
        sb.AppendLine($"    install-server: {(lx.InstallSshServer ? "true" : "false")}");
        sb.AppendLine("    allow-pw: true");
        if (!string.IsNullOrWhiteSpace(lx.SshAuthorizedKey))
        {
            sb.AppendLine("    authorized-keys:");
            sb.AppendLine($"      - {Yaml(lx.SshAuthorizedKey.Trim())}");
        }

        // Disco: "largest" faz o subiquity escolher o maior disco fixo e ignorar a midia de boot
        // (equivalente a selecao automatica de disco do Windows).
        sb.AppendLine("  storage:");
        sb.AppendLine("    layout:");
        sb.AppendLine("      name: " + lx.DiskMode switch
        {
            LinuxDiskMode.EntireDisk => "direct",
            _ => "lvm"
        });
        if (lx.DiskMode == LinuxDiskMode.EntireDiskEncrypted)
            sb.AppendLine($"      password: {Yaml(lx.DiskPassword)}");
        sb.AppendLine("      match:");
        if (!string.IsNullOrWhiteSpace(lx.TargetDisk))
            sb.AppendLine($"        path: {lx.TargetDisk}");
        else
            sb.AppendLine("        size: largest");

        // Pacotes garantidos antes do primeiro boot (o script de pos-instalacao depende deles).
        sb.AppendLine("  packages:");
        foreach (var p in BasePackages(c)) sb.AppendLine($"    - {p}");

        sb.AppendLine($"  updates: {(lx.UpdateDuringInstall ? "all" : "security")}");
        if (lx.ProprietaryDrivers)
        {
            sb.AppendLine("  drivers:");
            sb.AppendLine("    install: true");
            sb.AppendLine("  codecs:");
            sb.AppendLine("    install: true");
        }
        sb.AppendLine("  shutdown: reboot");

        // cloud-config aplicado ao sistema instalado.
        sb.AppendLine("  user-data:");
        sb.AppendLine($"    disable_root: {(lx.DisableRootPassword ? "true" : "false")}");
        sb.AppendLine("    preserve_hostname: false");

        sb.AppendLine("  late-commands:");
        sb.AppendLine("    - |");
        foreach (var line in LateCommands(c).Split('\n'))
            sb.AppendLine("      " + line.TrimEnd());

        return sb.ToString();
    }

    /// <summary>Pacotes básicos exigidos pelo script de pós-instalação.</summary>
    public static string[] BasePackages(BuildConfig c)
    {
        var list = new List<string> { "curl", "ca-certificates", "gnupg" };
        if (c.UseUnitSelection) { list.Add("dmidecode"); list.Add("zenity"); }
        if (c.Linux.EnableFlatpak) list.Add("flatpak");
        return list.ToArray();
    }

    /// <summary>
    /// Comandos executados no fim da instalação (ainda no ambiente do instalador, com o sistema
    /// montado em /target): copia o payload do IsoForge e habilita o serviço de primeiro boot.
    /// </summary>
    static string LateCommands(BuildConfig c)
    {
        var sb = new StringBuilder();
        sb.AppendLine("set -x");
        sb.AppendLine("SRC=\"\"");
        sb.AppendLine($"for d in /cdrom/{LinuxAnswerFile.PayloadFolderOnIso} /media/*/{LinuxAnswerFile.PayloadFolderOnIso} /run/media/*/{LinuxAnswerFile.PayloadFolderOnIso}; do");
        sb.AppendLine("  [ -d \"$d\" ] && SRC=\"$d\" && break");
        sb.AppendLine("done");
        sb.AppendLine("if [ -z \"$SRC\" ]; then");
        sb.AppendLine("  # ISO seed (rotulo cidata) anexada como segunda midia.");
        sb.AppendLine("  mkdir -p /mnt/isoforge-seed");
        sb.AppendLine($"  mount -o ro /dev/disk/by-label/{LinuxAnswerFile.SeedLabelCloudInit} /mnt/isoforge-seed 2>/dev/null \\");
        sb.AppendLine($"    && SRC=/mnt/isoforge-seed/{LinuxAnswerFile.PayloadFolderOnIso}");
        sb.AppendLine("fi");
        sb.AppendLine($"mkdir -p /target{LinuxPostInstall.PayloadDirOnDisk}");
        sb.AppendLine($"[ -n \"$SRC\" ] && [ -d \"$SRC\" ] && cp -a \"$SRC/.\" /target{LinuxPostInstall.PayloadDirOnDisk}/");
        sb.AppendLine($"chmod +x /target{LinuxPostInstall.PayloadDirOnDisk}/*.sh 2>/dev/null || true");
        sb.AppendLine($"install -m 0644 /target{LinuxPostInstall.PayloadDirOnDisk}/{LinuxAnswerFile.FirstBootServiceName} \\");
        sb.AppendLine($"  /target/etc/systemd/system/{LinuxAnswerFile.FirstBootServiceName}");
        sb.AppendLine($"curtin in-target --target=/target -- systemctl enable {LinuxAnswerFile.FirstBootServiceName}");
        if (c.Linux.AutoLogin)
        {
            sb.AppendLine("mkdir -p /target/etc/gdm3");
            sb.AppendLine("printf '[daemon]\\nAutomaticLoginEnable=true\\nAutomaticLogin=%s\\n' " +
                          LinuxPostInstall.Sh(c.UserName) + " > /target/etc/gdm3/custom.conf");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Escapa um valor YAML entre aspas simples (protege $, #, : e espaços).</summary>
    public static string Yaml(string? value)
    {
        var v = value ?? "";
        return "'" + v.Replace("'", "''") + "'";
    }
}
