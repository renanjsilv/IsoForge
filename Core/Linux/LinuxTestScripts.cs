namespace IsoForge.Core.Linux;

/// <summary>
/// Scripts de teste da ISO Linux gerada. Equivalente ao <see cref="TestScripts"/> do Windows:
/// cria uma VM Hyper-V Geração 2 já preparada para bootar Linux (Secure Boot com o certificado
/// da autoridade UEFI da Microsoft) e, no modo seed, anexa as duas ISOs.
/// </summary>
public static class LinuxTestScripts
{
    public const string FileName = "Testar-Linux-HyperV.ps1";

    public const string HyperV = """
<#
.SYNOPSIS
  Cria uma VM Geração 2 no Hyper-V e inicia o boot pela ISO Linux gerada pelo IsoForge.
.EXAMPLE
  .\Testar-Linux-HyperV.ps1 -IsoPath "C:\ISOs\Ubuntu_Personalizado.iso"
.EXAMPLE
  # Modo ISO seed: a ISO oficial boota e a seed vai anexada como segunda unidade.
  .\Testar-Linux-HyperV.ps1 -IsoPath "C:\ISOs\ubuntu-24.04.iso" -SeedIsoPath "C:\ISOs\seed.iso"
.NOTES
  Requer: PowerShell como Administrador + Hyper-V habilitado.
  Habilitar Hyper-V: Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All
#>
param(
    [Parameter(Mandatory)] [string] $IsoPath,
    [string] $SeedIsoPath = "",
    [string] $VmName      = "IsoForge-Linux",
    [int]    $MemoryGB    = 4,
    [int]    $DiskGB      = 40,
    [int]    $Cpus        = 2,
    [switch] $SemSecureBoot   # desliga o Secure Boot (algumas distros não são assinadas)
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $IsoPath)) { throw "ISO não encontrada: $IsoPath" }
if ($SeedIsoPath -and -not (Test-Path $SeedIsoPath)) { throw "ISO seed não encontrada: $SeedIsoPath" }
if (Get-VM -Name $VmName -ErrorAction SilentlyContinue) {
    throw "Já existe uma VM chamada '$VmName'. Remova com: Stop-VM '$VmName' -Force; Remove-VM '$VmName' -Force"
}

$vmRoot  = Join-Path $env:PUBLIC "IsoForgeVMs\$VmName"
$vhdPath = Join-Path $vmRoot "$VmName.vhdx"
New-Item -ItemType Directory -Path $vmRoot -Force | Out-Null

Write-Host "Criando a VM $VmName..." -ForegroundColor Cyan
New-VM -Name $VmName -Generation 2 -MemoryStartupBytes ($MemoryGB * 1GB) `
       -NewVHDPath $vhdPath -NewVHDSizeBytes ($DiskGB * 1GB) -Path $vmRoot | Out-Null

Set-VMProcessor -VMName $VmName -Count $Cpus
Set-VMMemory   -VMName $VmName -DynamicMemoryEnabled $false

# Secure Boot: o Linux precisa do certificado "Microsoft UEFI Certificate Authority",
# diferente do padrão usado pelo Windows. Sem ele a VM não boota.
if ($SemSecureBoot) {
    Set-VMFirmware -VMName $VmName -EnableSecureBoot Off
    Write-Host "Secure Boot desativado." -ForegroundColor Yellow
} else {
    Set-VMFirmware -VMName $VmName -EnableSecureBoot On -SecureBootTemplate "MicrosoftUEFICertificateAuthority"
    Write-Host "Secure Boot ligado com o template MicrosoftUEFICertificateAuthority." -ForegroundColor Cyan
}

Add-VMDvdDrive -VMName $VmName -Path $IsoPath
if ($SeedIsoPath) {
    Add-VMDvdDrive -VMName $VmName -Path $SeedIsoPath
    Write-Host "ISO seed anexada como segunda unidade de DVD." -ForegroundColor Cyan
}

# Boot pelo DVD com a ISO principal.
$dvd = Get-VMDvdDrive -VMName $VmName | Where-Object { $_.Path -eq $IsoPath } | Select-Object -First 1
Set-VMFirmware -VMName $VmName -FirstBootDevice $dvd

# Rede: usa o primeiro switch externo disponível (a pós-instalação precisa de internet).
$switch = Get-VMSwitch | Where-Object SwitchType -eq 'External' | Select-Object -First 1
if (-not $switch) { $switch = Get-VMSwitch | Select-Object -First 1 }
if ($switch) {
    Connect-VMNetworkAdapter -VMName $VmName -SwitchName $switch.Name
    Write-Host "Rede conectada ao switch '$($switch.Name)'." -ForegroundColor Cyan
} else {
    Write-Warning "Nenhum switch virtual encontrado. Sem rede, a instalação de programas do 1º boot falha."
}

Start-VM -Name $VmName
Write-Host ""
Write-Host "VM iniciada. Abra o console:" -ForegroundColor Green
Write-Host "  vmconnect.exe localhost $VmName"
Write-Host ""
Write-Host "A instalação roda sozinha. Depois do primeiro boot, o log da pós-instalação"
Write-Host "fica em /var/log/isoforge-postinstall.log dentro da máquina."
Write-Host ""
Write-Host "Para apagar tudo depois do teste:" -ForegroundColor DarkGray
Write-Host "  Stop-VM '$VmName' -Force; Remove-VM '$VmName' -Force; Remove-Item '$vmRoot' -Recurse -Force"
""";
}
