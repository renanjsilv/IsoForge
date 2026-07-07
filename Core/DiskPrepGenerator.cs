namespace IsoForge.Core;

/// <summary>
/// Gera o IsoForgeDiskPrep.cmd, colocado na RAIZ da ISO. Roda no WinPE do Setup
/// (via RunSynchronous do passe windowsPE) ANTES da instalação: escolhe o primeiro
/// disco FIXO que NÃO seja USB e o particiona (GPT: EFI + MSR + Windows). O Setup
/// então instala nesse disco via InstallToAvailablePartition.
///
/// Segurança: nunca toca em disco USB (InterfaceType='USB'); se nenhum disco fixo
/// não-USB for encontrado (ou o wmic não existir), não faz nada e a seleção volta a
/// ser manual — jamais apaga o pendrive de boot.
/// </summary>
public static class DiskPrepGenerator
{
    public const string FileName = "IsoForgeDiskPrep.cmd";

    // Comando (no passe windowsPE) que localiza e executa o script na mídia de boot.
    public const string RunCommand =
        "cmd /c \"for %i in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do " +
        "@if exist %i:\\" + FileName + " call %i:\\" + FileName + "\"";

    public static string Generate() =>
"""
@echo off
setlocal enableextensions enabledelayedexpansion
rem === IsoForge - selecao automatica de disco (NUNCA o pendrive/USB) ===
rem Escolhe o primeiro disco FIXO cujo InterfaceType nao seja USB.
rem Index e InterfaceType sao tokens nao-finais (3 colunas) => saem sem CR.
set "TARGET="
for /f "skip=1 tokens=1,2" %%A in ('wmic diskdrive get Index^,InterfaceType^,MediaType 2^>nul') do (
  set "IDX=%%A"
  set "IFT=%%B"
  if not "!IDX!"=="" if /i not "!IFT!"=="USB" if not defined TARGET set "TARGET=!IDX!"
)
if not defined TARGET (
  echo IsoForge: nenhum disco fixo nao-USB detectado; selecao de disco MANUAL.
  exit /b 0
)
echo IsoForge: preparando o disco !TARGET! (nao-USB) para instalar o Windows...
> X:\isoforge-dp.txt echo select disk !TARGET!
>> X:\isoforge-dp.txt echo clean
>> X:\isoforge-dp.txt echo convert gpt
>> X:\isoforge-dp.txt echo create partition efi size=300
>> X:\isoforge-dp.txt echo format quick fs=fat32 label=System
>> X:\isoforge-dp.txt echo create partition msr size=16
>> X:\isoforge-dp.txt echo create partition primary
>> X:\isoforge-dp.txt echo format quick fs=ntfs label=Windows
diskpart /s X:\isoforge-dp.txt
exit /b 0
""";
}
