using System.IO;
using System.Text;
using IsoForge.Models;

namespace IsoForge.Core;

/// <summary>
/// Modo Entra ID: gera o SetupComplete.cmd (executado pelo Windows Setup como SYSTEM,
/// ANTES do OOBE) que cria o usuário local administrador, instala os aplicativos,
/// aplica a aparência e agenda a remoção do usuário Entra do grupo Administradores.
///
/// Assim o 1º boot mostra o OOBE com login corporativo/estudante (Entra ID): a pessoa
/// conecta o WiFi, entra com o e-mail e ingressa no Entra ID como usuário PADRÃO — os
/// programas já estão instalados e o único administrador é o usuário local criado aqui.
/// </summary>
public static class SetupCompleteGenerator
{
    // Nome fixo exigido pelo Windows: %WINDIR%\Setup\Scripts\SetupComplete.cmd
    public const string SetupCompleteFileName = "SetupComplete.cmd";
    public const string CreateUserFileName = "Create-LocalUser.ps1";
    public const string DemoteFileName = "Demote-EntraAdmin.ps1";
    public const string RegisterTaskFileName = "Register-DemoteTask.ps1";
    public const string RenameUnitFileName = "RenameUnit.ps1";
    public const string RegisterUnitTasksFileName = "Register-UnitTasks.ps1";

    const string SetupDir = InstallScriptGenerator.SetupDirOnDisk; // C:\Setup

    /// <summary>SetupComplete.cmd — roda como SYSTEM ao fim da instalação, antes do OOBE.</summary>
    public static string SetupComplete(BuildConfig c)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        sb.AppendLine("setlocal");
        sb.AppendLine($"set \"LOGFILE={SetupDir}\\install.log\"");
        sb.AppendLine("echo ================================================>> \"%LOGFILE%\"");
        sb.AppendLine("echo IsoForge (Entra ID / SetupComplete como SYSTEM) - inicio: %date% %time%>> \"%LOGFILE%\"");
        sb.AppendLine();

        // 1) Usuário local administrador (o único admin da máquina)
        if (!string.IsNullOrWhiteSpace(c.UserName))
        {
            sb.AppendLine("echo [Usuario local administrador]>> \"%LOGFILE%\"");
            sb.AppendLine($"powershell -NoProfile -ExecutionPolicy Bypass -File \"{SetupDir}\\{CreateUserFileName}\">> \"%LOGFILE%\" 2>&1");
            sb.AppendLine();
        }

        // 2) Aplicativos, aparência e VPN (mesmos helpers do install.cmd)
        InstallScriptGenerator.AppendDrivers(sb, c);
        InstallScriptGenerator.AppendWifi(sb, c);
        InstallScriptGenerator.AppendAppInstalls(sb, c);
        InstallScriptGenerator.AppendAppearance(sb, c);
        InstallScriptGenerator.AppendForti(sb, c);
        InstallScriptGenerator.AppendDebloat(sb, c);
        InstallScriptGenerator.AppendPostScript(sb, c);
        InstallScriptGenerator.AppendReport(sb, c);

        // 3) Seleção de unidade no 1º logon (Entra ID): agenda a tela + o rename (SYSTEM).
        if (c.UseUnitSelection)
        {
            sb.AppendLine("echo [Selecao de unidade - agendando tela no 1o logon]>> \"%LOGFILE%\"");
            sb.AppendLine($"powershell -NoProfile -ExecutionPolicy Bypass -File \"{SetupDir}\\{RegisterUnitTasksFileName}\">> \"%LOGFILE%\" 2>&1");
            sb.AppendLine();
        }

        // 4) Agenda a remoção do usuário Entra do grupo Administradores no 1º logon
        if (c.DemoteEntraJoiner)
        {
            sb.AppendLine("echo [Agendando remocao do admin do usuario Entra no 1o logon]>> \"%LOGFILE%\"");
            sb.AppendLine($"powershell -NoProfile -ExecutionPolicy Bypass -File \"{SetupDir}\\{RegisterTaskFileName}\">> \"%LOGFILE%\" 2>&1");
            sb.AppendLine();
        }

