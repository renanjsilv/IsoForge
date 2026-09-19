using System.Text;
using IsoForge.Models;

namespace IsoForge.Core;

/// <summary>
/// Gera o Debloat.ps1 (executado no 1º logon): remove apps de fábrica, desliga telemetria,
/// tira propagandas do Iniciar, desativa Copilot, remove OneDrive/Teams — conforme escolhido.
/// ASCII puro (evita quebrar o PowerShell 5.1 com acentos).
/// </summary>
public static class DebloatGenerator
{
    public const string FileName = "Debloat.ps1";

    public static bool Has(BuildConfig c) =>
        c.DebloatRemoveApps || c.DebloatDisableTelemetry || c.DebloatRemoveOneDrive ||
        c.DebloatDisableStartAds || c.DebloatDisableCopilot || c.DebloatRemoveTeamsChat;

    /// <summary>
    /// Os apps escolhidos para remoção. A lista vem do <see cref="BloatCatalog"/>, item
    /// a item — nada fora do que foi marcado é tocado, e nada além do catálogo existe.
    ///
    /// Uma configuração salva antes da lista por item não tem IDs: ali vale o conjunto
    /// padrão, que é exatamente o que a opção única removia. Sem esse retorno, quem
    /// atualizasse o IsoForge veria a opção marcada e nenhum app sendo removido.
    /// </summary>
    static IEnumerable<string> Escolhidos(BuildConfig c) =>
        c.DebloatAppIds.Count > 0 ? c.DebloatAppIds : BloatCatalog.Padrao;

    public static string Generate(BuildConfig c)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# IsoForge - Otimizacao (debloat) no 1o logon");
        sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        sb.AppendLine();

        if (c.DebloatRemoveApps || c.DebloatRemoveTeamsChat || c.DebloatDisableCopilot)
        {
            var apps = new List<string>();
            if (c.DebloatRemoveApps) apps.AddRange(Escolhidos(c));
            if (c.DebloatRemoveTeamsChat) apps.Add("MicrosoftTeams");
            if (c.DebloatDisableCopilot) { apps.Add("Microsoft.Copilot"); apps.Add("Microsoft.Windows.Ai.Copilot.Provider"); }
            var list = string.Join(",", apps.Distinct().Select(a => "'" + a + "'"));
            sb.AppendLine("# Remove apps de fabrica (do usuario e do provisionamento p/ novos usuarios)");
            sb.AppendLine($"$apps = @({list})");
            // A lista de pacotes provisionados e lida UMA vez. Antes,
            // Get-AppxProvisionedPackage -Online era chamado dentro do laco: ele enumera
            // a imagem inteira a cada chamada, entao com 27 apps eram 27 varreduras
            // completas. Numa maquina recem-instalada isso ja e lento; no Windows
            // Sandbox passa de varios minutos sem nada aparecer na tela — parecia
            // travamento no fim do provisionamento.
            sb.AppendLine("$prov = Get-AppxProvisionedPackage -Online");
            sb.AppendLine("foreach ($a in $apps) {");
            sb.AppendLine("  Get-AppxPackage -AllUsers -Name $a | Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue");
            sb.AppendLine("  $prov | Where-Object { $_.DisplayName -eq $a } | ForEach-Object { Remove-AppxProvisionedPackage -Online -PackageName $_.PackageName -ErrorAction SilentlyContinue | Out-Null }");
            sb.AppendLine("  Write-Output \"removido (se presente): $a\"");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        if (c.DebloatDisableTelemetry)
        {
            sb.AppendLine("# Telemetria no minimo + desliga a coleta (DiagTrack)");
            sb.AppendLine("New-Item -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection' -Force | Out-Null");
            sb.AppendLine("Set-ItemProperty 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection' -Name 'AllowTelemetry' -Type DWord -Value 0 -Force");
            sb.AppendLine("Stop-Service -Name DiagTrack -Force -ErrorAction SilentlyContinue");
            sb.AppendLine("Set-Service -Name DiagTrack -StartupType Disabled -ErrorAction SilentlyContinue");
            sb.AppendLine("Write-Output 'Telemetria reduzida (DiagTrack desabilitado).'");
            sb.AppendLine();
        }

        if (c.DebloatDisableStartAds)
        {
            sb.AppendLine("# Tira sugestoes/propagandas do Iniciar e da timeline (usuario atual + hive padrao)");
            sb.AppendLine("$cdm = 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\ContentDeliveryManager'");
            sb.AppendLine("New-Item -Path $cdm -Force | Out-Null");
            sb.AppendLine("foreach ($v in 'SystemPaneSuggestionsEnabled','SilentInstalledAppsEnabled','SubscribedContent-338388Enabled','SubscribedContent-338389Enabled','SubscribedContent-353694Enabled','SubscribedContent-353696Enabled','PreInstalledAppsEnabled','OemPreInstalledAppsEnabled') {");
            sb.AppendLine("  Set-ItemProperty $cdm -Name $v -Type DWord -Value 0 -Force");
            sb.AppendLine("}");
            sb.AppendLine("Write-Output 'Sugestoes/propagandas do Iniciar desativadas.'");
            sb.AppendLine();
        }

        if (c.DebloatDisableCopilot)
        {
            sb.AppendLine("# Desativa o Windows Copilot (politica)");
            sb.AppendLine("New-Item -Path 'HKCU:\\Software\\Policies\\Microsoft\\Windows\\WindowsCopilot' -Force | Out-Null");
            sb.AppendLine("Set-ItemProperty 'HKCU:\\Software\\Policies\\Microsoft\\Windows\\WindowsCopilot' -Name 'TurnOffWindowsCopilot' -Type DWord -Value 1 -Force");
            sb.AppendLine("New-Item -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsCopilot' -Force | Out-Null");
            sb.AppendLine("Set-ItemProperty 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsCopilot' -Name 'TurnOffWindowsCopilot' -Type DWord -Value 1 -Force");
            sb.AppendLine("Write-Output 'Windows Copilot desativado.'");
            sb.AppendLine();
        }

        if (c.DebloatRemoveTeamsChat)
        {
            sb.AppendLine("# Impede reinstalacao do Chat (Teams pessoal)");
            sb.AppendLine("New-Item -Path 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Chat' -Force | Out-Null");
            sb.AppendLine("Set-ItemProperty 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Chat' -Name 'ChatIcon' -Type DWord -Value 3 -Force");
            sb.AppendLine("Write-Output 'Teams pessoal (Chat) removido/desativado.'");
            sb.AppendLine();
        }

        if (c.DebloatRemoveOneDrive)
        {
            sb.AppendLine("# Desinstala o OneDrive");
            sb.AppendLine("taskkill /f /im OneDrive.exe 2>$null | Out-Null");
            sb.AppendLine("$od = @(\"$env:SystemRoot\\System32\\OneDriveSetup.exe\", \"$env:SystemRoot\\SysWOW64\\OneDriveSetup.exe\") | Where-Object { Test-Path $_ } | Select-Object -First 1");
            sb.AppendLine("if ($od) { Start-Process -FilePath $od -ArgumentList '/uninstall' -Wait -ErrorAction SilentlyContinue }");
            sb.AppendLine("Write-Output 'OneDrive desinstalado.'");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
