using System.IO;
using System.Text;
using IsoForge.Models;

namespace IsoForge.Core;

/// <summary>
/// Gera os scripts auxiliares executados no primeiro logon (papel de parede e
/// configuração do FortiClient VPN), gravados em C:\Setup e chamados pelo
/// install.cmd.
/// </summary>
public static class ExtraScriptsGenerator
{
    public const string AppearanceFileName = "Set-Appearance.ps1";
    public const string FortiFileName = "Configure-FortiClient.ps1";
    public const string FortiRegImportName = "FortiClient-import.reg";
    public const string FortiVpnXmlName = "FortiClient-vpn.xml";

    public static bool HasAppearance(BuildConfig cfg)
        => !string.IsNullOrWhiteSpace(cfg.WallpaperPath) || !string.IsNullOrWhiteSpace(cfg.LockScreenPath);

    // ------------------------------------------------------------------
    // Aparência: papel de parede + tela de bloqueio (login)
    // ------------------------------------------------------------------
    public static string Appearance(string? wallpaperFileName, string? lockScreenFileName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Aparencia padrao (IsoForge): papel de parede + tela de bloqueio");
        sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(wallpaperFileName))
        {
            sb.AppendLine($"$wp = \"C:\\Setup\\{wallpaperFileName}\"");
            sb.AppendLine("if (Test-Path $wp) {");
            sb.AppendLine("  # 1) usuario atual: registro + aplica JA na sessao (SystemParametersInfo)");
            sb.AppendLine("  Set-ItemProperty 'HKCU:\\Control Panel\\Desktop' -Name Wallpaper -Value $wp -Force");
            sb.AppendLine("  Set-ItemProperty 'HKCU:\\Control Panel\\Desktop' -Name WallpaperStyle -Value 10 -Force");
            sb.AppendLine("  Set-ItemProperty 'HKCU:\\Control Panel\\Desktop' -Name TileWallpaper -Value 0 -Force");
            sb.AppendLine("  # Desliga o Spotlight de desktop (senao sobrepoe o papel de parede)");
            sb.AppendLine("  New-Item -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager' -Force | Out-Null");
            sb.AppendLine("  Set-ItemProperty 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager' -Name 'RotatingLockScreenEnabled' -Value 0 -Force");
            sb.AppendLine("  try {");
            sb.AppendLine("    Add-Type -Name IsoForgeWP -Namespace Win32 -MemberDefinition '[DllImport(\"user32.dll\",SetLastError=true)] public static extern bool SystemParametersInfo(int uAction,int uParam,string lpvParam,int fuWinIni);' -ErrorAction SilentlyContinue");
            sb.AppendLine("    [Win32.IsoForgeWP]::SystemParametersInfo(20,0,$wp,3) | Out-Null   # SPI_SETDESKWALLPAPER + update/broadcast");
            sb.AppendLine("  } catch {}");
            sb.AppendLine("  rundll32.exe user32.dll,UpdatePerUserSystemParameters 1, True");
            sb.AppendLine("  Write-Output \"IsoForge: wallpaper aplicado ($wp)\"");
            sb.AppendLine("  # 2) imagem padrao do Windows (novos usuarios)");
            sb.AppendLine("  foreach ($d in @('C:\\Windows\\Web\\Wallpaper\\Windows\\img0.jpg','C:\\Windows\\Web\\Wallpaper\\Windows\\img19.jpg')) {");
            sb.AppendLine("    if (Test-Path $d) { takeown /f $d | Out-Null; icacls $d /grant '*S-1-5-32-544:F' | Out-Null; Copy-Item $wp $d -Force }");
            sb.AppendLine("  }");
            sb.AppendLine("  $dir4k = 'C:\\Windows\\Web\\4K\\Wallpaper\\Windows'");
            sb.AppendLine("  if (Test-Path $dir4k) { Get-ChildItem $dir4k -Filter *.jpg | ForEach-Object { takeown /f $_.FullName | Out-Null; icacls $_.FullName /grant '*S-1-5-32-544:F' | Out-Null; Copy-Item $wp $_.FullName -Force } }");
            sb.AppendLine("} else {");
            sb.AppendLine("  Write-Output \"IsoForge: WALLPAPER NAO ENCONTRADO em $wp\"");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(lockScreenFileName))
        {
            sb.AppendLine($"$lock = \"C:\\Setup\\{lockScreenFileName}\"");
            sb.AppendLine("if (Test-Path $lock) {");
            sb.AppendLine("  # Copia para um local estavel PRESERVANDO o formato real da imagem.");
            sb.AppendLine("  # (renomear um .png para .jpg fazia a tela de bloqueio ficar preta.)");
            sb.AppendLine("  $ext = [System.IO.Path]::GetExtension($lock)");
            sb.AppendLine("  if ([string]::IsNullOrWhiteSpace($ext)) { $ext = '.jpg' }");
            sb.AppendLine("  $dst = \"C:\\Windows\\Web\\Screen\\IsoForgeLock$ext\"");
            sb.AppendLine("  New-Item -ItemType Directory -Force 'C:\\Windows\\Web\\Screen' | Out-Null");
            sb.AppendLine("  Copy-Item $lock $dst -Force");
            sb.AppendLine("  # A tela de bloqueio e desenhada em contexto de sistema: garante leitura p/ todos.");
            sb.AppendLine("  icacls $dst /grant '*S-1-5-32-545:(R)' | Out-Null   # Usuarios (SID) - leitura");
            sb.AppendLine("  icacls $dst /grant '*S-1-5-11:(R)' | Out-Null       # Usuarios autenticados");
            sb.AppendLine();
            sb.AppendLine("  # 1) Politica de Personalizacao: confiavel em Pro/Enterprise/Education e NAO fica preta.");
            sb.AppendLine("  #    O Intune (canal de politica de dispositivo) ainda consegue sobrescrever depois.");
            sb.AppendLine("  $pol = 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\Personalization'");
            sb.AppendLine("  New-Item -Path $pol -Force | Out-Null");
            sb.AppendLine("  New-ItemProperty -Path $pol -Name 'LockScreenImage' -Value $dst -PropertyType String -Force | Out-Null");
            sb.AppendLine();
            sb.AppendLine("  # 2) PersonalizationCSP = mesmo canal do Intune (compatibilidade com o fluxo anterior).");
            sb.AppendLine("  $k = 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\PersonalizationCSP'");
            sb.AppendLine("  New-Item -Path $k -Force | Out-Null");
            sb.AppendLine("  New-ItemProperty -Path $k -Name 'LockScreenImageStatus' -Value 1 -PropertyType DWord -Force | Out-Null");
            sb.AppendLine("  New-ItemProperty -Path $k -Name 'LockScreenImagePath'  -Value $dst -PropertyType String -Force | Out-Null");
            sb.AppendLine("  New-ItemProperty -Path $k -Name 'LockScreenImageUrl'   -Value $dst -PropertyType String -Force | Out-Null");
            sb.AppendLine();
            sb.AppendLine("  # 3) Desliga o Windows Spotlight na tela de bloqueio (senao ele pode sobrepor a imagem).");
            sb.AppendLine("  $cdm = 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\CloudContent'");
            sb.AppendLine("  New-Item -Path $cdm -Force | Out-Null");
            sb.AppendLine("  New-ItemProperty -Path $cdm -Name 'DisableWindowsSpotlightFeatures' -Value 1 -PropertyType DWord -Force | Out-Null");
            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // FortiClient VPN (túneis IPsec)
    // ------------------------------------------------------------------
    public static string FortiClient(BuildConfig cfg)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Configura tuneis IPsec do FortiClient VPN (IsoForge)");
        sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        sb.AppendLine();

        // Caminho CONFIÁVEL: importar um .reg exportado de um FortiClient já configurado.
        // Esse .reg carrega o gateway E a PSK CIFRADA (a chave de cifra é embutida no
        // FortiClient, então o blob é portável entre máquinas). Quando há .reg, NÃO gravamos
        // os túneis digitados: texto puro sobrescreveria a PSK cifrada e quebraria o túnel.
        if (!string.IsNullOrWhiteSpace(cfg.FortiClientRegImportPath))
        {
            sb.AppendLine("# Importa a configuracao exportada de um FortiClient ja ajustado (metodo confiavel:");
            sb.AppendLine("# traz gateway + PSK cifrada exatamente como o FortiClient grava).");
            sb.AppendLine($"$reg = \"C:\\Setup\\{FortiRegImportName}\"");
            sb.AppendLine("if (Test-Path $reg) { reg import $reg; Write-Output 'Config do FortiClient importada do .reg.' }");
            sb.AppendLine("# Reinicia o servico para reler a configuracao importada.");
            sb.AppendLine("Restart-Service -Name 'FA_Scheduler' -Force -ErrorAction SilentlyContinue");
            sb.AppendLine("Restart-Service -Name 'FortiClient*' -Force -ErrorAction SilentlyContinue");
            return sb.ToString();
        }

        if (cfg.VpnTunnels.Count > 0 && !cfg.VpnUseTextImport)
        {
            // Método por texto DESLIGADO (padrão): não mexe na config do FortiClient para não
            // corrompê-lo. O caminho confiável é o .reg (botão "Capturar deste PC").
            sb.AppendLine("Write-Output 'FortiClient: tuneis digitados NAO importados (import por texto desligado).'");
            sb.AppendLine("Write-Output 'Use Capturar deste PC / importar .reg para configurar os tuneis com seguranca.'");
            return sb.ToString();
        }

        if (cfg.VpnTunnels.Count > 0)
        {
            // Método por TEXTO (experimental): gera um XML de configuração (com a PSK em texto puro)
            // e importa pelo FCConfig.exe. Pode instabilizar o FortiClient em algumas versões.
            sb.AppendLine("# Importa os tuneis IPsec via FCConfig.exe (PSK em texto no XML) - EXPERIMENTAL.");
            sb.AppendLine($"$xml = \"C:\\Setup\\{FortiVpnXmlName}\"");
            sb.AppendLine("$fc = @(");
            sb.AppendLine("  \"$env:ProgramFiles\\Fortinet\\FortiClient\\FCConfig.exe\",");
            sb.AppendLine("  \"${env:ProgramFiles(x86)}\\Fortinet\\FortiClient\\FCConfig.exe\"");
            sb.AppendLine(") | Where-Object { Test-Path $_ } | Select-Object -First 1");
            sb.AppendLine("if ($fc -and (Test-Path $xml)) {");
            sb.AppendLine("  # -o importvpn: importa SO as conexoes de VPN (nao mexe no resto da config).");
            sb.AppendLine("  & $fc -f $xml -o importvpn 2>&1 | Write-Output");
            sb.AppendLine("  Write-Output \"Tuneis IPsec importados via FCConfig ($fc).\"");
            sb.AppendLine("} else {");
            sb.AppendLine("  Write-Output 'AVISO: FCConfig.exe nao encontrado (FortiClient instalado?) ou XML ausente; tuneis nao importados. Use a captura/import de .reg.'");
            sb.AppendLine("}");
            sb.AppendLine("# Reinicia o servico para reler a configuracao.");
            sb.AppendLine("Restart-Service -Name 'FA_Scheduler' -Force -ErrorAction SilentlyContinue");
            sb.AppendLine("Restart-Service -Name 'FortiClient*' -Force -ErrorAction SilentlyContinue");
        }
        return sb.ToString();
    }

