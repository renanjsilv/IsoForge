using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using IsoForge.Models;

namespace IsoForge.Core.Linux;

/// <summary>
/// Gera os arquivos do <c>archinstall</c> (instalador oficial da ISO do Arch Linux):
/// <c>user_configuration.json</c>, <c>user_credentials.json</c> e o script
/// <c>arch-autoinstall.sh</c>, disparado pelo parâmetro de boot <c>script=</c> do archiso.
/// </summary>
public static class ArchInstallGenerator
{
    public const string FileName = "user_configuration.json";
    public const string CredentialsFileName = "user_credentials.json";
    public const string BootstrapFileName = "arch-autoinstall.sh";

    // O TypeInfoResolver precisa ser explicito: JsonNode.ToJsonString marca as opcoes como
    // somente-leitura e falha se elas nao tiverem um resolvedor definido.
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static string Generate(BuildConfig c)
    {
        var lx = c.Linux;
        var hostname = LinuxPostInstall.Slug(string.IsNullOrWhiteSpace(c.ComputerName) ? "isoforge-pc" : c.ComputerName);

        var packages = new JsonArray();
        foreach (var p in AutoinstallGenerator.BasePackages(c)) packages.Add(p);
        packages.Add("sudo");
        packages.Add("networkmanager");

        var diskConfig = new JsonObject
        {
            ["config_type"] = "default_layout",
            // O device e preenchido pelo arch-autoinstall.sh (disco fixo detectado no boot).
            ["device_modifications"] = new JsonArray()
        };
        if (lx.DiskMode == LinuxDiskMode.EntireDiskEncrypted && !string.IsNullOrWhiteSpace(lx.DiskPassword))
        {
            diskConfig["disk_encryption"] = new JsonObject
            {
                ["encryption_type"] = "luks",
                ["encryption_password"] = lx.DiskPassword
            };
        }

        var root = new JsonObject
        {
            ["archinstall-language"] = "English",
            ["bootloader"] = "Systemd-boot",
            ["hostname"] = hostname,
            ["kernels"] = new JsonArray { "linux" },
            ["ntp"] = true,
            ["packages"] = packages,
            ["swap"] = true,
            ["timezone"] = lx.Timezone,
            ["locale_config"] = new JsonObject
            {
                ["kb_layout"] = KbLayout(lx),
                ["sys_enc"] = "UTF-8",
                ["sys_lang"] = lx.LocaleId.Split('.')[0] + ".UTF-8"
            },
            ["network_config"] = new JsonObject { ["type"] = "nm" },
            ["audio_config"] = new JsonObject { ["audio"] = "pipewire" },
            ["disk_config"] = diskConfig,
            ["profile_config"] = ProfileConfig(lx),
            ["custom-commands"] = new JsonArray
            {
                $"systemctl enable {LinuxAnswerFile.FirstBootServiceName}",
                "systemctl enable NetworkManager"
            },
            ["__isoforge_target_disk"] = lx.TargetDisk ?? ""
        };

        return root.ToJsonString(Json) + Environment.NewLine;
    }

    /// <summary>Credenciais (senhas já cifradas em SHA-512 crypt).</summary>
    public static string Credentials(BuildConfig c)
    {
        var hash = UnixCrypt.Sha512(c.Password);
        var root = new JsonObject
        {
            ["root_enc_password"] = c.Linux.DisableRootPassword ? "!" : hash,
            ["users"] = new JsonArray
            {
                new JsonObject
                {
                    ["username"] = c.UserName,
                    ["enc_password"] = hash,
                    ["sudo"] = c.IsAdministrator
                }
            }
        };
        return root.ToJsonString(Json) + Environment.NewLine;
    }

    static JsonObject ProfileConfig(LinuxConfig lx)
    {
        var (main, details, greeter) = lx.Desktop switch
        {
            LinuxDesktop.None => ("Minimal", new JsonArray(), ""),
            LinuxDesktop.Kde => ("Desktop", new JsonArray { "KDE Plasma" }, "sddm"),
            LinuxDesktop.Xfce => ("Desktop", new JsonArray { "Xfce4" }, "lightdm-slick-greeter"),
            _ => ("Desktop", new JsonArray { "GNOME" }, "gdm")
        };

        var profile = new JsonObject
        {
            ["main"] = main,
            ["details"] = details,
            ["custom_settings"] = new JsonObject()
        };
        var cfg = new JsonObject
        {
            ["profile"] = profile,
            ["gfx_driver"] = lx.ProprietaryDrivers ? "Nvidia (proprietary)" : "All open-source"
        };
        if (greeter.Length > 0) cfg["greeter"] = greeter;
        return cfg;
    }

