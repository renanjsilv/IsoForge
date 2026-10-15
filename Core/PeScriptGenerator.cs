using IsoForge.Models;

namespace IsoForge.Core;

/// <summary>
/// Gera o IsoForgePE.cmd, que vai na RAIZ da mídia e roda no WinPE do instalador, pelo
/// RunSynchronous do passe windowsPE. Com a seleção automática de disco ligada, ele faz a
/// instalação INTEIRA sozinho — e o Setup do Windows nunca chega a aplicar a imagem.
///
/// POR QUE NÃO DEIXAR O SETUP INSTALAR. O Windows 11 24H2 trouxe um Setup novo, e ele trava
/// em torno de 10% na fase de cópia com mídias personalizadas: vários relatos independentes
/// no 25H2, sempre com a mesma conclusão — a mesma mídia instala pelo Setup antigo. Não há
/// forma conhecida de forçar o Setup antigo numa instalação desassistida. O gerador de
/// autounattend mais usado (cschneegans/unattend-generator) chegou à mesma conclusão e, em
/// abril de 2026, passou a fazer a fase do WinPE com um script próprio, sempre. Este segue o
/// mesmo desenho: diskpart, dism /Apply-Image, cópia do payload, unattend.xml em
/// Windows\Panther, bcdboot, reinício. O resto da instalação — specialize, OOBE, primeiro
/// logon — vem do mesmo autounattend, lido de Windows\Panther depois do reinício.
///
/// Isso também tira do caminho o processamento do $OEM$ pelo Setup novo, que tem bugs
/// próprios relatados no 25H2: aqui quem copia o payload ($1 e $$) é o script.
///
/// AS TRAVAS. Este arquivo apaga um disco inteiro, então:
///   • só aprova disco cujo barramento, no "detail disk" do diskpart, está numa lista de
///     ACEITOS (NVMe, SATA, ATA, IDE, SAS, SCSI, RAID, MMC). A lista falha fechada: USB —
///     inclusive o SSD por UASP, que o wmic chamava de SCSI —, SD, VHD, iSCSI ou um tipo
///     novo ou traduzido ficam de fora;
///   • nunca aprova o disco onde a PRÓPRIA mídia está, por dois caminhos: o "*" do detail
///     volume e a letra da mídia na tabela de volumes do disco. Se a mídia aceita escrita e o
///     diskpart não disse em que disco ela está, nada é apagado;
///   • segue só com EXATAMENTE UM disco aprovado. Com dois, nada diz qual tem os dados;
///   • controladora RAID/VMD (classe PCI 01 04) sem driver devolve ao Setup: o NVMe atrás
///     dela não aparece, e o único disco aprovado poderia ser o de dados;
///   • o disco aprovado é apagado SEM PERGUNTAR — é o que "seleção automática" promete, e o
///     que o IsoForge sempre fez. Uma versão pediu APAGAR digitado na tela; na prática isso
///     só tirava o automático de quem o escolheu. A exceção é o disco que ainda está NO MEIO
///     de uma instalação por mídia IsoForge (marcador em Windows\Setup, gravado ao aplicar a
///     imagem e apagado no fim do primeiro logon): é a máquina voltando a dar boot pela mídia,
///     e apagar ali seria um laço sem fim;
///   • SÓ COMANDOS QUE EXISTEM NO WINPE DO INSTALADOR. O do 25H2 não tem findstr, where,
///     wmic, choice, timeout, more, sort, fc nem certutil — verificado numa VM. O primeiro
///     script usava findstr para ler o barramento: nenhum disco era aprovado, e a mídia caía
///     sempre na escolha manual. A suíte agora recusa qualquer um desses no script;
///   • imagem, edição, firmware ou letra que não se confirmam DEVOLVEM O CONTROLE ao Setup
///     sem tocar em nada (rótulo :recuo): o comportamento volta a ser o de antes, com escolha
///     manual de disco;
///   • código de saída é comparado com "neq 0", nunca com "if errorlevel 1": DISM e bcdboot
///     devolvem HRESULT negativo, e "errorlevel 1" quer dizer "maior ou igual a 1".
///
/// Antes de listar os discos, carrega com drvload os drivers de armazenamento (classe
/// SCSIAdapter: Intel VMD/RST, RAID) do pacote do fabricante que já está na mídia — sem
/// eles, o NVMe de um notebook com "RAID On" não aparece no WinPE. O que for carregado é
/// injetado de novo no Windows aplicado, e ali falhar para: é driver de boot.
///
/// Depois de apagar, não há volta: qualquer falha para numa tela que diz o que aconteceu e
/// onde está o log, em vez de seguir adiante às cegas. O boot é gravado POR ÚLTIMO, para que
/// uma cópia que falhe deixe o disco sem boot, e não um Windows sem usuário nem payload.
/// </summary>
public static class PeScriptGenerator
{
    public const string FileName = "IsoForgePE.cmd";

