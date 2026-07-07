namespace IsoForge.Core;

/// <summary>
/// Scripts auxiliares do fluxo de imagem "golden" (Windows + apps pré-instalados):
/// preparar a referência (sysprep) e capturar o install.wim (DISM, a partir do WinPE).
/// </summary>
public static class GoldenImageScripts
{
    /// <summary>Rodar DENTRO da máquina de referência (modo de auditoria), após instalar tudo.</summary>
    public const string Sysprep = """
@echo off
REM ============================================================
REM  IsoForge - Passo 1: preparar a imagem de referencia
REM  Rode DENTRO da VM/maquina de referencia, no MODO DE AUDITORIA,
REM  DEPOIS de instalar o Office e todos os aplicativos.
REM  O sysprep generaliza a instalacao e desliga a maquina.
REM ============================================================
echo Preparando (sysprep /generalize /oobe /shutdown)...
%windir%\System32\Sysprep\sysprep.exe /generalize /oobe /shutdown
""";

    /// <summary>
    /// Rodar a partir do WinPE (não do Windows instalado). Captura a partição do Windows
    /// para um install.wim que depois é informado ao IsoForge para remontar a ISO final.
    /// </summary>
    public const string Capture = """
# ============================================================
#  IsoForge - Passo 2: capturar a imagem golden (rodar no WinPE)
#  Depois do sysprep desligar a maquina de referencia, inicie-a
#  pelo WinPE (ou pela propria ISO do Windows -> Shift+F10) e rode
#  este script. Ele captura a particao do Windows em install.wim.
#
#  Descubra as letras: em WinPE rode 'diskpart' -> 'list volume'.
#  Ajuste $Windows (particao do Windows) e $Destino (onde salvar).
# ============================================================
param(
    [string]$Windows = "C:",
    [string]$Destino = "D:\install.wim",
    [string]$Nome    = "Windows 11 Golden (IsoForge)"
)

Write-Host "Capturando $Windows para $Destino ..." -ForegroundColor Cyan
dism /Capture-Image /ImageFile:"$Destino" /CaptureDir:"$Windows\" /Name:"$Nome" /Compress:max /CheckIntegrity

if ($LASTEXITCODE -eq 0) {
    Write-Host "OK! Agora abra o IsoForge, marque 'Usar install.wim capturado' e aponte para $Destino." -ForegroundColor Green
} else {
    Write-Host "Falha na captura (codigo $LASTEXITCODE)." -ForegroundColor Red
}
""";

