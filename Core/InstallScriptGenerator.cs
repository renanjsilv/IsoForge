using System.IO;
using System.Text;
using IsoForge.Models;

namespace IsoForge.Core;

/// <summary>
/// Gera o C:\Setup\install.cmd executado no primeiro logon (via FirstLogonCommands).
/// Instala os aplicativos em modo silencioso, ajusta a expiração de senha do
/// usuário e chama o script personalizado, gravando tudo em C:\Setup\install.log.
/// </summary>
public static class InstallScriptGenerator
{
    public const string SetupDirOnDisk = @"C:\Setup";
    public const string AppsDirOnDisk = @"C:\Setup\Apps";

    public static string Generate(BuildConfig c) => Generate(c, goldenAudit: false);

    /// <summary>
    /// Gera o script de instalação. Em <paramref name="goldenAudit"/> (imagem golden),
    /// roda no modo de auditoria: instala apps + wallpaper + FortiClient, pula a
    /// definição de usuário/hostname (o usuário ainda não existe) e termina com
    /// sysprep /generalize /oobe /shutdown para permitir a captura.
    /// </summary>
    public static string Generate(BuildConfig c, bool goldenAudit)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine("setlocal");
        sb.AppendLine($"set \"LOGFILE={SetupDirOnDisk}\\install.log\"");
        sb.AppendLine("echo ================================================>> \"%LOGFILE%\"");
        sb.AppendLine("echo IsoForge - inicio: %date% %time%>> \"%LOGFILE%\"");
        sb.AppendLine("title IsoForge - Instalando aplicativos padrao, aguarde...");
        sb.AppendLine("echo Instalando aplicativos padrao. NAO feche esta janela.");
        sb.AppendLine();

        // Idempotência: o FirstLogonCommands é re-executado pelo Windows se a máquina reiniciar
        // antes de ele concluir (é o que acontece na seleção de unidade). O marcador evita repetir
        // tudo (e o loop de "pedir a unidade de novo") no boot seguinte.
        if (!goldenAudit)
        {
            sb.AppendLine($"if exist \"{SetupDirOnDisk}\\install.done\" (");
            sb.AppendLine("  echo IsoForge: instalacao ja concluida anteriormente; saindo.>> \"%LOGFILE%\"");
            sb.AppendLine("  exit /b 0");
            sb.AppendLine(")");
            sb.AppendLine();
        }

        // Seleção de unidade no 1º logon (sem auditoria): mostra a tela e renomeia a máquina.
        // A reinicialização que aplica o nome acontece no fim deste script.
        if (!goldenAudit && c.UseUnitSelection && c.UnitMethod == UnitSelectionMethod.FirstLogon)
        {
            sb.AppendLine("echo [Selecao de unidade]>> \"%LOGFILE%\"");
            sb.AppendLine("echo Selecione a unidade na tela que abriu (define o nome do computador)...");
            sb.AppendLine($"powershell -NoProfile -ExecutionPolicy Bypass -File \"{SetupDirOnDisk}\\{UnitSelectorGenerator.FileName}\">> \"%LOGFILE%\" 2>&1");
            sb.AppendLine();
        }

        AppendAppInstalls(sb, c);
        AppendAppearance(sb, c);
        AppendForti(sb, c);

        // Expiração de senha do usuário local (não no modo golden — usuário ainda não existe)
        if (!goldenAudit && !string.IsNullOrWhiteSpace(c.UserName))
        {
            var flag = c.PasswordNeverExpires ? "$true" : "$false";
            sb.AppendLine("echo [Senha do usuario]>> \"%LOGFILE%\"");
            // O usuário é criado pelo OOBE (autounattend). No teste do Sandbox ele não existe,
            // então só ajusta se existir — evita o erro "User not found" no log.
            sb.AppendLine($"powershell -NoProfile -ExecutionPolicy Bypass -Command \"" +
                $"if (Get-LocalUser -Name '{c.UserName}' -ErrorAction SilentlyContinue) {{ Set-LocalUser -Name '{c.UserName}' -PasswordNeverExpires {flag} }} " +
                $"else {{ Write-Output 'Usuario {c.UserName} ainda nao existe (criado pelo OOBE no ISO real); ignorado.' }}\">> \"%LOGFILE%\" 2>&1");
            sb.AppendLine();
        }

        // Script personalizado (não no modo golden — roda no deploy final)
        if (!goldenAudit)
            AppendPostScript(sb, c);

