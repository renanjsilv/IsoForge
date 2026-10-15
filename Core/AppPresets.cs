using System.IO;
using IsoForge.Models;

namespace IsoForge.Core;

public record AppPreset(string Name, string DefaultArgsExe, string DefaultArgsMsi, AppKind Kind, string FileFilter, string Hint);

public static class AppPresets
{
    public static readonly AppPreset Office = new(
        "Office 365 (ODT)",
        "", "",
        AppKind.Office,
        "setup.exe do Office Deployment Tool|setup.exe|Executáveis|*.exe",
        "Selecione o setup.exe extraído do Office Deployment Tool (ODT). A instalação baixa o Office da internet no primeiro logon, conforme o XML de configuração.");

    public static readonly AppPreset AnyDesk = new(
        "AnyDesk",
        "--install \"C:\\Program Files (x86)\\AnyDesk\" --silent --create-shortcuts --create-desktop-icon --start-with-win",
        "/qn /norestart",
        AppKind.Generic,
        "Instalador do AnyDesk|*.exe;*.msi",
        "Baixe o AnyDesk.exe em anydesk.com.");

    public static readonly AppPreset SevenZip = new(
        "7-Zip",
        "/S",
        "/qn /norestart",
        AppKind.Generic,
        "Instalador do 7-Zip|*.exe;*.msi",
        "Baixe em 7-zip.org (o .msi x64 é o mais confiável para instalação silenciosa).");

    public static readonly AppPreset FortiClient = new(
        "FortiClient",
        "/quiet /norestart",
        "/qn /norestart",
        AppKind.Generic,
        "Instalador do FortiClient|*.msi;*.exe",
        "Prefira o .msi offline do FortiClient VPN (o instalador online .exe pode exigir internet e não ser 100% silencioso).");

    public static readonly AppPreset AdobeReader = new(
        "Adobe Acrobat Reader",
        "/sAll /rs /msi EULA_ACCEPT=YES",
        "/qn /norestart EULA_ACCEPT=YES",
        AppKind.Generic,
        "Instalador do Adobe Reader|*.exe;*.msi",
        "Baixe o instalador offline em get.adobe.com/br/reader/enterprise.");

    /// <summary>Argumentos padrão conforme a extensão do instalador escolhido.</summary>
    public static string DefaultArgsFor(AppPreset preset, string installerPath)
    {
        return Path.GetExtension(installerPath).Equals(".msi", StringComparison.OrdinalIgnoreCase)
            ? preset.DefaultArgsMsi
            : preset.DefaultArgsExe;
    }
}