    /// <summary>
    /// Marcador gravado em Windows\Setup ao aplicar a imagem e apagado pelo install.cmd no fim
    /// do primeiro logon. Enquanto ele existe, o disco está no meio de uma instalação, e o
    /// script não o apaga.
    /// </summary>
    public const string MarkerFileName = "isoforge-pe.txt";

    /// <summary>
    /// O comando do passe windowsPE que acha o script na mídia e o executa. X: fica de fora
    /// porque é o próprio WinPE em RAM.
    /// </summary>
    public const string RunCommand =
        "cmd /c \"for %i in (C D E F G H I J K L M N O P Q R S T U V W Y Z) do " +
        "@if exist %i:\\" + FileName + " call %i:\\" + FileName + "\"";

    // Chaves que dizem qual edição é. As genéricas de instalação não ativam; as GVLK ativam só
    // contra um servidor KMS. São as mesmas no Windows 10 e no 11. Uma chave fora desta lista
    // não diz a edição, e então o script instala a Pro — ver Edicao().
    static readonly Dictionary<string, string> ChavesGenericas = new(StringComparer.OrdinalIgnoreCase)
    {
        // genéricas de instalação
        ["YTMG3-N6DKC-DKB77-7M9GH-8HVX7"] = "Home",
        ["4CPRK-NM3K3-X6XXQ-RXX86-WXCHW"] = "Home N",
        ["BT79Q-G7N6G-PGBYW-4YWX6-6F4BT"] = "Home Single Language",
        ["VK7JG-NPHTM-C97JM-9MPGT-3V66T"] = "Pro",
        ["2B87N-8KFHP-DKV6R-Y2C8J-PKCKT"] = "Pro N",
        ["DXG7C-N36C4-C4HTG-X4T3X-2YV77"] = "Pro for Workstations",
        ["YNMGQ-8RYV3-4PGQ3-C8XTP-7CFBY"] = "Education",
        ["XGVPP-NMH47-7TTHJ-W3FW7-8HV2C"] = "Enterprise",
        // GVLK (KMS)
        ["W269N-WFGWX-YVC9B-4J6C9-T83GX"] = "Pro",
        ["MH37W-N47XK-V7XM9-C7227-GCQG9"] = "Pro N",
        ["NRG8B-VKK3Q-CXVCJ-9G2XF-6Q84J"] = "Pro for Workstations",
        ["6TP4R-GNPTD-KYYHQ-7B7DP-J447Y"] = "Pro Education",
        ["NW6C2-QMPVW-D7KKK-3GKT6-VCFB2"] = "Education",
        ["NPPR9-FWDCX-D2C8J-H872K-2YT43"] = "Enterprise",
        ["DPH2V-TTNVB-4X9Q3-TJR4H-KHJW4"] = "Enterprise N",
    };

    /// <summary>A edição que uma chave genérica ou GVLK indica, ou null se a chave não disser.</summary>
    public static string? EdicaoPelaChave(string? chave) =>
        !string.IsNullOrWhiteSpace(chave) && ChavesGenericas.TryGetValue(chave.Trim(), out var e) ? e : null;

    /// <summary>
    /// O nome da imagem a aplicar, como o DISM o conhece dentro do install.wim.
    ///
    /// O NOME da imagem não é traduzido — é "Windows 11 Pro" também na mídia em português; o
    /// que muda com o idioma é o nome de exibição. Antes, sem chave, o Setup usava a edição
    /// da chave gravada no firmware (MSDM) ou perguntava na tela; aqui não há nenhum dos dois,
    /// então a padrão é a Pro, que é a de uso corporativo. Numa máquina com licença Home no
    /// firmware, a Pro fica sem ativar — o IsoPipeline avisa disso na geração.
    /// </summary>
    public static string Edicao(BuildConfig c)
    {
        var versao = c.Os == TargetOs.Windows10 ? "10" : "11";
        return $"Windows {versao} {EdicaoPelaChave(c.ProductKey) ?? "Pro"}";
    }