    /// <summary>
    /// Orquestra a VM Hyper-V headless: cria a VM, dá boot na ISO de referência,
    /// aguarda o sysprep desligar, monta o VHDX e captura o install.wim com DISM.
    /// Recebe os parâmetros por linha de comando. Deve rodar como Administrador.
    /// </summary>
    public const string Orchestrate = """
param(
  [Parameter(Mandatory)][string]$RefIso,
  [Parameter(Mandatory)][string]$Vhdx,
  [Parameter(Mandatory)][string]$WimOut,
  [string]$VmName = "IsoForge-Golden",
  [int]$MemoryGB = 4,
  [int]$DiskGB = 64,
  [int]$TimeoutMin = 90
)
$ErrorActionPreference = 'Stop'

# Pre-checagem do Hyper-V: o servico vmms precisa estar rodando. Se o Hyper-V foi
# habilitado agora sem reiniciar, ele ainda nao funciona -> mensagem clara.
$svc = Get-Service vmms -ErrorAction SilentlyContinue
if (-not $svc) { throw "Servico Hyper-V (vmms) nao encontrado. Habilite o Hyper-V e REINICIE o Windows." }
if ($svc.Status -ne 'Running') {
  try { Start-Service vmms -ErrorAction Stop; Start-Sleep -Seconds 3 }
  catch { throw "O servico Hyper-V (vmms) nao esta rodando. REINICIE o Windows apos habilitar o Hyper-V e tente de novo." }
}

function Cleanup {
  if (Get-VM -Name $VmName -ErrorAction SilentlyContinue) {
    Stop-VM -Name $VmName -TurnOff -Force -ErrorAction SilentlyContinue
    Remove-VM -Name $VmName -Force -ErrorAction SilentlyContinue
  }
  if ((Get-VHD -Path $Vhdx -ErrorAction SilentlyContinue).Attached) { Dismount-VHD -Path $Vhdx -ErrorAction SilentlyContinue }
}

Cleanup
if (Test-Path $Vhdx) { Remove-Item $Vhdx -Force }

Write-Host "Criando VM '$VmName' ($MemoryGB GB RAM, disco $DiskGB GB)..."
# -Path explicito (pasta temporaria) para nao depender do caminho padrao de VM do host.
$vmDir = Split-Path $Vhdx -Parent

# Escolhe um switch com internet: 'Default Switch' (NAT) ou o primeiro disponivel.
$switch = Get-VMSwitch -Name 'Default Switch' -ErrorAction SilentlyContinue
if (-not $switch) { $switch = Get-VMSwitch -ErrorAction SilentlyContinue | Select-Object -First 1 }
if ($switch) {
  New-VM -Name $VmName -Generation 2 -MemoryStartupBytes ($MemoryGB * 1GB) -NewVHDPath $Vhdx -NewVHDSizeBytes ($DiskGB * 1GB) -Path $vmDir -SwitchName $switch.Name | Out-Null
  Write-Host "Rede da VM: conectada a '$($switch.Name)'."
} else {
  New-VM -Name $VmName -Generation 2 -MemoryStartupBytes ($MemoryGB * 1GB) -NewVHDPath $Vhdx -NewVHDSizeBytes ($DiskGB * 1GB) -Path $vmDir | Out-Null
  Write-Host "AVISO: nenhum switch do Hyper-V encontrado - a VM ficara SEM internet. Marque 'Office offline' ou crie o 'Default Switch'."
}
Set-VMMemory -VMName $VmName -DynamicMemoryEnabled $false
Set-VMProcessor -VMName $VmName -Count 2
Add-VMDvdDrive -VMName $VmName -Path $RefIso
Set-VMFirmware -VMName $VmName -FirstBootDevice (Get-VMDvdDrive -VMName $VmName) -EnableSecureBoot Off
Start-VM -Name $VmName

# Abre a janela do Hyper-V para o usuario ACOMPANHAR a instalacao automatica.
try { Start-Process "$env:windir\System32\vmconnect.exe" -ArgumentList 'localhost', $VmName } catch {}
Write-Host "Janela da VM aberta - acompanhe a instalacao. Nao interaja; a VM desliga sozinha ao terminar."

Write-Host "Aguardando a instalacao + apps + sysprep (a VM desliga sozinha ao terminar)..."
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ((Get-VM -Name $VmName).State -ne 'Off') {
  if ($sw.Elapsed.TotalMinutes -gt $TimeoutMin) { Cleanup; throw "Timeout ($TimeoutMin min) aguardando a VM concluir." }
  Start-Sleep -Seconds 15
}
Write-Host "VM desligada. Fechando a janela de visualizacao..."
Get-Process vmconnect -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# Remove a VM (LIBERA o VHDX, mas NAO apaga o disco) antes de montar para captura.
Write-Host "Liberando e montando o disco da VM para captura..."
Remove-VM -Name $VmName -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 6

# O VHDX pode ficar preso alguns segundos apos desligar: monta com retentativa.
$diskNum = $null
for ($m = 0; $m -lt 12 -and $null -eq $diskNum; $m++) {
  try {
    $info = Get-VHD -Path $Vhdx -ErrorAction Stop
    if (-not $info.Attached) { Mount-VHD -Path $Vhdx -ErrorAction Stop; Start-Sleep -Seconds 2; $info = Get-VHD -Path $Vhdx }
    $diskNum = $info.DiskNumber
  } catch { Start-Sleep -Seconds 3 }
  if ($null -eq $diskNum) { Start-Sleep -Seconds 2 }
}
if ($null -eq $diskNum) { throw "Nao consegui montar o disco da VM (VHDX ocupado). O disco foi MANTIDO em: $Vhdx" }
Write-Host "Disco montado (disco fisico $diskNum). Procurando a particao do Windows..."

$parts = @()
for ($try = 0; $try -lt 8 -and $parts.Count -eq 0; $try++) {
  try { $parts = @(Get-Partition -DiskNumber $diskNum -ErrorAction Stop) } catch { $parts = @() }
  if ($parts.Count -eq 0) { Start-Sleep -Seconds 3 }
}
$winLetter = $null
foreach ($p in $parts) {
  $vol = Get-Volume -Partition $p -ErrorAction SilentlyContinue
  if ($vol -and -not $vol.DriveLetter -and $p.Size -gt 5GB) {
    $p | Add-PartitionAccessPath -AssignDriveLetter -ErrorAction SilentlyContinue
    $vol = Get-Volume -Partition $p -ErrorAction SilentlyContinue
  }
  if ($vol -and $vol.DriveLetter -and (Test-Path ("$($vol.DriveLetter):\Windows\System32"))) {
    $winLetter = $vol.DriveLetter; break
  }
}
if (-not $winLetter) {
  Dismount-VHD -Path $Vhdx -ErrorAction SilentlyContinue
  throw "Nao encontrei o Windows no disco da VM. O disco foi MANTIDO em: $Vhdx (pode inspecionar). Se a instalacao parou na escolha da EDICAO, defina a edicao na aba 'Sistema e usuario'."
}

Write-Host "Capturando a particao ${winLetter}: para o install.wim (DISM). Isso mostra o progresso abaixo..."
if (Test-Path $WimOut) { Remove-Item $WimOut -Force }
& dism.exe /Capture-Image /ImageFile:"$WimOut" /CaptureDir:"${winLetter}:\" /Name:"Windows Golden (IsoForge)" /Compress:max
$rc = $LASTEXITCODE

Dismount-VHD -Path $Vhdx -ErrorAction SilentlyContinue
if ($rc -ne 0) { throw "DISM /Capture-Image falhou (codigo $rc). O disco foi MANTIDO em: $Vhdx" }
Remove-Item $Vhdx -Force -ErrorAction SilentlyContinue
Write-Host "OK: install.wim capturado em $WimOut"
""";

