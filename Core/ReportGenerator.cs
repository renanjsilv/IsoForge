using System.Text;
using IsoForge.Models;

namespace IsoForge.Core;

/// <summary>
/// Gera o Report.ps1 (executado ao final do 1o logon): monta um relatorio HTML de
/// provisionamento (o que foi configurado + resultado real lido do install.log) e salva
/// na Area de Trabalho publica. ASCII puro no script.
/// </summary>
public static class ReportGenerator
{
    public const string FileName = "Report.ps1";

    public static string Generate(BuildConfig c)
    {
        // Resumo do que o IsoForge configurou (embutido no relatorio).
        var itens = new List<(string, string)>();
        itens.Add(("Modo", c.Mode == DeploymentMode.EntraId ? "Entra ID (corporativo/estudante)" : "Conta local"));
        if (!string.IsNullOrWhiteSpace(c.UserName)) itens.Add(("Usuario local", c.UserName));
        var apps = c.Apps.Select(a => a.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (apps.Count > 0) itens.Add(("Aplicativos", string.Join(", ", apps)));
        if (!string.IsNullOrWhiteSpace(c.DriverModelName)) itens.Add(("Drivers do fabricante", c.DriverModelName));
        if (!string.IsNullOrWhiteSpace(c.FortiClientRegImportPath) || c.VpnTunnels.Count > 0) itens.Add(("VPN FortiClient", "configurada"));
        if (c.AutoConnectWifi && !string.IsNullOrWhiteSpace(c.WifiSsid)) itens.Add(("Wi-Fi automatico", c.WifiSsid));
        if (c.WindowsTheme != WindowsThemeMode.Default) itens.Add(("Tema do Windows", c.WindowsTheme == WindowsThemeMode.Light ? "Claro" : "Escuro"));
        if (c.TaskbarAlign != TaskbarAlignment.Default) itens.Add(("Barra de tarefas", c.TaskbarAlign == TaskbarAlignment.Left ? "Esquerda" : "Centro"));
        var deb = new List<string>();
        if (c.DebloatRemoveApps) deb.Add("remove apps de fabrica");
        if (c.DebloatDisableTelemetry) deb.Add("telemetria reduzida");
        if (c.DebloatRemoveOneDrive) deb.Add("sem OneDrive");
        if (c.DebloatDisableStartAds) deb.Add("sem propagandas no Iniciar");
        if (c.DebloatDisableCopilot) deb.Add("sem Copilot");
        if (c.DebloatRemoveTeamsChat) deb.Add("sem Teams pessoal");
        if (deb.Count > 0) itens.Add(("Otimizacao", string.Join(", ", deb)));

        static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        var cfgRows = string.Concat(itens.Select(i => $"<tr><td class=k>{Esc(i.Item1)}</td><td>{Esc(i.Item2)}</td></tr>"));

        var sb = new StringBuilder();
        sb.AppendLine("# IsoForge - relatorio de provisionamento (1o logon)");
        sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        sb.AppendLine("$log = 'C:\\Setup\\install.log'");
        sb.AppendLine("$when = Get-Date -Format 'dd/MM/yyyy HH:mm'");
        sb.AppendLine("$os = (Get-CimInstance Win32_OperatingSystem).Caption");
        sb.AppendLine("$hostn = $env:COMPUTERNAME");
        sb.AppendLine("# Le o install.log e pareia [Secao] com 'codigo de saida: N'");
        sb.AppendLine("$rows = ''");
        sb.AppendLine("if (Test-Path $log) {");
        sb.AppendLine("  $cur = $null");
        sb.AppendLine("  foreach ($l in (Get-Content $log)) {");
        sb.AppendLine("    if ($l -match '^\\[(.+)\\]\\s*$') { $cur = $Matches[1] }");
        sb.AppendLine("    elseif (($l -match 'codigo de saida:\\s*(-?\\d+)') -and $cur) {");
        sb.AppendLine("      $rc = [int]$Matches[1]");
        sb.AppendLine("      $ok = ($rc -eq 0 -or $rc -eq 3010 -or $rc -eq 1641)");
        sb.AppendLine("      $st = if ($ok) { 'OK' } else { \"erro (codigo $rc)\" }");
        sb.AppendLine("      $cl = if ($ok) { '#16A34A' } else { '#DC2626' }");
        sb.AppendLine("      $rows += \"<tr><td>$cur</td><td style='color:$cl;font-weight:600'>$st</td></tr>\"");
        sb.AppendLine("      $cur = $null");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine("if (-not $rows) { $rows = \"<tr><td colspan=2>Sem itens com codigo de saida no log.</td></tr>\" }");
        sb.AppendLine();
        // HTML (here-string). O resumo de config vem embutido; o resto e runtime.
        sb.AppendLine("$html = @\"");
        sb.AppendLine("<!doctype html><html><head><meta charset='utf-8'><title>IsoForge - Provisionamento</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;background:#f1f4fb;color:#0f172a;margin:0;padding:24px}");
        sb.AppendLine(".card{max-width:820px;margin:0 auto;background:#fff;border:1px solid #e2e8f0;border-radius:14px;padding:24px}");
        sb.AppendLine("h1{font-size:20px;margin:0 0 4px}.sub{color:#64748b;font-size:13px;margin:0 0 18px}");
        sb.AppendLine("h2{font-size:15px;margin:20px 0 8px}table{width:100%;border-collapse:collapse;font-size:13px}");
        sb.AppendLine("td{padding:7px 10px;border-bottom:1px solid #eef2f7;vertical-align:top}.k{color:#64748b;width:200px}</style></head>");
        sb.AppendLine("<body><div class=card>");
        sb.AppendLine("<h1>IsoForge - Relatorio de Provisionamento</h1>");
        sb.AppendLine("<p class=sub>Maquina: $hostn &middot; $os &middot; $when</p>");
        sb.AppendLine("<h2>Configuracao aplicada</h2><table>" + cfgRows + "</table>");
        sb.AppendLine("<h2>Resultado da instalacao (do log)</h2><table>$rows</table>");
        sb.AppendLine("<p class=sub style='margin-top:18px'>Gerado automaticamente pelo IsoForge. Log completo em C:\\Setup\\install.log.</p>");
        sb.AppendLine("</div></body></html>");
        sb.AppendLine("\"@");
        sb.AppendLine();
        sb.AppendLine("$dest = Join-Path $env:PUBLIC 'Desktop\\IsoForge-Provisionamento.html'");
        sb.AppendLine("Set-Content -Path $dest -Value $html -Encoding UTF8");
        sb.AppendLine("Set-Content -Path 'C:\\Setup\\IsoForge-Provisionamento.html' -Value $html -Encoding UTF8");
        sb.AppendLine("Write-Output \"Relatorio salvo em $dest\"");
        return sb.ToString();
    }
}