    /// <summary>
    /// Script chamado pelo archiso via <c>script=</c>: detecta o disco de destino, ajusta o
    /// perfil, roda o archinstall em modo silencioso e instala o payload do IsoForge.
    /// </summary>
    public static string Bootstrap(BuildConfig c)
    {
        var folder = LinuxAnswerFile.PayloadFolderOnIso;
        var payload = LinuxPostInstall.PayloadDirOnDisk;
        var svc = LinuxAnswerFile.FirstBootServiceName;

        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine("# ================================================================");
        sb.AppendLine("# IsoForge - instalacao desassistida do Arch Linux (archinstall)");
        sb.AppendLine("# Disparado pelo parametro de boot script= do archiso.");
        sb.AppendLine("# ================================================================");
        sb.AppendLine("set -u");
        sb.AppendLine("exec > >(tee -a /var/log/isoforge-arch.log) 2>&1");
        sb.AppendLine("log() { echo \"[IsoForge] $*\"; }");
        sb.AppendLine();
        sb.AppendLine("# A midia do archiso fica montada em /run/archiso/bootmnt.");
        sb.AppendLine($"MEDIA=\"\"");
        sb.AppendLine($"for d in /run/archiso/bootmnt/{folder} /run/archiso/cowspace/{folder} /mnt/{folder}; do");
        sb.AppendLine("  [ -d \"$d\" ] && MEDIA=\"$d\" && break");
        sb.AppendLine("done");
        sb.AppendLine("if [ -z \"$MEDIA\" ]; then log 'ERRO: payload do IsoForge nao encontrado na midia.'; exit 1; fi");
        sb.AppendLine("log \"payload em $MEDIA\"");
        sb.AppendLine();
        sb.AppendLine("log 'aguardando rede...'");
        sb.AppendLine("for i in $(seq 1 30); do ping -c1 -W2 archlinux.org >/dev/null 2>&1 && break; sleep 5; done");
        sb.AppendLine();
        sb.AppendLine("# Disco de destino: o configurado, ou o primeiro disco fixo que nao seja a midia de boot.");
        sb.AppendLine("DISK=" + LinuxPostInstall.Sh(c.Linux.TargetDisk ?? ""));
        sb.AppendLine("if [ -z \"$DISK\" ]; then");
        sb.AppendLine("  DISK=$(lsblk -dpno NAME,TYPE,RM,RO | awk '$2==\"disk\" && $3==0 && $4==0 {print $1; exit}')");
        sb.AppendLine("fi");
        sb.AppendLine("if [ -z \"$DISK\" ]; then log 'ERRO: nenhum disco fixo encontrado (nunca instalo no pendrive).'; exit 1; fi");
        sb.AppendLine("log \"disco de destino: $DISK\"");
        sb.AppendLine();
        sb.AppendLine("CONF=/tmp/isoforge-arch-config.json");
        sb.AppendLine($"CREDS=\"$MEDIA/{CredentialsFileName}\"");
        sb.AppendLine("# Injeta o disco escolhido no perfil do archinstall.");
        sb.AppendLine("python - \"$MEDIA/" + FileName + "\" \"$CONF\" \"$DISK\" <<'ISOFORGE_PY_EOF'");
        sb.AppendLine("import json, sys");
        sb.AppendLine("src, dst, disk = sys.argv[1], sys.argv[2], sys.argv[3]");
        sb.AppendLine("cfg = json.load(open(src))");
        sb.AppendLine("cfg.pop('__isoforge_target_disk', None)");
        sb.AppendLine("dc = cfg.setdefault('disk_config', {})");
        sb.AppendLine("dc['config_type'] = 'default_layout'");
        sb.AppendLine("dc['device_modifications'] = [{'device': disk, 'wipe': True, 'partitions': []}]");
        sb.AppendLine("json.dump(cfg, open(dst, 'w'), indent=2)");
        sb.AppendLine("ISOFORGE_PY_EOF");
        sb.AppendLine();
        sb.AppendLine("log 'executando o archinstall...'");
        sb.AppendLine("archinstall --config \"$CONF\" --creds \"$CREDS\" --silent");
        sb.AppendLine("RC=$?");
        sb.AppendLine("[ $RC -ne 0 ] && log \"archinstall terminou com codigo $RC\"");
        sb.AppendLine();
        sb.AppendLine("# Sistema instalado montado pelo archinstall.");
        sb.AppendLine("TARGET=/mnt/archinstall");
        sb.AppendLine("[ -d \"$TARGET/etc\" ] || TARGET=/mnt");
        sb.AppendLine($"if [ -d \"$TARGET/etc\" ]; then");
        sb.AppendLine($"  mkdir -p \"$TARGET{payload}\"");
        sb.AppendLine($"  cp -a \"$MEDIA/.\" \"$TARGET{payload}/\"");
        sb.AppendLine($"  chmod +x \"$TARGET{payload}\"/*.sh 2>/dev/null || true");
        sb.AppendLine($"  install -m 0644 \"$TARGET{payload}/{svc}\" \"$TARGET/etc/systemd/system/{svc}\"");
        sb.AppendLine($"  arch-chroot \"$TARGET\" systemctl enable {svc} 2>/dev/null || \\");
        sb.AppendLine($"    ln -sf /etc/systemd/system/{svc} \"$TARGET/etc/systemd/system/multi-user.target.wants/{svc}\"");
        sb.AppendLine("  log 'payload do IsoForge instalado.'");
        sb.AppendLine("fi");
        sb.AppendLine();
        if (!c.SandboxTest)
        {
            sb.AppendLine("log 'instalacao concluida; reiniciando em 10s.'");
            sb.AppendLine("sleep 10");
            sb.AppendLine("systemctl reboot");
        }
        else
        {
            sb.AppendLine("log 'modo de teste: reinicio pulado.'");
        }
        return sb.ToString();
    }

    /// <summary>Layout de teclado no formato do console do Arch (ex.: br-abnt2).</summary>
    static string KbLayout(LinuxConfig lx) =>
        lx.KeyboardLayout == "br" && lx.KeyboardVariant.Contains("abnt", StringComparison.OrdinalIgnoreCase)
            ? "br-abnt2"
            : lx.KeyboardLayout;
}