        sb.AppendLine("echo IsoForge (Entra ID) - fim: %date% %time%>> \"%LOGFILE%\"");
        sb.AppendLine("exit /b 0");
        return sb.ToString();
    }

    /// <summary>Create-LocalUser.ps1 — cria/atualiza o usuário local e o coloca (ou não) como admin.</summary>
    public static string CreateUser(BuildConfig c)
    {
        var user = (c.UserName ?? "").Replace("'", "''");
        var pass = (c.Password ?? "").Replace("'", "''");
        var neverExpires = c.PasswordNeverExpires ? "$true" : "$false";
        var hasPassword = !string.IsNullOrEmpty(c.Password);

        var sb = new StringBuilder();
        sb.AppendLine("# IsoForge - cria o usuario local (modo Entra ID; roda como SYSTEM)");
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$user = '{user}'");
        sb.AppendLine("# Grupo Administradores pelo SID (independe do idioma do Windows)");
        sb.AppendLine("$adminGroup = (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')).Translate([System.Security.Principal.NTAccount]).Value.Split('\\')[-1]");
        sb.AppendLine("$existing = Get-LocalUser -Name $user -ErrorAction SilentlyContinue");
        if (hasPassword)
        {
            sb.AppendLine($"$sec = ConvertTo-SecureString '{pass}' -AsPlainText -Force");
            sb.AppendLine("if ($existing) { Set-LocalUser -Name $user -Password $sec }");
            sb.AppendLine("else { New-LocalUser -Name $user -Password $sec -FullName $user -Description 'IsoForge' | Out-Null }");
        }
        else
        {
            sb.AppendLine("if (-not $existing) { New-LocalUser -Name $user -NoPassword -FullName $user -Description 'IsoForge' | Out-Null }");
        }
        sb.AppendLine($"Set-LocalUser -Name $user -PasswordNeverExpires {neverExpires}");
        if (c.IsAdministrator)
        {
            sb.AppendLine("try { Add-LocalGroupMember -Group $adminGroup -Member $user -ErrorAction Stop }");
            sb.AppendLine("catch { if ($_.Exception.Message -notmatch 'already a member|já é membro') { throw } }");
            sb.AppendLine("Write-Output \"Usuario '$user' criado como administrador local.\"");
        }
        else
        {
            sb.AppendLine("Write-Output \"Usuario '$user' criado como usuario padrao.\"");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Register-UnitTasks.ps1 — no modo Entra ID, agenda duas tarefas no logon:
    /// (1) IsoForge-SelectUnit roda a tela na sessão do usuário (padrão, sem admin) e grava a
    /// escolha; (2) IsoForge-RenameUnit (SYSTEM) renomeia e reinicia quando a escolha aparece.
    /// </summary>
    public static string RegisterUnitTasks()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# IsoForge - agenda a selecao de unidade no 1o logon (modo Entra ID)");
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("# Pasta de coordenacao com escrita para os usuarios (a tela roda como usuario padrao).");
        sb.AppendLine("$dir = Join-Path $env:ProgramData 'IsoForge'");
        sb.AppendLine("New-Item -ItemType Directory -Force $dir | Out-Null");
        sb.AppendLine("icacls $dir /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null   # Usuarios: modificar");
        sb.AppendLine();
        sb.AppendLine("# Tarefa 1: mostra a tela de selecao na sessao interativa do usuario (sem admin).");
        sb.AppendLine($"$a1 = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{SetupDir}\\{UnitSelectorGenerator.FileName}\"'");
        sb.AppendLine("$t1 = New-ScheduledTaskTrigger -AtLogOn");
        sb.AppendLine("$p1 = New-ScheduledTaskPrincipal -GroupId 'S-1-5-32-545' -RunLevel Limited   # BUILTIN\\Users (interativo)");
        sb.AppendLine("Register-ScheduledTask -TaskName 'IsoForge-SelectUnit' -Action $a1 -Trigger $t1 -Principal $p1 -Force | Out-Null");
        sb.AppendLine();
        sb.AppendLine("# Tarefa 2: renomeia + reinicia como SYSTEM assim que a escolha for gravada.");
        sb.AppendLine($"$a2 = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{SetupDir}\\{RenameUnitFileName}\"'");
        sb.AppendLine("$t2 = New-ScheduledTaskTrigger -AtLogOn");
        sb.AppendLine("$p2 = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest");
        sb.AppendLine("Register-ScheduledTask -TaskName 'IsoForge-RenameUnit' -Action $a2 -Trigger $t2 -Principal $p2 -Force | Out-Null");
        sb.AppendLine("Write-Output 'Tarefas de selecao de unidade registradas.'");
        return sb.ToString();
    }

    /// <summary>
    /// RenameUnit.ps1 — (SYSTEM) espera a escolha da unidade (gravada pela tela), renomeia o
    /// computador, remove as tarefas (roda uma vez) e reinicia para aplicar o nome. No Entra ID,
    /// o Windows sincroniza o novo nome com o Entra depois do reboot.
    /// </summary>
    public static string RenameUnit()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# IsoForge - renomeia a maquina com a unidade escolhida e reinicia (modo Entra ID)");
        sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        sb.AppendLine("$dir  = Join-Path $env:ProgramData 'IsoForge'");
        sb.AppendLine("$file = Join-Path $dir 'unit.txt'");
        sb.AppendLine("$log  = Join-Path $dir 'rename.log'");
        sb.AppendLine("# Aguarda o usuario escolher a unidade (ate ~60 min).");
        sb.AppendLine("for ($i = 0; $i -lt 720; $i++) { if (Test-Path $file) { break }; Start-Sleep -Seconds 5 }");
        sb.AppendLine("if (-not (Test-Path $file)) { return }");
        sb.AppendLine("$name = ((Get-Content $file -Raw) -replace '\\s','')");
        sb.AppendLine("\"Renomeando para '$name' em $(Get-Date)\" | Out-File $log -Append");
        sb.AppendLine("try { Rename-Computer -NewName $name -Force -ErrorAction Stop; \"Renomeado OK.\" | Out-File $log -Append }");
        sb.AppendLine("catch { \"Rename-Computer falhou: $_ ; tentando CIM...\" | Out-File $log -Append; try { $rc=Invoke-CimMethod -ClassName Win32_ComputerSystem -MethodName Rename -Arguments @{ Name = $name }; \"rename CIM ReturnValue=$($rc.ReturnValue)\" | Out-File $log -Append } catch { \"rename CIM falhou: $_\" | Out-File $log -Append } }");
        sb.AppendLine("# Roda uma vez: remove as tarefas e o arquivo.");
        sb.AppendLine("Unregister-ScheduledTask -TaskName 'IsoForge-SelectUnit' -Confirm:$false");
        sb.AppendLine("Unregister-ScheduledTask -TaskName 'IsoForge-RenameUnit' -Confirm:$false");
        sb.AppendLine("Remove-Item $file -Force");
        sb.AppendLine("shutdown /r /t 5 /c \"IsoForge: aplicando o nome do computador\"");
        return sb.ToString();
    }

    /// <summary>
    /// Register-DemoteTask.ps1 — registra a tarefa agendada (como SYSTEM, no logon) que
    /// executa o Demote-EntraAdmin.ps1. Fica num .ps1 próprio para evitar aspas aninhadas no .cmd.
    /// </summary>
    public static string RegisterDemoteTask()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# IsoForge - registra a tarefa que remove o admin do usuario Entra no 1o logon");
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{SetupDir}\\{DemoteFileName}\"'");
        sb.AppendLine("$trigger = New-ScheduledTaskTrigger -AtLogOn");
        sb.AppendLine("$trigger.Delay = 'PT1M'   # espera o ingresso no Entra ID assentar");
        sb.AppendLine("$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest");
        sb.AppendLine("Register-ScheduledTask -TaskName 'IsoForge-DemoteEntraAdmin' -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null");
        sb.AppendLine("Write-Output 'Tarefa IsoForge-DemoteEntraAdmin registrada.'");
        return sb.ToString();
    }

    /// <summary>
    /// Demote-EntraAdmin.ps1 — no 1º logon, rebaixa APENAS a conta Entra que fez o setup (o
    /// usuário interativo que rodou o OOBE/join), removendo-a do grupo Administradores. Os demais
    /// admins do Entra e as roles que o join cria (Global Admin / Azure AD Joined Device Local
    /// Administrator) permanecem intactos. Só rebaixa se o usuário logado for do Entra ID (SID
    /// S-1-12-1); conta local (S-1-5-21) nunca é tocada. Enquanto o logado não for uma conta
    /// Entra, mantém a tarefa e tenta no próximo logon (só se autoremove após rebaixar o joiner).
    /// </summary>
    public static string Demote() =>