        sb.AppendLine("echo IsoForge - fim: %date% %time%>> \"%LOGFILE%\"");

        if (goldenAudit)
        {
            // Modo golden: generaliza e desliga para a captura do install.wim.
            // O modo de auditoria abre a janela do Sysprep sozinho; é preciso fechá-la,
            // senão o nosso sysprep falha com "já em execução em outra janela".
            sb.AppendLine("echo [Sysprep] fechando a janela do Sysprep do modo auditoria...>> \"%LOGFILE%\"");
            sb.AppendLine("taskkill /f /im sysprep.exe >nul 2>&1");
            sb.AppendLine("ping -n 3 127.0.0.1 >nul");
            sb.AppendLine("echo [Sysprep] generalizando para captura...>> \"%LOGFILE%\"");
            sb.AppendLine("%windir%\\System32\\Sysprep\\sysprep.exe /generalize /oobe /shutdown");
            sb.AppendLine("exit /b 0");
        }
        else
        {
            sb.AppendLine("echo Concluido. O log esta em %LOGFILE%");
            // Marca como concluído ANTES de reiniciar: no boot seguinte o FirstLogonCommands
            // re-executa este script, que vê o marcador e sai (sem repetir/loopar).
            sb.AppendLine($"> \"{SetupDirOnDisk}\\install.done\" echo %date% %time%");
            // Seleção de unidade no 1º logon: reinicia uma vez para aplicar o novo nome.
            // No teste do Sandbox o reboot é pulado (o Sandbox não reinicia) — a tela e o
            // renomear acontecem, só não há reboot, para o teste seguir mostrando os apps.
            if (c.UseUnitSelection && c.UnitMethod == UnitSelectionMethod.FirstLogon && c.SandboxTest)
            {
                sb.AppendLine("echo [Teste/Sandbox] reboot pulado (no ISO real a maquina reiniciaria aqui).>> \"%LOGFILE%\"");
            }
            else if (c.UseUnitSelection && c.UnitMethod == UnitSelectionMethod.FirstLogon)
            {
                sb.AppendLine("echo [Reinicio] aplicando o nome do computador...>> \"%LOGFILE%\"");
                sb.AppendLine("shutdown /r /t 5 /c \"IsoForge: aplicando o nome do computador\"");
            }
            sb.AppendLine("exit /b 0");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Instala os aplicativos em modo silencioso (partilhado por install.cmd e SetupComplete.cmd).
    /// Assume que os instaladores já estão em C:\Setup\Apps e que %LOGFILE% está definido.
    /// </summary>
    internal static void AppendAppInstalls(StringBuilder sb, BuildConfig c)
    {
        foreach (var app in c.Apps)
        {
            if (string.IsNullOrWhiteSpace(app.InstallerPath))
                continue;

            var fileName = Path.GetFileName(app.InstallerPath);
            sb.AppendLine($"echo [{app.Name}]>> \"%LOGFILE%\"");
            sb.AppendLine($"echo Instalando {app.Name}...");
            AppendWaitMsi(sb); // espera o Windows Installer ficar livre (evita erro 1618)

            if (app.Kind == AppKind.Office)
            {
                // Diagnostico: registra se ha fonte offline embutida ou se vai baixar da internet.
                sb.AppendLine($"if exist \"{AppsDirOnDisk}\\Office\\Office\\Data\" (echo   Office OFFLINE: fonte local presente>> \"%LOGFILE%\") else (echo   Office ONLINE: sem fonte local, vai BAIXAR da internet>> \"%LOGFILE%\")");
                sb.AppendLine($"\"{AppsDirOnDisk}\\Office\\setup.exe\" /configure \"{AppsDirOnDisk}\\Office\\Configuration.xml\">> \"%LOGFILE%\" 2>&1");
                // O setup.exe pode RETORNAR antes de o Click-to-Run terminar (instala em segundo
                // plano). Sem esperar, um reboot posterior truncaria o Office -> nao instala.
                // Aguarda o Click-to-Run concluir (ClientVersionToReport aparece ao finalizar).
                sb.AppendLine("echo   aguardando o Office concluir (Click-to-Run)...>> \"%LOGFILE%\"");
                sb.AppendLine("powershell -NoProfile -ExecutionPolicy Bypass -Command \"" +
                    "for($i=0;$i -lt 160;$i++){ " +
                    "$c=Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Office\\ClickToRun\\Configuration' -ErrorAction SilentlyContinue; " +
                    "if($c -and $c.ClientVersionToReport){ Write-Output ('Office instalado: '+$c.ClientVersionToReport); break }; " +
                    "Start-Sleep -Seconds 15 }\">> \"%LOGFILE%\" 2>&1");
            }
            else if (fileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            {
                var args = string.IsNullOrWhiteSpace(app.SilentArgs) ? "/qn /norestart" : app.SilentArgs.Trim();
                sb.AppendLine($"msiexec /i \"{AppsDirOnDisk}\\{fileName}\" {args}>> \"%LOGFILE%\" 2>&1");
            }
            else
            {
                var args = string.IsNullOrWhiteSpace(app.SilentArgs) ? "/S" : app.SilentArgs.Trim();
                sb.AppendLine($"\"{AppsDirOnDisk}\\{fileName}\" {args}>> \"%LOGFILE%\" 2>&1");
            }
            sb.AppendLine($"echo   codigo de saida: %errorlevel%>> \"%LOGFILE%\"");
            sb.AppendLine();
        }
    }

    /// <summary>
    /// Espera o Windows Installer ficar livre antes de instalar o próximo app. Detecta uma
    /// instalação MSI ativa pelo mutex Global\_MSIExecute (existe só durante um install) e
    /// evita o erro 1618 ("outra instalação em andamento"), que fez o Adobe falhar.
    /// </summary>
    internal static void AppendWaitMsi(StringBuilder sb)
    {
        sb.AppendLine("powershell -NoProfile -ExecutionPolicy Bypass -Command \"" +
            "for($i=0;$i -lt 120;$i++){ try { $m=[System.Threading.Mutex]::OpenExisting('Global\\_MSIExecute'); $m.Dispose(); Start-Sleep -Seconds 5 } catch { break } }\" >nul 2>&1");
    }

    /// <summary>Aparência: papel de parede + tela de bloqueio (chama o Set-Appearance.ps1 em C:\Setup).</summary>
    internal static void AppendAppearance(StringBuilder sb, BuildConfig c)
    {
        if (!ExtraScriptsGenerator.HasAppearance(c)) return;
        sb.AppendLine("echo [Aparencia: papel de parede / tela de bloqueio]>> \"%LOGFILE%\"");
        sb.AppendLine($"powershell -NoProfile -ExecutionPolicy Bypass -File \"{SetupDirOnDisk}\\{ExtraScriptsGenerator.AppearanceFileName}\">> \"%LOGFILE%\" 2>&1");
        sb.AppendLine();
    }

    /// <summary>FortiClient VPN (chama o Configure-FortiClient.ps1 em C:\Setup).</summary>
    internal static void AppendForti(StringBuilder sb, BuildConfig c)
    {
        if (!ExtraScriptsGenerator.HasFortiConfig(c)) return;
        sb.AppendLine("echo [FortiClient VPN]>> \"%LOGFILE%\"");
        sb.AppendLine($"powershell -NoProfile -ExecutionPolicy Bypass -File \"{SetupDirOnDisk}\\{ExtraScriptsGenerator.FortiFileName}\">> \"%LOGFILE%\" 2>&1");
        sb.AppendLine();
    }

    /// <summary>Script personalizado do primeiro logon (.ps1/.cmd/.bat em C:\Setup).</summary>
    internal static void AppendPostScript(StringBuilder sb, BuildConfig c)
    {
        if (string.IsNullOrWhiteSpace(c.PostScriptPath)) return;
        var scriptFile = Path.GetFileName(c.PostScriptPath);
        sb.AppendLine("echo [Script personalizado]>> \"%LOGFILE%\"");
        if (scriptFile.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine($"powershell -NoProfile -ExecutionPolicy Bypass -File \"{SetupDirOnDisk}\\{scriptFile}\">> \"%LOGFILE%\" 2>&1");
        else
            sb.AppendLine($"call \"{SetupDirOnDisk}\\{scriptFile}\">> \"%LOGFILE%\" 2>&1");
        sb.AppendLine($"echo   codigo de saida: %errorlevel%>> \"%LOGFILE%\"");
        sb.AppendLine();
    }

    public static void WriteTo(BuildConfig c, string filePath)
    {
        // UTF-8 sem BOM + chcp 65001 no início do script evita problemas de acentuação no cmd
        File.WriteAllText(filePath, Generate(c), new UTF8Encoding(false));
    }

    public static void WriteGoldenTo(BuildConfig c, string filePath)
    {
        File.WriteAllText(filePath, Generate(c, goldenAudit: true), new UTF8Encoding(false));
    }
}
