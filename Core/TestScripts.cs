using System.Xml.Linq;

namespace IsoForge.Core;

public static class TestScripts
{
    /// <summary>
    /// Gera um arquivo .wsb do Windows Sandbox que mapeia a pasta Setup gerada
    /// como C:\Setup e executa o install.cmd no logon — testa a instalação
    /// silenciosa dos aplicativos em uma máquina descartável, sem VM nem formatação.
    /// </summary>
    public static string SandboxWsb(string hostSetupFolder)
    {
        var doc = new XElement("Configuration",
            new XElement("MemoryInMB", 4096),
            new XElement("MappedFolders",
                new XElement("MappedFolder",
                    new XElement("HostFolder", hostSetupFolder),
                    new XElement("SandboxFolder", @"C:\Setup"),
                    new XElement("ReadOnly", "false"))),
            new XElement("LogonCommand",
                new XElement("Command",
                    "cmd.exe /c start \"IsoForge - teste de instalacao\" cmd /k C:\\Setup\\install.cmd")));
        return doc.ToString();
    }

    public const string HyperV = """
<#
.SYNOPSIS
  Cria uma VM Geração 2 no Hyper-V e inicia o boot pela ISO personalizada do IsoForge.
.EXAMPLE
  .\Testar-HyperV.ps1 -IsoPath "C:\ISOs\Win11_Personalizada.iso"
.NOTES
  Requer: PowerShell como Administrador + Hyper-V habilitado.
  Habilitar Hyper-V: Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All
#>
param(
    [Parameter(Mandatory)] [string] $IsoPath,
    [string] $VmName   = "IsoForge-Teste",
    [int]    $MemoryGB = 4,
    [int]    $DiskGB   = 64,
    [int]    $Cpus     = 2,
    [switch] $ComTPM   # habilita TPM virtual (para testar SEM o bypass de requisitos)
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $IsoPath)) { throw "ISO não encontrada: $IsoPath" }
if (Get-VM -Name $VmName -ErrorAction SilentlyContinue) {
    throw "Já existe uma VM chamada '$VmName'. Remova com: Stop-VM '$VmName' -Force; Remove-VM '$VmName' -Force"
}

$vhdDir  = Join-Path $env:PUBLIC "Documents\Hyper-V\Virtual Hard Disks"
New-Item -ItemType Directory -Force $vhdDir | Out-Null
$vhdPath = Join-Path $vhdDir "$VmName.vhdx"

Write-Host "Criando VM '$VmName' (Geração 2, $MemoryGB GB RAM, $DiskGB GB disco)..." -ForegroundColor Cyan
New-VM -Name $VmName -Generation 2 -MemoryStartupBytes ($MemoryGB * 1GB) `
       -NewVHDPath $vhdPath -NewVHDSizeBytes ($DiskGB * 1GB) | Out-Null
Set-VMProcessor -VMName $VmName -Count $Cpus
Set-VM -VMName $VmName -CheckpointType Disabled -AutomaticStopAction ShutDown

Add-VMDvdDrive -VMName $VmName -Path $IsoPath
Set-VMFirmware -VMName $VmName -FirstBootDevice (Get-VMDvdDrive -VMName $VmName)

if ($ComTPM) {
    Write-Host "Habilitando TPM virtual..." -ForegroundColor Cyan
    Set-VMKeyProtector -VMName $VmName -NewLocalKeyProtector
    Enable-VMTPM -VMName $VmName
} else {
    Write-Host "VM sem TPM — use a opção 'Ignorar requisitos de hardware' do IsoForge." -ForegroundColor Yellow
}

Start-VM -Name $VmName
Write-Host "VM iniciada. Abrindo console..." -ForegroundColor Green
Write-Host ""
Write-Host "O QUE VERIFICAR:" -ForegroundColor Green
Write-Host " 1. Se a ISO foi gerada SEM a opção de boot direto, pressione uma tecla quando pedir."
Write-Host " 2. A instalação deve pular idioma/EULA/conta Microsoft (só pergunta o disco)."
Write-Host " 3. Após o 1º logon automático, uma janela instala os aplicativos."
Write-Host " 4. Confira o log em C:\Setup\install.log dentro da VM."
Write-Host " 5. Verifique o usuário: net user <nome>  (senha expira? grupo administradores?)"
Write-Host ""
Write-Host "Para descartar o teste: Stop-VM '$VmName' -Force; Remove-VM '$VmName' -Force; Remove-Item '$vhdPath'"

vmconnect.exe localhost $VmName
""";
}