    /// <summary>
    /// Como o DISM escolhe a imagem. O WIM capturado pelo IsoForge (ISO golden) tem uma imagem
    /// só, com outro nome — "Windows Golden (IsoForge)" —, então vai pelo índice. Pedir
    /// "Windows 11 Pro" ali faria o script recuar ao Setup novo, que é o que trava.
    /// </summary>
    public static string Selecao(BuildConfig c) =>
        c.UseCapturedWim ? "/Index:1" : $"/Name:\"{Edicao(c)}\"";

    /// <summary>O que vai ser aplicado, em palavras, para o log. Sem parênteses nem aspas:
    /// o texto passa por "call :log", e o cmd reinterpretaria os dois.</summary>
    public static string Descricao(BuildConfig c) =>
        c.UseCapturedWim ? "imagem golden do IsoForge, indice 1" : Edicao(c);

    public static string Generate(BuildConfig c) => $$"""
@echo off
setlocal enableextensions enabledelayedexpansion
rem ============================================================================
rem  IsoForge - instalacao direta pelo WinPE, sem o Setup novo do Windows 11.
rem  Particiona, aplica a imagem com DISM, copia o payload e o autounattend
rem  para Windows\Panther, grava o boot e reinicia. Specialize, OOBE e primeiro
rem  logon continuam vindo do mesmo autounattend, lido de la depois do reinicio.
rem
rem  Qualquer duvida ANTES de apagar - imagem, firmware, disco, letra - devolve
rem  ao Setup sem tocar em nada, pelo rotulo :recuo. Depois de apagar, qualquer
rem  erro para nesta tela, pelo rotulo :falha. So usa comando que existe no
rem  WinPE do instalador: o do 25H2 nao tem findstr, where, wmic nem choice.
rem ============================================================================

rem Uma vez so: o comando do windowsPE varre todas as letras e poderia achar
rem este arquivo em mais de uma.
if exist X:\isoforge-pe.ran exit /b 0
> X:\isoforge-pe.ran echo 1

set "LOG=X:\isoforge-pe.log"
set "MIDIA=%~d0"
set "EDICAO={{Descricao(c)}}"
set "SELECAO={{Selecao(c)}}"
rem O DISM poe os temporarios no TEMP; sem ele, pode falhar sem dizer por que.
if not defined TEMP set "TEMP=X:\Windows\Temp"
if not defined TMP set "TMP=X:\Windows\Temp"
call :log IsoForge: instalacao direta a partir de %MIDIA%
set >> "%LOG%" 2>&1

rem Sem o arquivo de respostas nao ha o que levar para Windows\Panther, e a
rem maquina subiria sem usuario. Tambem e o sinal de que esta nao e a midia
rem que gerou este script.
if not exist "%MIDIA%\autounattend.xml" (
  call :log nao achei %MIDIA%\autounattend.xml - devolvendo ao Setup
  goto :recuo
)

rem ---------------------------------------------------------------- a imagem
set "IMAGEM="
set "SWM="
if exist "%MIDIA%\sources\install.wim" set "IMAGEM=%MIDIA%\sources\install.wim"
if not defined IMAGEM if exist "%MIDIA%\sources\install.swm" set "IMAGEM=%MIDIA%\sources\install.swm"
if not defined IMAGEM if exist "%MIDIA%\sources\install.esd" set "IMAGEM=%MIDIA%\sources\install.esd"
if not defined IMAGEM (
  call :log nao achei install.wim, .swm ou .esd em %MIDIA%\sources - devolvendo ao Setup
  goto :recuo
)
if /i "!IMAGEM:~-4!"==".swm" set "SWM=/SWMFile:%MIDIA%\sources\install*.swm"
call :log imagem: !IMAGEM! !SWM! - a aplicar: !EDICAO!

rem A imagem pedida precisa existir ANTES de apagar qualquer disco: sem isto,
rem uma midia sem a edicao pedida so seria descoberta depois do clean. O mesmo
rem comando prova que o arquivo da imagem e legivel. "neq 0", nunca
rem "if errorlevel 1": o DISM devolve HRESULT negativo, e "errorlevel 1" quer
rem dizer "maior ou igual a 1".
dism /Get-WimInfo /WimFile:"!IMAGEM!" !SELECAO! >> "%LOG%" 2>&1
set "RC=!errorlevel!"
if not "!RC!"=="0" (
  call :log a imagem pedida nao existe nesta midia ou nao abriu - DISM devolveu !RC! - devolvendo ao Setup
  dism /Get-WimInfo /WimFile:"!IMAGEM!" >> "%LOG%" 2>&1
  goto :recuo
)

rem CheckIntegrity e Verify so para WIM e SWM. Num ESD o DISM pode recusar a
rem opcao, e recusar significaria nao instalar.
set "INTEGRIDADE=/CheckIntegrity /Verify"
if /i "!IMAGEM:~-4!"==".esd" set "INTEGRIDADE="

rem ---------------------------------------------------------------- firmware
rem PEFirmwareType: 0x1 = BIOS, 0x2 = UEFI. E o mesmo jeito que a documentacao
rem da Microsoft manda descobrir, e nao depende do idioma do WinPE. Ilegivel
rem nao vira palpite: um disco UEFI com MBR e boot de BIOS nao sobe.
wpeutil UpdateBootInfo >nul 2>&1
set "FW="
for /f "tokens=3" %%F in ('reg query HKLM\System\CurrentControlSet\Control /v PEFirmwareType 2^>nul ^| find "PEFirmwareType"') do set "FW=%%F"
set "MODO="
if "!FW!"=="0x1" set "MODO=BIOS"
if "!FW!"=="0x2" set "MODO=UEFI"
call :log firmware: [!MODO!] - PEFirmwareType [!FW!]
if not defined MODO (
  call :log PEFirmwareType ilegivel ou inesperado - devolvendo ao Setup sem tocar em nada
  goto :recuo
)

rem ------------------------------ driver de armazenamento do fabricante
rem Intel VMD/RST e RAID: sem o driver, o NVMe atras do VMD nao aparece no
rem WinPE e o script recua ao Setup - que e o que trava. O driver vem no
rem pacote do fabricante, ja na midia. So a classe SCSIAdapter: um driver de
rem controladora SATA (HDC) reiniciaria ao vivo a controladora que pode ser a
rem do DVD de onde o script le. O "type" converte .inf em UTF-16 antes do
rem find. Todo .inf que o drvload aceitar vai para X:\isoforge-stor.txt e e
rem injetado de novo no Windows aplicado, com falha fatal: e driver de boot.
if exist X:\isoforge-stor.txt del /f /q X:\isoforge-stor.txt
if exist "%MIDIA%\sources\$OEM$\$1\Drivers\" (
  call :log carregando drivers de armazenamento do pacote do fabricante
  for /r "%MIDIA%\sources\$OEM$\$1\Drivers" %%f in (*.inf) do (
    type "%%f" > X:\isoforge-inf.tmp 2>nul
    find /i "SCSIAdapter" X:\isoforge-inf.tmp >nul && (
      drvload "%%f" >> "%LOG%" 2>&1 && >> X:\isoforge-stor.txt echo %%f
    )
  )
  > X:\isoforge-rs.txt echo rescan
  diskpart /s X:\isoforge-rs.txt >> "%LOG%" 2>&1
)

rem ------------------------------------ controladora de disco sem driver
rem Um NVMe atras de uma controladora RAID/VMD sem driver nao aparece no "list
rem disk" - e entao o UNICO disco aprovado pode ser o de dados, que seria
rem apagado sem perguntar. Controladora da classe PCI 01 04 (RAID, onde o VMD
rem da Intel tambem cai) sem servico associado e controladora sem driver: nada
rem e apagado. Com o driver carregado pelo drvload acima, ela ganha o servico
rem e passa. So reg e find, que existem no WinPE do instalador.
set "SEMDRV="
for /f "delims=" %%k in ('reg query HKLM\SYSTEM\CurrentControlSet\Enum\PCI /s /d /f "CC_0104" 2^>nul ^| find "HKEY_"') do (
  reg query "%%k" /v Service >nul 2>&1 || set "SEMDRV=%%k"
)
if defined SEMDRV (
  >> "%LOG%" echo controladora RAID/VMD sem driver: !SEMDRV!
  call :log ha uma controladora RAID ou VMD sem driver - um disco pode estar invisivel - devolvendo ao Setup
  goto :recuo
)

rem ------------------------------------------- o disco onde a midia esta
rem Nunca pode ser o destino. A linha do disco selecionado no "detail volume"
rem comeca com asterisco, e o numero e o terceiro campo em qualquer idioma:
rem "* Disk 1" em ingles, "* Disco 1" em portugues. Midia otica ou ISO montada
rem nao tem essa linha, mas tambem nao aceita escrita. Midia que aceita escrita
rem esta num disco: se o diskpart nao disse qual, a trava falharia aberta -
rem entao nada e apagado.
set "DISCO_MIDIA="
> X:\isoforge-vol.txt echo select volume %MIDIA:~0,1%
>> X:\isoforge-vol.txt echo detail volume
diskpart /s X:\isoforge-vol.txt > X:\isoforge-vol.out 2>&1
type X:\isoforge-vol.out >> "%LOG%"
for /f "tokens=1-3" %%a in (X:\isoforge-vol.out) do if "%%a"=="*" set "DISCO_MIDIA=%%c"
set "MIDIA_RW="
(> "%MIDIA%\isoforge-rw.tmp" echo x) 2>nul && set "MIDIA_RW=1"
if defined MIDIA_RW del /f /q "%MIDIA%\isoforge-rw.tmp" >nul 2>&1
call :log disco da midia: [!DISCO_MIDIA!] - midia aceita escrita: [!MIDIA_RW!]
if defined MIDIA_RW if not defined DISCO_MIDIA (
  call :log a midia esta num disco, mas o diskpart nao disse qual - devolvendo ao Setup
  goto :recuo
)

rem --------------------------------------------------- o disco de destino
rem Todo disco do "list disk" passa pelas travas de :candidato, e o script so
rem segue com EXATAMENTE UM aprovado: com dois, nada diz qual tem os dados.
rem Sem wmic: ele esta saindo do Windows 11, e o InterfaceType dele chama de
rem SCSI o disco USB por UASP. O Type do "detail disk" e o barramento do VDS.
set "ALVO="
set "CANDIDATOS="
set /a NCAND=0
> X:\isoforge-ld.txt echo list disk
diskpart /s X:\isoforge-ld.txt > X:\isoforge-ld.out 2>&1
type X:\isoforge-ld.out >> "%LOG%"
for /f "tokens=1,2" %%a in (X:\isoforge-ld.out) do call :candidato "%%b"
if not "!NCAND!"=="1" (
  call :log discos aprovados: [!CANDIDATOS! ] - so instalo sozinho com exatamente um - devolvendo ao Setup, escolha manual
  goto :recuo
)
call :log disco de destino: !ALVO!

rem ------------------------------ o disco esta no meio de uma instalacao?
rem A selecao automatica apaga o disco escolhido sem perguntar - e o que ela
rem promete, e o que fazia antes. A unica excecao e o disco que ESTA recebendo
rem o Windows por uma midia IsoForge e ainda nao terminou o primeiro logon: e a
rem maquina voltando a dar boot pela midia no meio da instalacao, e apagar ali
rem seria um laco sem fim. O marcador e gravado ao aplicar a imagem e apagado
rem no fim do primeiro logon (install.cmd) ou pelo SetupComplete (Entra ID). Se
rem ele ficou para tras mas a instalacao terminou (install.done existe), ele nao
rem vale mais. Sem testar a palavra "Volume": qualquer terceiro campo que nao
rem seja letra so faz o "if exist" dar falso.
set "MARCADO="
for /f "tokens=1-3" %%v in (X:\isoforge-alvo.out) do if exist "%%x:\Windows\Setup\isoforge-pe.txt" if not exist "%%x:\ProgramData\IsoForge\install.done" set "MARCADO=%%x:"
if defined MARCADO (
  call :log o disco !ALVO! esta no meio de uma instalacao IsoForge em !MARCADO! - nada apagado
  echo  ===========================================================================
  echo   IsoForge: o disco !ALVO! esta no meio de uma instalacao feita por esta midia,
  echo   e a maquina voltou a dar boot pela midia. NADA FOI APAGADO.
  echo.
  echo   Para continuar a instalacao: tire a midia e reinicie.
  echo   Para reinstalar do zero: na tela seguinte, exclua as particoes do disco.
  echo.
  echo   Esta tela fecha em 60 segundos.
  echo  ===========================================================================
  ping -n 61 127.0.0.1 >nul
  goto :recuo
)


rem As letras que o script usa precisam estar livres. Se nao estiverem, sai
rem ANTES de apagar qualquer coisa.
if exist S:\ (
  call :log a letra S: ja esta em uso - devolvendo ao Setup
  goto :recuo
)
if exist W:\ (
  call :log a letra W: ja esta em uso - devolvendo ao Setup
  goto :recuo
)

rem ---------------------------------------------------- particionamento
rem A PARTIR DAQUI O DISCO !ALVO! E APAGADO. Nao ha volta.
> X:\isoforge-dp.txt echo select disk !ALVO!
>> X:\isoforge-dp.txt echo clean
if "!MODO!"=="UEFI" (
  >> X:\isoforge-dp.txt echo convert gpt
  >> X:\isoforge-dp.txt echo create partition efi size=300
  >> X:\isoforge-dp.txt echo format quick fs=fat32 label=System
  >> X:\isoforge-dp.txt echo assign letter=S
  >> X:\isoforge-dp.txt echo create partition msr size=16
) else (
  >> X:\isoforge-dp.txt echo convert mbr
  >> X:\isoforge-dp.txt echo create partition primary size=300
  >> X:\isoforge-dp.txt echo format quick fs=ntfs label=System
  >> X:\isoforge-dp.txt echo assign letter=S
  >> X:\isoforge-dp.txt echo active
)
>> X:\isoforge-dp.txt echo create partition primary
>> X:\isoforge-dp.txt echo format quick fs=ntfs label=Windows
>> X:\isoforge-dp.txt echo assign letter=W
call :log particionando o disco !ALVO! - !MODO!
diskpart /s X:\isoforge-dp.txt >> "%LOG%" 2>&1
set "RC=!errorlevel!"
if not exist W:\ (
  call :log o diskpart devolveu !RC!
  call :falha "o particionamento do disco !ALVO! falhou"
  exit /b 1
)

rem ------------------------------------------------------- aplica a imagem
rem Sem redirecionar a saida do DISM: a barra de progresso dele aparece na tela,
rem e o detalhe fica no log proprio dele, X:\Windows\Logs\DISM\dism.log.
rem /CheckIntegrity e /Verify transformam uma midia corrompida num erro com
rem nome, em vez de uma trava sem explicacao no meio da instalacao.
call :log aplicando !EDICAO! em W: - isto leva alguns minutos
dism /Apply-Image /ImageFile:"!IMAGEM!" !SWM! !SELECAO! /ApplyDir:W:\ !INTEGRIDADE!
set "RC=!errorlevel!"
if not "!RC!"=="0" (
  call :log o DISM devolveu !RC! ao aplicar a imagem
  call :falha "o DISM nao conseguiu aplicar a imagem - midia corrompida ou lida com erro"
  exit /b 1
)
if not exist W:\Windows\System32\config\SYSTEM (
  call :falha "o DISM terminou sem erro, mas W:\Windows nao esta completo"
  exit /b 1
)

rem ------------------------------------------------------------ payload
rem O que o Setup faria com o $OEM$: $1 vai para a raiz do Windows e $$ para
rem W:\Windows - no modo Entra ID e la que esta o SetupComplete.cmd. /A-:R
rem porque, vindo de DVD ou ISO, todo arquivo chega somente-leitura.
if exist "%MIDIA%\sources\$OEM$\$1\" (
  call :log copiando o payload do IsoForge
  robocopy "%MIDIA%\sources\$OEM$\$1" W:\ /E /A-:R /R:1 /W:1 /NFL /NDL /NJH /NP >> "%LOG%" 2>&1
  if errorlevel 8 (
    call :falha "a copia do payload para W: falhou"
    exit /b 1
  )
)
if exist "%MIDIA%\sources\$OEM$\$$\" (
  call :log copiando o $OEM$\$$ para W:\Windows
  robocopy "%MIDIA%\sources\$OEM$\$$" W:\Windows /E /A-:R /R:1 /W:1 /NFL /NDL /NJH /NP >> "%LOG%" 2>&1
  if errorlevel 8 (
    call :falha "a copia do $OEM$\$$ para W:\Windows falhou"
    exit /b 1
  )
)

rem O driver de armazenamento carregado no WinPE e critico para o boot. Sem
rem ele no Windows aplicado, o primeiro boot para em INACCESSIBLE_BOOT_DEVICE
rem e o pnputil do primeiro logon nunca roda. Por isso, aqui, falhar para.
if exist X:\isoforge-stor.txt (
  mkdir W:\Windows\Temp >nul 2>&1
  for /f "usebackq delims=" %%f in ("X:\isoforge-stor.txt") do (
    call :log injetando o driver de armazenamento %%~nxf
    dism /Image:W:\ /ScratchDir:W:\Windows\Temp /Add-Driver /Driver:"%%f" >> "%LOG%" 2>&1
    if !errorlevel! neq 0 (
      call :falha "o DISM nao injetou o driver de armazenamento %%~nxf - o Windows nao daria boot"
      exit /b 1
    )
  )
)

rem Drivers: o passe offlineServicing so roda quando o Setup aplica a imagem.
rem Aqui quem injeta e o DISM, com os temporarios e o log no disco - o RAM disk
rem do WinPE e pequeno. Falhar aqui nao para a instalacao: o primeiro logon
rem ainda passa os mesmos drivers pelo pnputil.
if exist "W:\Drivers\" (
  call :log injetando os drivers do fabricante
  mkdir W:\Windows\Temp >nul 2>&1
  mkdir W:\Windows\Logs >nul 2>&1
  dism /Image:W:\ /ScratchDir:W:\Windows\Temp /LogPath:W:\Windows\Logs\isoforge-drivers.log /Add-Driver /Driver:W:\Drivers /Recurse >> "%LOG%" 2>&1
  if !errorlevel! neq 0 call :log aviso: o DISM nao injetou todos os drivers - o primeiro logon tenta de novo
)

rem ---------------------------------------------------- o arquivo de respostas
rem Em Windows\Panther ele e lido no proximo boot, e o specialize, o OOBE e o
rem primeiro logon seguem exatamente como seguiriam pelo Setup.
mkdir W:\Windows\Panther >nul 2>&1
copy /y "%MIDIA%\autounattend.xml" W:\Windows\Panther\unattend.xml >nul
if !errorlevel! neq 0 (
  call :falha "nao consegui copiar o autounattend.xml para W:\Windows\Panther"
  exit /b 1
)
attrib -r W:\Windows\Panther\unattend.xml >nul 2>&1

rem ------------------------------------------------------ arquivos de boot
rem Por ultimo, de proposito: se uma copia acima falhar, o disco fica sem boot
rem e a maquina nao sobe num Windows sem payload nem arquivo de respostas.
call :log gravando o boot - !MODO!
bcdboot W:\Windows /s S: /f !MODO! >> "%LOG%" 2>&1
if !errorlevel! neq 0 (
  call :falha "o bcdboot nao gravou os arquivos de boot"
  exit /b 1
)
set "BCD=S:\Boot\BCD"
if "!MODO!"=="UEFI" set "BCD=S:\EFI\Microsoft\Boot\BCD"
if not exist "!BCD!" (
  call :falha "o bcdboot terminou sem erro, mas nao criou !BCD!"
  exit /b 1
)

rem Marca o disco: se a maquina voltar a dar boot pela midia antes do fim do
rem primeiro logon, o script ve o marcador e devolve ao Setup sem apagar.
mkdir W:\Windows\Setup >nul 2>&1
(> W:\Windows\Setup\isoforge-pe.txt echo IsoForge: imagem aplicada a partir de %MIDIA% em %date% %time%) || call :log aviso: nao gravei o marcador em W:\Windows\Setup

rem No UEFI: o Windows Boot Manager vai para o topo da ordem de boot, como o
rem Setup faria, e o proximo boot vai para ele mesmo que a midia esteja
rem primeiro - sem isto, a maquina voltaria a dar boot pela midia. O BootNext
rem vale para UM boot; o topo da ordem cobre os reinicios do specialize e do
rem install.cmd. Os dois podem falhar em firmware teimoso, e por isso o disco
rem marcado acima nao e apagado de novo.
set "BOOTNEXT="
if "!MODO!"=="UEFI" (
  bcdedit /set {fwbootmgr} displayorder {bootmgr} /addfirst >> "%LOG%" 2>&1
  if !errorlevel! neq 0 call :log aviso: nao consegui por o Windows Boot Manager no topo da ordem de boot
  bcdedit /set {fwbootmgr} bootsequence {bootmgr} >> "%LOG%" 2>&1
  if !errorlevel! equ 0 set "BOOTNEXT=1"
  bcdedit /enum firmware >> "%LOG%" 2>&1
)
if not defined BOOTNEXT call :log aviso: sem BootNext - modo !MODO! ou o bcdedit falhou

call :log pronto - reiniciando para o Windows instalado
mkdir W:\Setup >nul 2>&1
copy /y "%LOG%" W:\Setup\isoforge-pe.log >nul 2>&1
rem Aviso, espera e reinicio no MESMO bloco: o cmd le o bloco inteiro antes de
rem executar, entao tirar a midia durante a espera nao o interrompe.
if not defined BOOTNEXT (
  echo  ===========================================================================
  echo   Windows aplicado. TIRE A MIDIA AGORA - a maquina reinicia em 20 segundos
  echo   e, com a midia ligada, pode voltar a dar boot por ela.
  echo  ===========================================================================
  ping -n 21 127.0.0.1 >nul
  wpeutil reboot
  exit /b 0
)
wpeutil reboot
exit /b 0

rem ============================================================================
:recuo
rem Devolve ao Setup sem ter tocado em disco. O log vai junto para a midia,
rem se ela aceitar escrita: X: some no reinicio.
copy /y "%LOG%" "%MIDIA%\isoforge-pe.log" >nul 2>&1
exit /b 0

:candidato
rem Recebe o 2o campo de uma linha do "list disk" - o numero, nas linhas de
rem disco. Aprova so disco que o diskpart le, que nao e o da midia e cujo
rem barramento esta na lista de ACEITOS. Lista de aceitos falha fechada: um
rem tipo novo ou traduzido deixa o disco de fora, e o script volta ao Setup.
set "IDX=%~1"
if not defined IDX goto :eof
rem So digitos, sem pipe: tira cada digito e ve se sobra algo alem do "x".
set "RESTO=x!IDX!"
for %%d in (0 1 2 3 4 5 6 7 8 9) do set "RESTO=!RESTO:%%d=!"
if not "!RESTO!"=="x" goto :eof
> X:\isoforge-dd.txt echo select disk !IDX!
>> X:\isoforge-dd.txt echo detail disk
diskpart /s X:\isoforge-dd.txt > X:\isoforge-dd.out 2>&1
set "RC=!errorlevel!"
>> "%LOG%" echo ---- disco !IDX! - diskpart devolveu !RC!
type X:\isoforge-dd.out >> "%LOG%"
if not "!RC!"=="0" goto :eof
if "!IDX!"=="!DISCO_MIDIA!" goto :eof
rem Sem findstr: ele NAO EXISTE no WinPE do instalador do 25H2 (nem where,
rem wmic, choice, timeout, more, sort). So for /f, find e o que o cmd faz.
rem A letra da midia na tabela de volumes deste disco: e o disco da midia.
set "TEM_MIDIA="
for /f "tokens=1-3" %%v in (X:\isoforge-dd.out) do if /i "%%v"=="Volume" if /i "%%x"=="%MIDIA:~0,1%" set "TEM_MIDIA=1"
if defined TEM_MIDIA goto :eof
rem Ventoy: a ISO montada como CD nao aparece na tabela de volumes do disco
rem que guarda o arquivo .iso. O volume VTOYEFI denuncia esse disco.
find /i "VTOYEFI" X:\isoforge-dd.out >nul && goto :eof
rem O barramento: o valor depois do ":" na linha "Tipo : SAS" ("Type" em
rem ingles). Compara o primeiro termo de cada valor com a lista de ACEITOS -
rem nenhuma outra linha do "detail disk" tem um valor desses.
set "BUS="
for /f "tokens=1,* delims=:" %%a in (X:\isoforge-dd.out) do (
  if not "%%b"=="" for /f "tokens=1" %%v in ("%%b") do (
    for %%t in (NVMe SATA ATA IDE SAS SCSI RAID MMC) do if /i "%%v"=="%%t" set "BUS=%%t"
  )
)
if not defined BUS (
  >> "%LOG%" echo disco !IDX!: barramento fora da lista de aceitos - recusado
  goto :eof
)
set /a NCAND+=1
set "CANDIDATOS=!CANDIDATOS! !IDX!"
set "ALVO=!IDX!"
copy /y X:\isoforge-dd.out X:\isoforge-alvo.out >nul
>> "%LOG%" echo disco !IDX!: aprovado - barramento !BUS!
goto :eof

:log
echo %*
>> "%LOG%" echo %date% %time% %*
goto :eof

:falha
call :log ERRO: %~1
echo.
echo  ===========================================================================
echo   IsoForge: %~1
echo.
echo   O disco ja foi apagado, entao nao da para seguir sozinho.
echo   O log esta em %LOG% - e, se a midia aceitar escrita,
echo   tambem em %MIDIA%\isoforge-pe.log.
echo   Tire uma foto desta tela.
echo  ===========================================================================
copy /y "%LOG%" "%MIDIA%\isoforge-pe.log" >nul 2>&1
copy /y X:\Windows\Logs\DISM\dism.log "%MIDIA%\isoforge-dism.log" >nul 2>&1
pause
goto :eof
""";
}
