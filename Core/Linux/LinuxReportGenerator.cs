using System.Text;
using IsoForge.Models;

namespace IsoForge.Core.Linux;

/// <summary>
/// Gera o <c>isoforge-report.sh</c>: ao final da pós-instalação escreve um relatório HTML
/// com o que foi provisionado (sistema, usuário, aplicativos, otimizações) na área de trabalho
/// pública e em /var/log. Equivalente Linux do <see cref="ReportGenerator"/> do Windows.
/// </summary>
public static class LinuxReportGenerator
{
    public const string FileName = "isoforge-report.sh";

    static string E(string? s) => LinuxPostInstall.Ascii(s)
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public static string Generate(BuildConfig c)
    {
        var os = OsCatalog.Get(c.Os);
        var apps = LinuxPostInstall.SelectedRecipes(c);

        var rows = new StringBuilder();
        void Row(string label, string value) =>
            rows.AppendLine($"<tr><th>{E(label)}</th><td>{E(value)}</td></tr>");

        Row("Sistema", os.Name);
        Row("Usuario", c.UserName);
        if (!string.IsNullOrWhiteSpace(c.Linux.FullName)) Row("Nome completo", c.Linux.FullName);
        Row("Nome da maquina", c.UseUnitSelection ? "definido pela unidade escolhida" : c.ComputerName);
        Row("Fuso horario", c.Linux.Timezone);
        Row("Idioma", c.Linux.LocaleId);
        Row("Teclado", c.Linux.KeyboardLayout + (string.IsNullOrWhiteSpace(c.Linux.KeyboardVariant) ? "" : " (" + c.Linux.KeyboardVariant + ")"));
        Row("Disco", c.Linux.DiskMode switch
        {
            LinuxDiskMode.EntireDiskLvm => "disco inteiro com LVM",
            LinuxDiskMode.EntireDiskEncrypted => "disco inteiro com LVM + criptografia LUKS",
            _ => "disco inteiro (ESP + raiz ext4)"
        });
        Row("Servidor SSH", c.Linux.InstallSshServer ? "instalado" : "nao");
        Row("Flatpak/Flathub", c.Linux.EnableFlatpak ? "habilitado" : "nao");
        Row("Drivers proprietarios", c.Linux.ProprietaryDrivers ? "instalados" : "nao");

        var appItems = new StringBuilder();
        if (apps.Count == 0)
            appItems.AppendLine("<li>Nenhum programa selecionado.</li>");
        else
            foreach (var a in apps)
                appItems.AppendLine($"<li><b>{E(a.DisplayName)}</b>{(string.IsNullOrWhiteSpace(a.Note) ? "" : $" <span class='nota'>{E(a.Note)}</span>")}</li>");

        var optimizations = new List<string>();
        if (c.Linux.RemoveDefaultGames) optimizations.Add("jogos de fabrica removidos");
        if (c.Linux.DisableTelemetry) optimizations.Add("coleta de dados desativada");
        if (c.Linux.RemoveSnap) optimizations.Add("snapd removido");
        if (c.Linux.DisableMotdAds) optimizations.Add("mensagens promocionais do MOTD desativadas");
        var optText = optimizations.Count == 0 ? "nenhuma" : string.Join(", ", optimizations);

        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine("# IsoForge: relatorio de provisionamento (HTML).");
        sb.AppendLine("set -u");
        sb.AppendLine("OUT_DIR=/var/log/isoforge");
        sb.AppendLine("mkdir -p \"$OUT_DIR\"");
        sb.AppendLine("REPORT=\"$OUT_DIR/IsoForge-Provisionamento.html\"");
        sb.AppendLine();
        sb.AppendLine("LOG=" + LinuxPostInstall.Sh(LinuxPostInstall.LogPath));
        sb.AppendLine("AVISOS=$(grep -c 'AVISO' \"$LOG\" 2>/dev/null || echo 0)");
        sb.AppendLine("HOSTNAME_ATUAL=$(hostnamectl --static 2>/dev/null || hostname)");
        sb.AppendLine("KERNEL=$(uname -r)");
        sb.AppendLine("DISTRO=$(. /etc/os-release 2>/dev/null && echo \"$PRETTY_NAME\")");
        sb.AppendLine("QUANDO=$(date '+%d/%m/%Y %H:%M')");
        sb.AppendLine("if [ \"$AVISOS\" -gt 0 ] 2>/dev/null; then CLASSE=warn; else CLASSE=ok; fi");
        sb.AppendLine();
        sb.AppendLine("cat > \"$REPORT\" <<ISOFORGE_HTML_EOF");
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html lang=\"pt-BR\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<title>IsoForge - Relatorio de provisionamento</title><style>");
        sb.AppendLine("body{font-family:system-ui,Segoe UI,sans-serif;background:#f1f4fb;color:#0f172a;margin:0;padding:32px}");
        sb.AppendLine(".card{max-width:840px;margin:0 auto;background:#fff;border:1px solid #e5eaf3;border-radius:14px;padding:28px}");
        sb.AppendLine("h1{margin:0 0 4px;font-size:22px}h2{font-size:15px;margin:26px 0 10px;color:#334155}");
        sb.AppendLine(".sub{color:#64748b;font-size:13px;margin-bottom:18px}");
        sb.AppendLine("table{border-collapse:collapse;width:100%}th{text-align:left;width:210px;color:#64748b;font-weight:600}");
        sb.AppendLine("th,td{padding:7px 8px;border-bottom:1px solid #eef2f7;font-size:13.5px;vertical-align:top}");
        sb.AppendLine("ul{margin:0;padding-left:20px;font-size:13.5px}li{margin:5px 0}");
        sb.AppendLine(".nota{color:#64748b;font-size:12px}");
        sb.AppendLine(".ok{color:#15803d;font-weight:600}.warn{color:#b45309;font-weight:600}");
        sb.AppendLine("</style></head><body><div class=\"card\">");
        sb.AppendLine("<h1>IsoForge &mdash; provisionamento concluido</h1>");
        sb.AppendLine("<div class=\"sub\">$DISTRO &middot; kernel $KERNEL &middot; maquina <b>$HOSTNAME_ATUAL</b> &middot; $QUANDO</div>");
        sb.AppendLine("<h2>Configuracao</h2><table>");
        sb.Append(rows);
        sb.AppendLine("</table>");
        sb.AppendLine("<h2>Aplicativos instalados</h2><ul>");
        sb.Append(appItems);
        sb.AppendLine("</ul>");
        sb.AppendLine($"<h2>Otimizacao</h2><p style=\"font-size:13.5px\">{E(optText)}</p>");
        sb.AppendLine("<h2>Resultado</h2><table>");
        sb.AppendLine("<tr><th>Avisos no log</th><td class=\"$CLASSE\">$AVISOS</td></tr>");
        sb.AppendLine("<tr><th>Log completo</th><td>$LOG</td></tr>");
        sb.AppendLine("</table>");
        sb.AppendLine("</div></body></html>");
        sb.AppendLine("ISOFORGE_HTML_EOF");
        sb.AppendLine();
        sb.AppendLine("chmod 644 \"$REPORT\"");
        sb.AppendLine("# Copia para a area de trabalho de cada usuario real (respeita o nome traduzido da pasta).");
        sb.AppendLine("for home in /home/*; do");
        sb.AppendLine("  [ -d \"$home\" ] || continue");
        sb.AppendLine("  usuario=$(basename \"$home\")");
        sb.AppendLine("  desktop=$(sudo -u \"$usuario\" xdg-user-dir DESKTOP 2>/dev/null)");
        sb.AppendLine("  if [ -z \"$desktop\" ] || [ ! -d \"$desktop\" ]; then desktop=\"$home/Desktop\"; fi");
        sb.AppendLine("  mkdir -p \"$desktop\" 2>/dev/null || continue");
        sb.AppendLine("  cp -f \"$REPORT\" \"$desktop/\" 2>/dev/null || true");
        sb.AppendLine("  chown \"$usuario\":\"$usuario\" \"$desktop/IsoForge-Provisionamento.html\" 2>/dev/null || true");
        sb.AppendLine("done");
        sb.AppendLine("echo \"IsoForge: relatorio gerado em $REPORT\"");
        return sb.ToString();
    }
}