    /// <summary>
    /// XML de configuração do FortiClient (formato de import completo) com os túneis IPsec.
    /// A PSK vai em TEXTO — o FortiClient aceita texto na importação e cifra internamente.
    /// </summary>
    public static string FortiClientVpnXml(BuildConfig cfg)
    {
        static string Esc(string s) => System.Security.SecurityElement.Escape(s ?? "") ?? "";

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\" ?>");
        sb.AppendLine("<forticlient_configuration>");
        sb.AppendLine("  <vpn>");
        sb.AppendLine("    <ipsecvpn>");
        sb.AppendLine("      <options><enabled>1</enabled></options>");
        sb.AppendLine("      <connections>");
        foreach (var t in cfg.VpnTunnels)
        {
            if (string.IsNullOrWhiteSpace(t.Name)) continue;
            sb.AppendLine("        <connection>");
            sb.AppendLine($"          <name>{Esc(t.Name)}</name>");
            sb.AppendLine("          <type>manual</type>");
            sb.AppendLine("          <ike_settings>");
            sb.AppendLine("            <version>1</version>");
            sb.AppendLine($"            <server>{Esc(t.RemoteGateway)}</server>");
            sb.AppendLine("            <authentication_method>Preshared Key</authentication_method>");
            sb.AppendLine("            <auth_data>");
            sb.AppendLine($"              <preshared_key>{Esc(t.PresharedKey)}</preshared_key>");
            sb.AppendLine("            </auth_data>");
            sb.AppendLine("            <mode>aggressive</mode>");
            sb.AppendLine("            <dhgroup>5;14;</dhgroup>");
            sb.AppendLine("            <key_life>28800</key_life>");
            sb.AppendLine("            <nat_traversal>1</nat_traversal>");
            sb.AppendLine("            <dpd>1</dpd>");
            sb.AppendLine("            <proposals>");
            sb.AppendLine("              <proposal>AES256|SHA256</proposal>");
            sb.AppendLine("              <proposal>AES128|SHA256</proposal>");
            sb.AppendLine("              <proposal>AES256|SHA1</proposal>");
            sb.AppendLine("              <proposal>AES128|SHA1</proposal>");
            sb.AppendLine("              <proposal>3DES|SHA1</proposal>");
            sb.AppendLine("            </proposals>");
            // XAuth (autenticacao de usuario): Prompt pede no login, Save salva, Disabled desliga.
            sb.AppendLine("            <xauth>");
            if (cfg.VpnXAuth == VpnXAuthMode.Disabled)
            {
                sb.AppendLine("              <enabled>0</enabled>");
            }
            else if (cfg.VpnXAuth == VpnXAuthMode.Save)
            {
                sb.AppendLine("              <enabled>1</enabled>");
                sb.AppendLine("              <prompt_username>0</prompt_username>");
                sb.AppendLine($"              <username>{Esc(cfg.XAuthUsername)}</username>");
                sb.AppendLine($"              <password>{Esc(cfg.XAuthPassword)}</password>");
                sb.AppendLine("              <attempts_allowed>3</attempts_allowed>");
            }
            else // Prompt (padrao): pede usuario/senha ao conectar
            {
                sb.AppendLine("              <enabled>1</enabled>");
                sb.AppendLine("              <prompt_username>1</prompt_username>");
                sb.AppendLine("              <attempts_allowed>3</attempts_allowed>");
            }
            sb.AppendLine("            </xauth>");
            sb.AppendLine("          </ike_settings>");
            sb.AppendLine("          <ipsec_settings>");
            sb.AppendLine("            <dhgroup>5;14;</dhgroup>");
            sb.AppendLine("            <key_life_type>seconds</key_life_type>");
            sb.AppendLine("            <key_life_seconds>1800</key_life_seconds>");
            sb.AppendLine("            <replay_detection>1</replay_detection>");
            sb.AppendLine("            <pfs>1</pfs>");
            sb.AppendLine("            <proposals>");
            sb.AppendLine("              <proposal>AES256|SHA256</proposal>");
            sb.AppendLine("              <proposal>AES128|SHA256</proposal>");
            sb.AppendLine("              <proposal>AES256|SHA1</proposal>");
            sb.AppendLine("              <proposal>AES128|SHA1</proposal>");
            sb.AppendLine("              <proposal>3DES|SHA1</proposal>");
            sb.AppendLine("            </proposals>");
            sb.AppendLine("          </ipsec_settings>");
            sb.AppendLine("        </connection>");
        }
        sb.AppendLine("      </connections>");
        sb.AppendLine("    </ipsecvpn>");
        sb.AppendLine("  </vpn>");
        sb.AppendLine("</forticlient_configuration>");
        return sb.ToString();
    }

    public static bool HasFortiConfig(BuildConfig cfg)
        => cfg.VpnTunnels.Count > 0 || !string.IsNullOrWhiteSpace(cfg.FortiClientRegImportPath);
}