"""
# IsoForge - rebaixa a conta Entra que fez o setup (roda no logon; robusto p/ conta de nuvem)
$ErrorActionPreference = 'SilentlyContinue'
$log = 'C:\Setup\entra-demote.log'
function L($m) { "$([DateTime]::Now.ToString('s')) $m" | Out-File $log -Append }
L '=== demote start ==='

# Nome do grupo Administradores pelo SID (independe do idioma do Windows).
$adminGroup = (New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')).Translate([System.Security.Principal.NTAccount]).Value.Split('\')[-1]

# SID do usuario Entra logado. NAO traduz o nome (contas de nuvem aparecem como
# SPO\... ou AzureAD\... e NTAccount.Translate falha). Metodos robustos:
#  1) hive de usuario Entra carregado em HKEY_USERS (o joiner que acabou de logar);
#  2) fallback: dono do explorer.exe (contexto local da tarefa SYSTEM).
$sid = $null
try { $sid = @(Get-ChildItem Registry::HKEY_USERS -ErrorAction SilentlyContinue | Select-Object -ExpandProperty PSChildName | Where-Object { $_ -like 'S-1-12-1-*' -and $_ -notlike '*_Classes' })[0] } catch {}
if (-not $sid) { try { $p = @(Get-WmiObject Win32_Process -Filter "Name='explorer.exe'")[0]; if ($p) { $sid = ($p.GetOwnerSid()).Sid } } catch {} }
L "SID Entra logado: $sid"
if (-not $sid) { L 'sem usuario interativo ainda; tenta no proximo logon'; return }

# So rebaixa conta do Entra ID (S-1-12-1). Conta local (S-1-5-21) nunca e tocada,
# e os demais admins/roles do Entra continuam (so mexemos no usuario logado).
if ($sid -notlike 'S-1-12-1-*') { L "logado nao e Entra ($sid); mantem a tarefa"; return }

# Remove esse SID do grupo Administradores via ADSI WinNT (funciona com conta de nuvem,
# ao contrario do Get-LocalGroupMember/Remove-LocalGroupMember que quebram com SIDs que nao resolvem).
$grp = [ADSI]"WinNT://./$adminGroup,group"
function Get-Members { @($grp.psbase.Invoke('Members')) }
function Get-MemberSid($m) { try { (New-Object System.Security.Principal.SecurityIdentifier(($m.GetType().InvokeMember('objectSID','GetProperty',$null,$m,$null)),0)).Value } catch { '' } }
foreach ($m in Get-Members) {
  if ((Get-MemberSid $m) -eq $sid) {
    $path = $m.GetType().InvokeMember('ADsPath','GetProperty',$null,$m,$null)
    L "removendo $path"
    try { $grp.psbase.Invoke('Remove', $path) } catch { L "erro no Remove: $_" }
  }
}
# Confirma e so remove a tarefa quando o usuario Entra nao for mais admin (auto-heal).
$still = $false
foreach ($m in Get-Members) { if ((Get-MemberSid $m) -eq $sid) { $still = $true } }
if ($still) {
  L 'ainda administrador; tenta de novo no proximo logon'
} else {
  L 'usuario Entra rebaixado (efetivo no proximo logon). Removendo a tarefa.'
  Unregister-ScheduledTask -TaskName 'IsoForge-DemoteEntraAdmin' -Confirm:$false
}
""";
}