    public const string Readme = """
FLUXO DE IMAGEM GOLDEN (Windows + apps ja instalados)
=====================================================

Objetivo: a ISO final instala um Windows que JA vem com Office e todos os
aplicativos, sem instalar nada nem baixar da internet no primeiro boot.

Passo a passo (feito uma vez por versao do Windows):

1) GERAR A ISO DE REFERENCIA
   No IsoForge, gere uma ISO normal com a "Selecao de unidade / modo de
   auditoria" desligada e SEM apps (ou com os apps que queira instalar
   automaticamente). O importante e conseguir chegar ao desktop.

2) INSTALAR TUDO NUMA VM (MODO DE AUDITORIA)
   - Crie uma VM e instale o Windows com essa ISO.
   - Na primeira tela do OOBE, pressione CTRL+SHIFT+F3 para entrar no
     MODO DE AUDITORIA (desktop de Administrador, sem usuario).
   - Instale o Office e todos os aplicativos manualmente (ou deixe o
     install.cmd rodar). Ajuste o que quiser.

3) PREPARAR (SYSPREP)
   - Rode "Sysprep-Generalize.cmd" (este pacote). A VM desliga sozinha.

4) CAPTURAR O install.wim (WinPE)
   - Inicie a VM pelo WinPE (ou pela ISO do Windows e Shift+F10).
   - Rode "Capture-GoldenImage.ps1" ajustando as letras das particoes.
   - Isso gera o install.wim com tudo dentro.

5) REMONTAR A ISO FINAL
   - No IsoForge, marque "Usar install.wim capturado", aponte para o
     arquivo gerado, defina o usuario/idioma e gere a ISO.
   - Pronto: essa ISO instala o Windows com tudo ja instalado.

Observacao: a ISO final fica grande (~8-12 GB) porque carrega a imagem
completa. Teste sempre numa VM antes de implantar em producao.
""";
}
