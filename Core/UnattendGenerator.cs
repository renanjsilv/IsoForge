using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using IsoForge.Models;

namespace IsoForge.Core;

/// <summary>
/// Gera o autounattend.xml colocado na raiz da ISO. O Setup do Windows lê esse
/// arquivo automaticamente e aplica as respostas em cada passe (windowsPE,
/// specialize, oobeSystem).
/// </summary>
public static class UnattendGenerator
{
    static readonly XNamespace U = "urn:schemas-microsoft-com:unattend";
    static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

    public static void WriteTo(BuildConfig c, string filePath)
    {
        var doc = Generate(c);
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new UTF8Encoding(false)
        };
        using var writer = XmlWriter.Create(filePath, settings);
        doc.Save(writer);
    }

    public static XDocument Generate(BuildConfig c)
    {
        var root = new XElement(U + "unattend",
            new XAttribute(XNamespace.Xmlns + "wcm", Wcm.NamespaceName),
            SettingsWindowsPe(c),
            SettingsSpecialize(c),
            SettingsAuditUser(c),
            SettingsOobe(c));

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    static XElement Component(string name, params object[] content)
    {
        var el = new XElement(U + "component",
            new XAttribute("name", name),
            new XAttribute("processorArchitecture", "amd64"),
            new XAttribute("publicKeyToken", "31bf3856ad364e35"),
            new XAttribute("language", "neutral"),
            new XAttribute("versionScope", "nonSxS"));
        el.Add(content);
        return el;
    }

    static XElement SettingsWindowsPe(BuildConfig c)
    {
        var intl = Component("Microsoft-Windows-International-Core-WinPE",
            new XElement(U + "SetupUILanguage", new XElement(U + "UILanguage", c.Locale)),
            new XElement(U + "InputLocale", c.InputLocale),
            new XElement(U + "SystemLocale", c.Locale),
            new XElement(U + "UILanguage", c.Locale),
            new XElement(U + "UserLocale", c.Locale));

        var setup = Component("Microsoft-Windows-Setup");

        var run = new XElement(U + "RunSynchronous");
        int order = 0;
        if (c.BypassHardwareChecks)
        {
            string[] checks = { "BypassTPMCheck", "BypassSecureBootCheck", "BypassRAMCheck", "BypassStorageCheck", "BypassCPUCheck" };
            foreach (var check in checks)
            {
                run.Add(new XElement(U + "RunSynchronousCommand",
                    new XAttribute(Wcm + "action", "add"),
                    new XElement(U + "Order", ++order),
                    new XElement(U + "Path",
                        $"cmd /c reg add HKLM\\SYSTEM\\Setup\\LabConfig /v {check} /t REG_DWORD /d 1 /f")));
            }
        }
        // Seleção automática de disco (nunca o pendrive) — roda antes de aplicar a imagem.
        if (c.AutoSelectDisk && !c.GoldenReference)
        {
            run.Add(new XElement(U + "RunSynchronousCommand",
                new XAttribute(Wcm + "action", "add"),
                new XElement(U + "Order", ++order),
                new XElement(U + "Path", DiskPrepGenerator.RunCommand),
                new XElement(U + "Description", "IsoForge - selecao automatica de disco (nao-USB)")));
        }
        if (order > 0)
            setup.Add(run);

        var userData = new XElement(U + "UserData",
            new XElement(U + "AcceptEula", "true"));
        if (!string.IsNullOrWhiteSpace(c.ProductKey))
        {
            userData.Add(new XElement(U + "ProductKey",
                new XElement(U + "Key", c.ProductKey.Trim()),
                new XElement(U + "WillShowUI", "OnError")));
        }
        setup.Add(userData);

        // ISO de referência (imagem golden automática): particiona o disco 0 e instala
        // sem intervenção, para a VM headless conseguir instalar sozinha.
        if (c.GoldenReference)
        {
            setup.Add(GoldenDiskConfiguration());
            setup.Add(new XElement(U + "ImageInstall",
                new XElement(U + "OSImage",
                    new XElement(U + "InstallTo",
                        new XElement(U + "DiskID", 0),
                        new XElement(U + "PartitionID", 3)),
                    new XElement(U + "InstallToAvailablePartition", "false"))));
        }
        else if (c.AutoSelectDisk)
        {
            // O IsoForgeDiskPrep.cmd já preparou o disco não-USB; instala na partição pronta.
            setup.Add(new XElement(U + "ImageInstall",
                new XElement(U + "OSImage",
                    new XElement(U + "InstallToAvailablePartition", "true"))));
        }

        return new XElement(U + "settings", new XAttribute("pass", "windowsPE"), intl, setup);
    }

    static XElement GoldenDiskConfiguration()
    {
        XElement Part(int order, string type, bool extend = false, int size = 0)
        {
            var p = new XElement(U + "CreatePartition",
                new XAttribute(Wcm + "action", "add"),
                new XElement(U + "Order", order),
                new XElement(U + "Type", type));
            if (extend) p.Add(new XElement(U + "Extend", "true"));
            else p.Add(new XElement(U + "Size", size));
            return p;
        }
        XElement Modify(int order, int id, string? format = null, string? label = null, string? letter = null)
        {
            var m = new XElement(U + "ModifyPartition",
                new XAttribute(Wcm + "action", "add"),
                new XElement(U + "Order", order),
                new XElement(U + "PartitionID", id));
            if (format != null) m.Add(new XElement(U + "Format", format));
            if (label != null) m.Add(new XElement(U + "Label", label));
            if (letter != null) m.Add(new XElement(U + "Letter", letter));
            return m;
        }

        return new XElement(U + "DiskConfiguration",
            new XElement(U + "Disk",
                new XAttribute(Wcm + "action", "add"),
                new XElement(U + "DiskID", 0),
                new XElement(U + "WillWipeDisk", "true"),
                new XElement(U + "CreatePartitions",
                    Part(1, "EFI", size: 260),
                    Part(2, "MSR", size: 16),
                    Part(3, "Primary", extend: true)),
                new XElement(U + "ModifyPartitions",
                    Modify(1, 1, format: "FAT32", label: "System"),
                    Modify(2, 2),
                    Modify(3, 3, format: "NTFS", label: "Windows", letter: "C"))));
    }

    static XElement? SettingsSpecialize(BuildConfig c)
    {
        // Na seleção de unidade o nome é definido pelo operador no modo de auditoria.
        if (c.UseUnitSelection || string.IsNullOrWhiteSpace(c.ComputerName))
            return null;

        var shell = Component("Microsoft-Windows-Shell-Setup",
            new XElement(U + "ComputerName", c.ComputerName.Trim()));

        return new XElement(U + "settings", new XAttribute("pass", "specialize"), shell);
    }

    /// <summary>
    /// Passe auditUser: só existe quando a seleção de unidade está ligada.
    /// Lança a tela WPF de seleção no modo de auditoria (antes do usuário).
    /// </summary>
    static XElement? SettingsAuditUser(BuildConfig c)
    {
        // ISO de referência: instala tudo e faz sysprep automaticamente.
        if (c.GoldenReference)
        {
            var golden = Component("Microsoft-Windows-Deployment",
                new XElement(U + "RunAsynchronous",
                    new XElement(U + "RunAsynchronousCommand",
                        new XAttribute(Wcm + "action", "add"),
                        new XElement(U + "Order", 1),
                        new XElement(U + "Path", "cmd.exe /c C:\\Setup\\golden.cmd"),
                        new XElement(U + "Description", "IsoForge - instala apps e faz sysprep (imagem golden)"))));
            return new XElement(U + "settings", new XAttribute("pass", "auditUser"), golden);
        }

        // Seleção de unidade no 1º logon (sem auditoria) NÃO usa este passe.
        if (!c.UseUnitSelection || c.UnitMethod != UnitSelectionMethod.Audit)
            return null;

        var deployment = Component("Microsoft-Windows-Deployment",
            new XElement(U + "RunAsynchronous",
                new XElement(U + "RunAsynchronousCommand",
                    new XAttribute(Wcm + "action", "add"),
                    new XElement(U + "Order", 1),
                    new XElement(U + "Path",
                        $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\\Setup\\{UnitSelectorGenerator.FileName}"),
                    new XElement(U + "Description", "IsoForge - selecao de unidade"))));

        return new XElement(U + "settings", new XAttribute("pass", "auditUser"), deployment);
    }

    static XElement SettingsOobe(BuildConfig c)
    {
        var intl = Component("Microsoft-Windows-International-Core",
            new XElement(U + "InputLocale", c.InputLocale),
            new XElement(U + "SystemLocale", c.Locale),
            new XElement(U + "UILanguage", c.Locale),
            new XElement(U + "UserLocale", c.Locale));

        // Modo Entra ID: NÃO cria conta local nem faz logon automático no autounattend —
        // deixa o OOBE mostrar o login corporativo/estudante (WiFi + e-mail + ingresso no
        // Entra ID). O usuário local (admin) e os apps são feitos pelo SetupComplete.cmd
        // (como SYSTEM, antes do OOBE). Ver SetupCompleteGenerator.
        if (c.Mode == DeploymentMode.EntraId && !c.GoldenReference)
        {
            var oobeEntra = new XElement(U + "OOBE",
                new XElement(U + "HideEULAPage", "true"),
                new XElement(U + "HideOEMRegistrationScreen", "true"),
                // false: mostra a tela "conta corporativa ou de estudante" (login Entra ID)
                new XElement(U + "HideOnlineAccountScreens", "false"),
                new XElement(U + "HideLocalAccountScreen", "false"),
                // false: mostra a configuração de WiFi no OOBE (necessária para o Entra ID)
                new XElement(U + "HideWirelessSetupInOOBE", c.SkipWifiSetup ? "true" : "false"),
                new XElement(U + "ProtectYourPC", "3"));

            var shellEntra = Component("Microsoft-Windows-Shell-Setup", oobeEntra);
            return new XElement(U + "settings", new XAttribute("pass", "oobeSystem"), intl, shellEntra);
        }

        var oobe = new XElement(U + "OOBE",
            new XElement(U + "HideEULAPage", "true"),
            new XElement(U + "HideLocalAccountScreen", "true"),
            new XElement(U + "HideOEMRegistrationScreen", "true"),
            new XElement(U + "HideOnlineAccountScreens", "true"),
            new XElement(U + "HideWirelessSetupInOOBE", c.SkipWifiSetup ? "true" : "false"),
            new XElement(U + "ProtectYourPC", "3"));

        var account = new XElement(U + "LocalAccount",
            new XAttribute(Wcm + "action", "add"),
            new XElement(U + "Name", c.UserName),
            new XElement(U + "DisplayName", c.UserName),
            new XElement(U + "Group", c.IsAdministrator ? "Administrators" : "Users"),
            new XElement(U + "Password",
                new XElement(U + "Value", c.Password),
                new XElement(U + "PlainText", "true")));

        var shell = Component("Microsoft-Windows-Shell-Setup",
            oobe,
            new XElement(U + "UserAccounts", new XElement(U + "LocalAccounts", account)));

        if (c.AutoLogonOnce)
        {
            shell.Add(new XElement(U + "AutoLogon",
                new XElement(U + "Enabled", "true"),
                new XElement(U + "LogonCount", 1),
                new XElement(U + "Username", c.UserName),
                new XElement(U + "Password",
                    new XElement(U + "Value", c.Password),
                    new XElement(U + "PlainText", "true"))));
        }

        shell.Add(new XElement(U + "FirstLogonCommands",
            new XElement(U + "SynchronousCommand",
                new XAttribute(Wcm + "action", "add"),
                new XElement(U + "Order", 1),
                new XElement(U + "CommandLine", "cmd.exe /c C:\\Setup\\install.cmd"),
                new XElement(U + "Description", "IsoForge - instalacao de aplicativos padrao"),
                new XElement(U + "RequiresUserInput", "false"))));

        var settings = new XElement(U + "settings", new XAttribute("pass", "oobeSystem"), intl, shell);

        // Seleção de unidade EM AUDITORIA: manda o Windows entrar no modo de auditoria após
        // instalar. A tela roda antes do OOBE; ao renomear, o script chama sysprep /oobe /reboot,
        // e então este mesmo passe é reprocessado, criando o usuário e rodando o install.cmd.
        // (No método "1º logon" isso não é usado — a renomeação acontece no install.cmd.)
        if ((c.UseUnitSelection && c.UnitMethod == UnitSelectionMethod.Audit) || c.GoldenReference)
        {
            settings.Add(Component("Microsoft-Windows-Deployment",
                new XElement(U + "Reseal",
                    new XElement(U + "Mode", "Audit"))));
        }

        return settings;
    }
}
