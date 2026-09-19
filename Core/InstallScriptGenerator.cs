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

    /// <summary>
    /// Marcador de "ja instalei" — em %ProgramData%, NAO em C:\Setup.
    ///
    /// POR QUE MUDOU DE LUGAR: no teste do Windows Sandbox a pasta C:\Setup e um
    /// MAPEAMENTO da pasta do HOST (&lt;saida&gt;\sources\$OEM$\$1\Setup) com
    /// ReadOnly=false. O marcador gravado no fim da 1a rodada ia parar no host e
    /// ninguem o apagava — entao da 2a rodada em diante o install.cmd saia na
    /// terceira linha, sem instalar nada e sem uma linha de log explicando. Quem
    /// testasse o Office duas vezes veria "nao funciona de nenhuma maneira" sem o
    /// ODT ter sido chamado nenhuma vez. %ProgramData% e estado da MAQUINA de
    /// destino (no Sandbox, descartavel a cada rodada), que e o que o marcador
    /// sempre quis dizer.
    /// </summary>
    internal const string DoneMarker = @"%ProgramData%\IsoForge\install.done";

    /// <summary>
    /// O ponto de encontro entre a tela cheia e este script: a tela grava ali o nome
    /// escolhido, DEPOIS de renomear a máquina, e o install.cmd espera o arquivo
    /// aparecer para seguir. Um arquivo, e não um código de saída, porque a tela é um
    /// processo separado que continua vivo depois da escolha — ela vira a página de
    /// progresso e acompanha o resto da instalação.
    /// </summary>
    internal const string UnitFileOnDisk = @"%ProgramData%\IsoForge\unidade.txt";

    /// <summary>
    /// A versão do IsoForge que gerou os scripts, para carimbar no install.log.
    /// Lida do assembly — no projeto de teste ela sai como a versão do próprio teste,
    /// o que é irrelevante: o que importa é o número chegar na máquina de destino.
    /// </summary>
    static string VersaoQueGerou =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    /// <summary>Nome do lancador que roda o install.cmd sem janela.</summary>
    public const string LauncherFileName = "install-oculto.vbs";

    /// <summary>
    /// O lancador SEM JANELA do install.cmd.
    ///
    /// O FirstLogonCommands abre um console, e com a tela cheia de progresso no ar ele
    /// ficava ATRAS dela: um retangulo preto nas bordas e no alt-tab, estragando o que a
    /// tela existe para entregar.
    ///
    /// E um .vbs e nao um "start /min" nem o -WindowStyle Hidden do PowerShell porque o
    /// WshShell.Run com estilo 0 nao chega a CRIAR a janela — os outros dois criam e
    /// escondem, e o console pisca por uma fracao de segundo. O ultimo parametro (True)
    /// faz o lancador esperar: o FirstLogonCommands e sincrono e precisa continuar sendo,
    /// senao o Windows segue para a area de trabalho no meio da instalacao.
    /// </summary>
    /// <summary>Nome do esperador que substitui o Explorer no 1o logon.</summary>
    public const string ShellStubFileName = "shell-espera.vbs";

    /// <summary>
    /// O SHELL do primeiro logon, no lugar do explorer.exe.
    ///
    /// PROBLEMA: numa maquina recem-formatada o Windows abre o menu Iniciar sozinho no
    /// primeiro logon, e ele vem POR CIMA da tela cheia — a escolha de unidade e o
    /// progresso ficavam atras. Nao ha chave suportada para "nao abrir o Iniciar", e a
    /// retomada de foco a cada 2 s era remendo: brigava com o Windows e perdia as vezes.
    ///
    /// SOLUCAO: o Explorer NAO SOBE. O Winlogon inicia este esperador como shell, entao
    /// nao ha menu Iniciar, nem barra de tarefas, nem area de trabalho para roubar a
    /// tela — so a tela cheia do provisionamento. Quando o provisionamento termina,
    /// este mesmo script devolve o Explorer.
    ///
    /// QUEM DESFAZ A TROCA E O install.cmd, no comeco e ELEVADO: escrever em
    /// HKLM...\Winlogon exige privilegio que este processo (sessao do usuario) nao tem.
    /// Fazer la, e logo no inicio, garante que qualquer falha posterior ainda deixe a
    /// maquina com o Explorer de volta no proximo boot.
    ///
    /// O tempo limite existe para o pior caso: se o provisionamento morrer sem gravar o
    /// marcador, a area de trabalho volta sozinha em vez de a maquina ficar sem shell.
    /// </summary>
    public static string ShellStub()
    {
        var sb = new StringBuilder();
        sb.AppendLine("' IsoForge - shell do 1o logon (ver InstallScriptGenerator.ShellStub)");
        sb.AppendLine("Dim s, fso, i, marcador");
        sb.AppendLine("Set s = CreateObject(\"WScript.Shell\")");
        sb.AppendLine("Set fso = CreateObject(\"Scripting.FileSystemObject\")");
        sb.AppendLine("marcador = s.ExpandEnvironmentStrings(\"%ProgramData%\") & \"\\IsoForge\\install.done\"");
        sb.AppendLine("For i = 1 To 7200");   // 2 h
        sb.AppendLine("  If fso.FileExists(marcador) Then Exit For");
        sb.AppendLine("  WScript.Sleep 1000");
        sb.AppendLine("Next");
        sb.AppendLine("' Devolve a area de trabalho. Na maquina real o install.cmd reinicia");
        sb.AppendLine("' logo em seguida; isto cobre o caso de nao haver reinicio.");
        sb.AppendLine("s.Run \"explorer.exe\", 1, False");
        return sb.ToString();
    }

    public static string Launcher()
    {
        var sb = new StringBuilder();
        sb.AppendLine("' IsoForge - roda o install.cmd sem janela.");
        sb.AppendLine("Dim s");
        sb.AppendLine("Set s = CreateObject(\"WScript.Shell\")");
        // As aspas duplas DOBRADAS sao o escape do VBScript: o caminho precisa ir
        // entre aspas porque C:\Setup pode ser renomeado no futuro para algo com espaco.
        sb.AppendLine($@"s.Run ""cmd /c """"{SetupDirOnDisk}\install.cmd"""""", 0, True");
        return sb.ToString();
    }

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
        // A VERSAO QUE GEROU A ISO, na primeira linha do log.
        //
        // Existe porque a pergunta "isso ja tem a correcao?" custou caro mais de uma vez:
        // uma ISO gerada por uma versao antiga se comporta como a versao antiga, e nao ha
        // como saber isso olhando a maquina de destino. Com o numero aqui, a primeira
        // linha do install.log responde sozinha.
        sb.AppendLine($">>\"%LOGFILE%\" echo IsoForge {VersaoQueGerou} - inicio: %date% %time%");
        sb.AppendLine("title IsoForge - Instalando aplicativos padrao, aguarde...");
        sb.AppendLine("echo Instalando aplicativos padrao. NAO feche esta janela.");
        // FECHA C:\Setup ANTES DE QUALQUER COISA.
        //
        // MEDIDO nesta maquina: uma pasta criada na raiz de C: HERDA
        // "Usuarios autenticados: Modify" — e os arquivos dentro dela tambem. Como
        // C:\Setup nasce da copia do $OEM$ para a raiz, ela nasce gravavel por qualquer
        // usuario padrao da maquina provisionada. Duas consequencias, as duas reais:
        //
        //  LEITURA  — ali ficam o install.log, o perfil de WiFi com a PSK da rede
        //             corporativa e os scripts do provisionamento. Qualquer funcionario
        //             com conta padrao lia tudo isso.
        //  ESCRITA  — no modo Entra ID uma tarefa agendada roda
        //             "powershell -File C:\Setup\RenameUnit.ps1" COMO SYSTEM no logon.
        //             Trocar esse arquivo era virar SYSTEM sem exploit nenhum.
        //
        // /inheritance:r corta a heranca da raiz; depois so SYSTEM e Administradores.
        // DEVOLVE O EXPLORER, e o mais cedo possivel.
        //
        // O autounattend trocou o shell do 1o logon por um esperador, para o menu
        // Iniciar nao subir por cima da tela cheia. Desfazer a troca AQUI, e nao no fim,
        // e deliberado: este e o ponto mais cedo em que ha privilegio para escrever em
        // HKLM (o esperador roda na sessao do usuario e nao consegue). Se qualquer coisa
        // adiante falhar, a maquina ja volta ao normal no proximo boot.
        if (!goldenAudit && c.FullscreenProgress)
        {
            sb.AppendLine(@"reg add ""HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"" /v Shell /t REG_SZ /d explorer.exe /f >nul 2>&1");
        }

        // NO SANDBOX, DERRUBA O EXPLORER NA MARRA.
        //
        // O Windows Sandbox NAO executa o autounattend: ele sobe uma imagem pronta,
        // com o Explorer ja como shell, e so roda o LogonCommand do .wsb. Ou seja, a
        // troca de shell do specialize — que na maquina real impede o menu Iniciar de
        // subir por cima da tela cheia — nunca chega a valer ali.
        //
        // Sem isto o teste MOSTRA O CONTRARIO do que vai acontecer em producao: barra
        // de tarefas, area de trabalho e o Iniciar aparecendo. Encerrar o Explorer aqui
        // reproduz o efeito e faz o teste valer alguma coisa. O proprio script o
        // devolve no fim (no lugar onde a maquina real reiniciaria).
        if (c.SandboxTest && c.FullscreenProgress)
        {
            sb.AppendLine("taskkill /f /im explorer.exe >nul 2>&1");
            sb.AppendLine(">>\"%LOGFILE%\" echo [Teste/Sandbox] Explorer encerrado: reproduz o 1o logon da maquina real, onde ele nem chega a subir.");
        }
        sb.AppendLine($"icacls \"{SetupDirOnDisk}\" /inheritance:r /grant \"*S-1-5-18:(OI)(CI)F\" /grant \"*S-1-5-32-544:(OI)(CI)F\" >nul 2>&1");
        sb.AppendLine($"set \"PROGRESSFILE={ProgressFileVar}\"");
        sb.AppendLine("if not exist \"%ProgramData%\\IsoForge\" mkdir \"%ProgramData%\\IsoForge\" >nul 2>&1");
        sb.AppendLine();

        // A TELA CHEIA e disparada aqui e o script SEGUE. Ela e um visualizador do
        // arquivo de status: se nao subir, a instalacao acontece igual — o contrario
        // (a tela chamando o install) faria uma falha de interface abortar tudo.
        if (c.FullscreenProgress)
        {
            Status(sb, "fase", 0, 0, "Preparando o sistema");
            sb.AppendLine($"start \"\" powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{SetupDirOnDisk}\\{ProgressUiGenerator.FileName}\"");
            sb.AppendLine();
        }

        // Idempotência: o FirstLogonCommands é re-executado pelo Windows se a máquina reiniciar
        // antes de ele concluir (é o que acontece na seleção de unidade). O marcador evita repetir
        // tudo (e o loop de "pedir a unidade de novo") no boot seguinte.
        if (!goldenAudit)
        {
            sb.AppendLine($"if exist \"{DoneMarker}\" (");
            sb.AppendLine("  >>\"%LOGFILE%\" echo IsoForge: instalacao ja concluida anteriormente; saindo.");
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
            if (ProgressUiGenerator.ComUnidade(c))
            {
                // A escolha acontece DENTRO da tela cheia, que já está no ar — este
                // script só espera o arquivo que ela grava ao confirmar.
                //
                // POR QUE DEIXOU DE SER UM SEGUNDO POWERSHELL: a tela de progresso sobe
                // com Topmost e um temporizador que reativa a janela a cada 2 s. Ela
                // COBRIA a tela de unidade, que ficava atrás esperando um clique que
                // nunca chegava — e o install.cmd, parado nesta linha, nunca seguia. Na
                // máquina de teste o provisionamento só destravou alternando de janela
                // pelo menu Iniciar. Duas janelas em tela cheia disputando o foco não
                // têm como dar certo; agora é uma só, com duas páginas.
                sb.AppendLine($"echo   aguardando a escolha na tela ({UnitFileOnDisk})...>> \"%LOGFILE%\"");
                sb.AppendLine("powershell -NoProfile -ExecutionPolicy Bypass -Command \"" +
                    $"$f='{UnitFileOnDisk}'; " +
                    // 2 h de teto: alguém pode ter saído da sala no meio. Se estourar, o
                    // provisionamento segue com o nome que o Windows deu, em vez de a
                    // máquina ficar parada para sempre.
                    "for($i=0;$i -lt 7200;$i++){ if(Test-Path $f){ Write-Output ('unidade escolhida: ' + (Get-Content $f -Raw).Trim()); exit 0 }; Start-Sleep -Seconds 1 }; " +
                    "Write-Output 'AVISO: ninguem escolheu a unidade em 2 h; seguindo sem renomear.'\">> \"%LOGFILE%\" 2>&1");
            }
            else
            {
                sb.AppendLine($"powershell -NoProfile -ExecutionPolicy Bypass -File \"{SetupDirOnDisk}\\{UnitSelectorGenerator.FileName}\">> \"%LOGFILE%\" 2>&1");
            }
            sb.AppendLine();
        }

        AppendDrivers(sb, c);
        AppendWifi(sb, c);
        AppendAppInstalls(sb, c);
        AppendAppearance(sb, c);
        AppendForti(sb, c);
        if (!goldenAudit) AppendDebloat(sb, c);

        // Expiração de senha do usuário local (não no modo golden — usuário ainda não existe)
        if (!goldenAudit && !string.IsNullOrWhiteSpace(c.UserName))
        {
            var flag = c.PasswordNeverExpires ? "$true" : "$false";
            // A aspa simples DOBRADA e como se escapa um literal do PowerShell. Sem isto,
            // um nome legitimo como O'Brien ja quebrava a linha — e um nome escolhido de
            // proposito saia do literal e virava comando, executado como administrador no
            // 1o logon. O SetupCompleteGenerator ja fazia este escape no MESMO campo;
            // aqui tinha ficado de fora.
            var usuarioPs = c.UserName.Replace("'", "''");
            sb.AppendLine("echo [Senha do usuario]>> \"%LOGFILE%\"");
            // O usuário é criado pelo OOBE (autounattend). No teste do Sandbox ele não existe,
            // então só ajusta se existir — evita o erro "User not found" no log.
            sb.AppendLine($"powershell -NoProfile -ExecutionPolicy Bypass -Command \"" +
                $"if (Get-LocalUser -Name '{usuarioPs}' -ErrorAction SilentlyContinue) {{ Set-LocalUser -Name '{usuarioPs}' -PasswordNeverExpires {flag} }} " +
                $"else {{ Write-Output 'Usuario {usuarioPs} ainda nao existe (criado pelo OOBE no ISO real); ignorado.' }}\">> \"%LOGFILE%\" 2>&1");
            sb.AppendLine();
        }

        // Script personalizado (não no modo golden — roda no deploy final)
        if (!goldenAudit)
            AppendPostScript(sb, c);

        // Relatório de provisionamento por último (lê o install.log já completo).
        if (!goldenAudit) AppendReport(sb, c);

        // O log da TELA vai junto do resto, em C:\Setup.
        //
        // Ele nasce em %ProgramData%, que no Windows Sandbox morre com a caixa e numa
        // maquina real ninguem sabe onde procurar. Foi exatamente o que faltou quando a
        // tela apareceu congelada em "Preparando o Windows": o motivo estava escrito,
        // num arquivo que ninguem conseguia mais alcancar. C:\Setup e a pasta mapeada
        // no teste e a pasta obvia na maquina de destino.
        if (c.FullscreenProgress)
        {
            sb.AppendLine($"copy /y \"%ProgramData%\\IsoForge\\ui.log\" \"{SetupDirOnDisk}\\tela.log\" >nul 2>&1");
            sb.AppendLine($"copy /y \"%ProgramData%\\IsoForge\\progress.txt\" \"{SetupDirOnDisk}\\tela-status.txt\" >nul 2>&1");
        }
        // APAGA OS SEGREDOS. O perfil de WiFi carrega a PSK da rede corporativa em texto
        // puro e ja cumpriu o papel (o netsh o importou para o sistema). Deixa-lo no disco
        // e guardar a senha da rede da empresa em toda maquina provisionada, para sempre.
        if (!goldenAudit && c.AutoConnectWifi && !string.IsNullOrWhiteSpace(c.WifiPassword))
        {
            sb.AppendLine($"del /f /q \"{SetupDirOnDisk}\\\\{ExtraScriptsGenerator.WifiProfileFileName}\" >nul 2>&1");
            sb.AppendLine(">>\"%LOGFILE%\" echo Perfil de WiFi removido do disco (a senha ja esta no sistema).");
        }
        sb.AppendLine(">>\"%LOGFILE%\" echo IsoForge - fim: %date% %time%");

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
            // "fim" faz a tela cheia mostrar o visto e FICAR na tela ate o reboot:
            // fechar aqui devolveria a area de trabalho por alguns segundos, que e
            // exatamente o que a tela existe para evitar.
            if (c.FullscreenProgress) Status(sb, "fim", 0, 0, "Concluido");
            // Marca como concluído ANTES de reiniciar: no boot seguinte o FirstLogonCommands
            // re-executa este script, que vê o marcador e sai (sem repetir/loopar).
            sb.AppendLine($"> \"{DoneMarker}\" echo %date% %time%");
            // REINICIA SEMPRE no fim (fora do Sandbox, que nao reinicia).
            //
            // Antes o reinicio estava amarrado a selecao de unidade — era o passo que
            // aplicava o nome do computador. Consequencia: quem nao usava selecao de
            // unidade terminava o provisionamento e ficava na area de trabalho, com
            // metade dos programas pedindo reinicio para completar (o proprio Office
            // registra componentes no boot seguinte). O reinicio nao e um detalhe da
            // renomeacao: e o ultimo passo do provisionamento, e a tela cheia conta com
            // ele — o estado "Tudo pronto / reiniciando em instantes" fica na tela ate a
            // maquina cair, justamente para nao devolver a area de trabalho no meio.
            if (c.SandboxTest)
            {
                sb.AppendLine("echo [Teste/Sandbox] reboot pulado (no ISO real a maquina reiniciaria aqui).>> \"%LOGFILE%\"");
                if (c.FullscreenProgress)
                {
                    // Devolve a area de trabalho para a pessoa poder inspecionar o
                    // resultado do teste. Na maquina real quem faz isso e o reinicio.
                    sb.AppendLine("start \"\" explorer.exe");
                    sb.AppendLine(">>\"%LOGFILE%\" echo [Teste/Sandbox] Explorer devolvido.");
                }
            }
            else
            {
                var motivo = c.UseUnitSelection && c.UnitMethod == UnitSelectionMethod.FirstLogon
                    ? "IsoForge: aplicando o nome do computador"
                    : "IsoForge: concluindo a instalacao";
                sb.AppendLine($"echo [Reinicio] {motivo}...>> \"%LOGFILE%\"");
                // 15 s: tempo de a tela cheia mostrar o visto de concluido. O /f evita
                // que um instalador com janela aberta cancele o reinicio.
                sb.AppendLine($"shutdown /r /f /t 15 /c \"{motivo}\"");
            }
            sb.AppendLine("exit /b 0");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Instala os aplicativos em modo silencioso (partilhado por install.cmd e SetupComplete.cmd).
    /// Assume que os instaladores já estão em C:\Setup\Apps e que %LOGFILE% está definido.
    /// </summary>
    /// <summary>Caminho do arquivo de status lido pela tela de progresso.</summary>
    internal const string ProgressFileVar = "%ProgramData%\\IsoForge\\progress.txt";

    /// <summary>
    /// Escreve uma linha de status para a tela cheia (ProgressUiGenerator). O pipe
    /// precisa de escape no cmd, senao vira redirecionamento.
    /// </summary>
    internal static void Status(StringBuilder sb, string etapa, int indice, int total, string rotulo)
    {
        var limpo = rotulo.Replace("^", "").Replace("|", "").Replace(">", "").Replace("<", "").Replace("&", "e");
        sb.AppendLine($"> \"%PROGRESSFILE%\" echo {etapa}^|{indice}^|{total}^|{limpo}");
    }

    internal static void AppendAppInstalls(StringBuilder sb, BuildConfig c)
    {
        var apps = c.Apps.Where(a => !string.IsNullOrWhiteSpace(a.InstallerPath)).ToList();
        int total = apps.Count;
        if (total == 0) return;

        sb.AppendLine($"echo Serao instalados {total} programa(s). Acompanhe o progresso abaixo:");
        sb.AppendLine($"echo   Progresso geral: {Bar(0, total)}");
        sb.AppendLine();

        for (int idx = 0; idx < total; idx++)
        {
            var app = apps[idx];
            int n = idx + 1;
            var fileName = Path.GetFileName(app.InstallerPath);
            sb.AppendLine($"echo [{app.Name}]>> \"%LOGFILE%\"");
            // Mostra o programa atual (n de total) no titulo da janela e no corpo, com a barra geral.
            sb.AppendLine($"title IsoForge - Instalando ({n}/{total}) {app.Name}");
            if (c.FullscreenProgress) Status(sb, "app", n, total, app.Name);
            sb.AppendLine("echo.");
            sb.AppendLine($"echo === [{n}/{total}] {app.Name} ===");
            sb.AppendLine($"echo   Progresso geral: {Bar(idx, total)}");
            // Se o app precisa de internet, garante conexao antes (mostra a tela pedindo p/ conectar).
            if (NeedsInternet(app, c))
                AppendWaitForInternet(sb, app.Name);
            AppendWaitMsi(sb); // espera o Windows Installer ficar livre (evita erro 1618)

            if (app.Kind == AppKind.Office)
            {
                AppendOfficePreFlight(sb, c, n);
                // O Click-to-Run baixa via BITS/Delivery Optimization: garante que os servicos estejam ativos
                // (ajuda no download online; no Windows real ja rodam, custo zero).
                sb.AppendLine("sc config BITS start= delayed-auto >nul 2>&1 & net start BITS >nul 2>&1");
                sb.AppendLine("sc config DoSvc start= demand >nul 2>&1 & net start DoSvc >nul 2>&1");
                AppendOdtComCaoDeGuarda(sb, c.OfficeOffline ? OdtTimeoutMin : OdtTimeoutOnlineMin);
                // O codigo do ODT tem de ser lido AQUI. Antes, o "codigo de saida" no fim
                // do bloco vinha depois do laco de espera em PowerShell — ou seja, era o
                // codigo do PowerShell, e uma falha do Office aparecia no log como 0.
                sb.AppendLine("set \"ODTEXIT=%errorlevel%\"");
                // O REDIRECIONAMENTO VEM ANTES DO ECHO, de proposito. Escrito como
                // 'echo ... %ODTEXIT%>> "%LOGFILE%"' o cmd le o UNICO DIGITO colado no '>>'
                // como especificador de HANDLE: com ODTEXIT=0 vira '0>>' (redireciona o
                // STDIN) e a linha some do log; com 1 vira '1>>' e o log recebe a frase SEM
                // o numero. Ou seja, o log ficava mudo justamente nos dois codigos mais
                // comuns — 0 (sucesso) e 1 (falha generica) —, que e o motivo de quem olhou
                // o install.log ter concluido que o ODT nem chegou a rodar. Medido com um
                // stub no lugar do setup.exe: RC=0 nao gravava nada; RC=30015 gravava certo.
                sb.AppendLine(">>\"%LOGFILE%\" echo   ODT /configure devolveu %ODTEXIT%");
                // O setup.exe pode RETORNAR antes de o Click-to-Run terminar (instala em segundo
                // plano). Sem esperar, um reboot posterior truncaria o Office -> nao instala.
                // Aguarda o Click-to-Run concluir (ClientVersionToReport aparece ao finalizar).
                sb.AppendLine(">>\"%LOGFILE%\" echo   aguardando o Office concluir (Click-to-Run) e conferindo o ESTADO...");
                AppendOfficeVeredito(sb);
                sb.AppendLine("set \"OFFINST=%errorlevel%\"");
                sb.AppendLine("if not \"%OFFINST%\"==\"0\" (");
                sb.AppendLine($"  >>\"%LOGFILE%\" echo   ATENCAO: o Office NAO instalou ^(codigo do ODT: %ODTEXIT%^). Os logs do Click-to-Run estao em {OfficeLogDir}.");
                sb.AppendLine(")");
                // O log do Click-to-Run e a UNICA coisa que diz por que o Office falhou, e ele
                // fica no %TEMP% com um nome imprevisivel. Na maquina do cliente ninguem vai
                // procurar la: o diagnostico do ciclo anterior so foi possivel porque alguem
                // copiou esse arquivo de dentro do Sandbox a mao. Agora ele viaja junto do
                // install.log.
                AppendColetaLogC2R(sb);
                // Sempre: o desvio da checagem previa (fonte offline incompleta) cai aqui.
                sb.AppendLine($":office_fim_{n}");
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
            // Para o Office o "codigo de saida" generico seria o do ultimo powershell do
            // bloco — um 0 tranquilizador logo abaixo de um "o Office NAO instalou". O
            // veredito do Office ja foi gravado acima, pelo ESTADO da maquina.
            if (app.Kind != AppKind.Office)
                sb.AppendLine($">>\"%LOGFILE%\" echo   codigo de saida: %errorlevel%");
            sb.AppendLine($"echo   [{n}/{total}] {app.Name}: concluido.   {Bar(n, total)}");
            sb.AppendLine();
        }
        sb.AppendLine("title IsoForge - Instalacao dos programas concluida");
    }

    /// <summary>
    /// Barra de progresso GERAL (programas concluidos / total) para exibir no echo do CMD.
    /// Retorna com '%%' escapado porque o texto vai dentro de um 'echo' no batch.
    /// </summary>
    static string Bar(int done, int total)
    {
        const int width = 24;
        int filled = total <= 0 ? width : (int)Math.Round(width * done / (double)total);
        filled = Math.Max(0, Math.Min(width, filled));
        int pct = total <= 0 ? 100 : (int)Math.Round(100.0 * done / total);
        return $"[{new string('#', filled)}{new string('-', width - filled)}] {pct}%% ({done}/{total})";
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

    /// <summary>
    /// Minutos que o ODT tem para terminar antes de o cão-de-guarda matá-lo. OFFLINE lê
    /// 3,6 GB do disco local (10–20 min na prática); ONLINE ainda precisa BAIXAR esses
    /// 3,6 GB, e num link corporativo lento isso passa fácil de uma hora — matar um
    /// download em andamento seria trocar um defeito por outro.
    /// </summary>
    internal const int OdtTimeoutMin = 40;
    internal const int OdtTimeoutOnlineMin = 120;

    /// <summary>Código que o script registra quando o ODT estourou o tempo (ERROR_TIMEOUT).</summary>
    internal const int OdtTimeoutExit = 1460;

    /// <summary>
    /// Chama o <c>setup.exe /configure</c> COM LIMITE DE TEMPO e depois de esperar
    /// qualquer operação Click-to-Run que já esteja em curso.
    ///
    /// POR QUE (medido, não suposto): o ODT pode terminar o trabalho, registrar
    /// <c>"Bootstrapper Finished", ExitCode 0</c> no próprio log, fechar a telemetria… e
    /// NÃO SAIR DO PROCESSO. Amostrado de 3 em 3 segundos, 11 minutos depois do
    /// "Finished": CPU idêntica (zero consumo), nenhuma conexão TCP, as 15 threads em
    /// ThreadState=Wait. Havia duas instâncias do setup.exe concorrendo; matar uma fez a
    /// outra sair sozinha no mesmo instante — uma segurava um lock global. Como o
    /// install.cmd chamava o ODT de forma SÍNCRONA e sem limite, o provisionamento ficava
    /// parado para sempre em "Progresso geral: 0%", sem caixa de erro nenhuma. Desligar a
    /// caixa modal (Display Level=None) resolveu só UMA das duas formas de travar.
    ///
    /// A concorrência não é hipotética no destino: numa instalação nova do Windows 11 o
    /// stub pré-instalado do Microsoft 365 costuma disparar o Click-to-Run no primeiro
    /// logon — exatamente quando o install.cmd roda. Por isso o script espera o outro
    /// terminar ANTES de chamar o nosso.
    /// </summary>
    internal static void AppendOdtComCaoDeGuarda(StringBuilder sb, int limiteMin)
    {
        var exe = $"{AppsDirOnDisk}\\Office\\setup.exe";
        var cfgXml = $"{AppsDirOnDisk}\\Office\\Configuration.xml";
        var limiteMs = limiteMin * 60 * 1000;

        sb.AppendLine("powershell -NoProfile -ExecutionPolicy Bypass -Command \"" +
            "$ErrorActionPreference='SilentlyContinue';" +
            // 1) Espera (ate 10 min) o Click-to-Run que o proprio Windows possa ter
            //    disparado. Olha so os processos de INSTALACAO (setup / OfficeC2RClient),
            //    nunca o servico OfficeClickToRun, que fica vivo para sempre depois que
            //    ha Office na maquina e faria a espera estourar sempre.
            "for($i=0;$i -lt 120;$i++){ " +
            "$b=@(Get-Process setup,OfficeC2RClient -EA SilentlyContinue); " +
            "if($b.Count -eq 0){ break }; " +
            "if($i -eq 0){ Write-Output ('  aguardando outra operacao Click-to-Run em curso: '+($b.Count)+' processo(s)') }; " +
            "Start-Sleep -Seconds 5 };" +
            // 2) Roda o ODT em segundo plano e vigia o relogio.
            "$o=Join-Path $env:TEMP 'IsoForge-odt.out.log'; $e=Join-Path $env:TEMP 'IsoForge-odt.err.log';" +
            $"$p=Start-Process -FilePath '{exe}' -ArgumentList '/configure','{cfgXml}' -NoNewWindow -PassThru -RedirectStandardOutput $o -RedirectStandardError $e;" +
            // $p.Handle força o PowerShell a guardar o handle do processo; sem isso o
            // $p.ExitCode volta $null quando se usa -PassThru sem -Wait, e o codigo do ODT
            // se perderia de novo.
            "$null=$p.Handle;" +
            $"if($p.WaitForExit({limiteMs})){{ $rc=$p.ExitCode }} else {{ " +
            $"Write-Output '  TIMEOUT: o ODT passou de {limiteMin} min sem sair; encerrando (o processo trava depois de terminar o trabalho).'; " +
            "Stop-Process -Id $p.Id -Force -EA SilentlyContinue; " +
            "Get-Process setup,OfficeC2RClient -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue; " +
            $"$rc={OdtTimeoutExit} }};" +
            "Get-Content $o,$e -EA SilentlyContinue;" +
            "Remove-Item $o,$e -Force -EA SilentlyContinue;" +
            "exit $rc\">> \"%LOGFILE%\" 2>&1");
    }

    /// <summary>
    /// Decide se o Office instalou pelo ESTADO DA MAQUINA, não pelo código de saída do ODT.
    /// Sai com 0 (instalado) ou 1 (não instalado).
    ///
    /// POR QUE (medido no Windows Sandbox, sem rede, com o payload real): numa execução em
    /// que o Office instalou COMPLETO — ClientVersionToReport 16.0.20326.20132,
    /// O365ProPlusRetail.MediaType = Local, WINWORD.EXE no disco, serviço ClickToRunSvc
    /// rodando — o ODT devolveu <b>17002</b>. As únicas atividades que falharam foram
    /// Office.Identity.ConfigService e a telemetria Aria, que dependem de rede (ausente de
    /// propósito no teste); todas as InstallTask terminaram Success:true. Com o portão
    /// antigo (<c>if not "%ODTEXIT%"=="0"</c>) o script gravaria "o Office NAO instalou" e
    /// pularia a espera do Click-to-Run — um falso NEGATIVO num Office perfeitamente
    /// instalado, e um reinício no meio do trabalho em segundo plano.
    ///
    /// O MediaType vale a linha no log: ele sai "Local" quando a instalação veio da fonte
    /// offline e "CDN" quando veio da internet. É a única prova direta, na máquina do
    /// cliente, de que o modo offline funcionou.
    /// </summary>
    internal static void AppendOfficeVeredito(StringBuilder sb)
    {
        sb.AppendLine("powershell -NoProfile -ExecutionPolicy Bypass -Command \"" +
            "$k='HKLM:\\SOFTWARE\\Microsoft\\Office\\ClickToRun\\Configuration';" +
            // Espera longa (40 min) quando o ODT terminou o trabalho; um minuto de
            // cortesia quando ele nem chegou a comecar.
            //
            // 17002 CONTA COMO SUCESSO. Ele foi o codigo das DUAS instalacoes que deram
            // certo na validacao (offline sem rede e online): o Office ficou completo e
            // as unicas atividades que falharam foram as que dependem de rede
            // (Office.Identity.ConfigService e telemetria). Tratando 17002 como erro, a
            // espera caia de 40 min para 60 s — nas medicoes o registro ja estava
            // escrito e passou, mas isso foi sorte: numa maquina lenta o Click-to-Run
            // ainda estaria terminando e o script gravaria "Office AUSENTE" num Office
            // que instalou. Codigos como 1603 ou 0x80072EE7 continuam curtos, porque ali
            // o bootstrapper realmente nao comecou.
            "$ok=@('0','17002','17004'); $lim=160; if($ok -notcontains $env:ODTEXIT){ $lim=4 };" +
            "$c=$null;" +
            "for($i=0;$i -lt $lim;$i++){ $c=Get-ItemProperty $k -EA SilentlyContinue; " +
            "if($c -and $c.ClientVersionToReport){ break }; Start-Sleep -Seconds 15 };" +
            "$w=@('C:\\Program Files\\Microsoft Office\\root\\Office16\\WINWORD.EXE'," +
            "'C:\\Program Files (x86)\\Microsoft Office\\root\\Office16\\WINWORD.EXE') " +
            "| Where-Object { Test-Path $_ } | Select-Object -First 1;" +
            "$mt='?'; if($c){ $p=@($c.PSObject.Properties | Where-Object { $_.Name -like '*.MediaType' }); " +
            "if($p.Count -gt 0){ $mt=[string]$p[0].Value } };" +
            // O TAMANHO do WINWORD.EXE junto do caminho: depois que a maquina (ou o
            // Sandbox) some, "existia o arquivo" nao responde "estava inteiro?".
            "$tam=0; if($w){ try { $tam=(Get-Item $w).Length } catch {} };" +
            "if($c -and $c.ClientVersionToReport -and $w){ " +
            "Write-Output ('  Office INSTALADO: ' + $c.ClientVersionToReport + ' - midia: ' + $mt + ' - ' + $w + ' (' + $tam + ' bytes)'); exit 0 };" +
            "Write-Output ('  Office AUSENTE: sem ClientVersionToReport no registro e sem WINWORD.EXE no disco.'); " +
            "exit 1\">> \"%LOGFILE%\" 2>&1");
    }

    /// <summary>Pasta, na máquina de destino, para onde os logs do Click-to-Run são copiados.</summary>
    internal const string OfficeLogDir = @"C:\Setup\OfficeLogs";

    /// <summary>
    /// Copia os logs do Click-to-Run (%TEMP% e C:\Windows\Temp) para C:\Setup\OfficeLogs.
    ///
    /// O nome do arquivo é imprevisível (&lt;maquina&gt;-&lt;data&gt;-&lt;hora&gt;.log, UTF-16) e é
    /// nele — não no nosso install.log — que está o motivo real de uma falha do Office: foi
    /// esse arquivo que mostrou o pré-requisito MSIxC2RCultureReachable pedindo o s641033.cab.
    /// Dizer "procure em %TEMP%" no log não ajuda ninguém no cliente.
    /// </summary>
    internal static void AppendColetaLogC2R(StringBuilder sb)
    {
        sb.AppendLine("powershell -NoProfile -ExecutionPolicy Bypass -Command \"" +
            $"$d='{OfficeLogDir}'; New-Item -ItemType Directory -Force -Path $d | Out-Null;" +
            "$lim=(Get-Date).AddHours(-12);" +
            "foreach($t in @($env:TEMP,'C:\\Windows\\Temp')){ if($t -and (Test-Path $t)){ " +
            "Get-ChildItem -Path $t -Filter '*.log' -File -EA SilentlyContinue " +
            "| Where-Object { $_.LastWriteTime -gt $lim -and $_.Length -lt 20971520 } " +
            "| Sort-Object LastWriteTime -Descending | Select-Object -First 8 " +
            "| ForEach-Object { Copy-Item $_.FullName (Join-Path $d $_.Name) -Force -EA SilentlyContinue } } };" +
            "Write-Output ('  logs do Click-to-Run copiados para ' + $d)\">> \"%LOGFILE%\" 2>&1");
    }

    /// <summary>Um app precisa de internet no 1º logon? (online installer do Office ou do FortiClient, etc.)</summary>
    /// <summary>
    /// Confere a fonte offline do Office NA MAQUINA DE DESTINO, antes de chamar o ODT.
    ///
    /// Por que: quando o Office "pede internet" numa ISO offline, a pergunta a responder
    /// e uma so — o payload chegou em C:\Setup\Apps\Office\Office\Data\&lt;versao&gt;?
    /// O XML e o payload foram VERIFICADOS na maquina que gera (setup.exe /download
    /// sobre a fonte pronta: exit 0, 0 MB), entao o que resta e o transporte: o $OEM$
    /// copiou os 3,6 GB? o stream chegou inteiro? Sem esta checagem o sintoma no destino
    /// era uma caixa de erro generica da Microsoft, sem nada no nosso log.
    ///
    /// A versao e o tamanho esperados sao GRAVADOS AQUI, na geracao, quando a fonte esta
    /// em maos. No destino e so comparar.
    /// </summary>
    static void AppendOfficePreFlight(StringBuilder sb, BuildConfig c, int n)
    {
        var raiz = $"{AppsDirOnDisk}\\Office\\Office\\Data";

        if (!c.OfficeOffline)
        {
            sb.AppendLine(">>\"%LOGFILE%\" echo   Office ONLINE: sem fonte local, vai BAIXAR da internet.");
            return;
        }

        var fonte = OfficeSource.Validar(c.OfficeSourceFolder ?? "");
        var versao = fonte.versao;
        var criticos = OfficeSource.ArquivosCriticos(c.OfficeSourceFolder ?? "");

        sb.AppendLine($">>\"%LOGFILE%\" echo   Office OFFLINE: conferindo a fonte em {raiz}");
        // ISTO E UM PORTAO, NAO UM RELATORIO. Antes as checagens eram so 'echo': a
        // ausencia da pasta da versao e a do stream imprimiam FALHA no log e a execucao
        // SEGUIA para o ODT do mesmo jeito; e o tamanho do stream era ecoado, nunca
        // comparado. Ou seja, exatamente no caso que a checagem existia para pegar — a
        // copia parcial do $OEM$ — o sintoma voltava a ser a caixa generica da Microsoft.
        // Agora cada arquivo e conferido com o TAMANHO que ele tem na maquina que gera, e
        // qualquer divergencia pula o ODT com o motivo escrito no log.
        sb.AppendLine("set \"OFFOK=1\"");
        sb.AppendLine($"if not exist \"{raiz}\" (");
        sb.AppendLine($"  >>\"%LOGFILE%\" echo   FALHA: a fonte local NAO chegou na maquina ^(o payload do Office nao foi copiado da midia, pasta $OEM$^).");
        sb.AppendLine("  set \"OFFOK=0\"");
        sb.AppendLine(")");

        if (versao != null)
        {
            sb.AppendLine($"if not exist \"{raiz}\\{versao}\" (");
            sb.AppendLine($"  >>\"%LOGFILE%\" echo   FALHA: a pasta da versao {versao} NAO chegou na maquina.");
            sb.AppendLine("  set \"OFFOK=0\"");
            sb.AppendLine(")");
        }

        // Um 'for' de um item so: e a unica forma de o batch ler o TAMANHO de um arquivo
        // (%%~zF). Para arquivo inexistente %%~zF vem vazio, entao esta mesma linha cobre
        // "nao chegou" e "chegou truncado".
        foreach (var (rel, tamanho) in criticos)
        {
            sb.AppendLine($"for %%F in (\"{raiz}\\{rel}\") do if not \"%%~zF\"==\"{tamanho}\" (");
            sb.AppendLine($"  >>\"%LOGFILE%\" echo   FALHA: {rel} ausente ou com tamanho errado - esperado {tamanho} bytes, achei [%%~zF].");
            sb.AppendLine("  set \"OFFOK=0\"");
            sb.AppendLine(")");
        }

        sb.AppendLine("if \"%OFFOK%\"==\"1\" (");
        sb.AppendLine($"  >>\"%LOGFILE%\" echo   fonte offline conferida: versao {versao}, {criticos.Count} arquivo^(s^) com o tamanho esperado.");
        sb.AppendLine(") else (");
        // Sem a fonte inteira NAO se chama o ODT. Nao adianta contar com AllowCdnFallback
        // para "falhar rapido": medido, o ODT le o atributo e nao o aplica (o tri-state
        // interno fica 'unspecified' tanto com False quanto com True). Quem tem de barrar
        // a ida ao CDN somos nos, aqui.
        sb.AppendLine("  >>\"%LOGFILE%\" echo   Office PULADO: a fonte offline nao chegou inteira. Chamar o ODT agora so produziria a caixa generica da Microsoft - we weren't able to download a required file - numa maquina sem rede.");
        sb.AppendLine($"  goto :office_fim_{n}");
        sb.AppendLine(")");
    }

    internal static bool NeedsInternet(AppEntry app, BuildConfig c)
    {
        if (app.Kind == AppKind.Office) return !c.OfficeOffline; // Office online baixa no 1º logon
        return app.RequiresInternet;
    }

    /// <summary>Algum app do build precisa de internet? (usado para gerar o WaitForInternet.ps1)</summary>
    public static bool AnyNeedsInternet(BuildConfig c) => c.Apps.Any(a => !string.IsNullOrWhiteSpace(a.InstallerPath) && NeedsInternet(a, c));

    /// <summary>Espera por internet (chama o WaitForInternet.ps1): mostra a tela e volta sozinho ao conectar.</summary>
    internal static void AppendWaitForInternet(StringBuilder sb, string appName)
    {
        sb.AppendLine($"echo   {appName} precisa de internet; verificando conexao...>> \"%LOGFILE%\"");
        sb.AppendLine($"powershell -NoProfile -ExecutionPolicy Bypass -File \"{SetupDirOnDisk}\\{ExtraScriptsGenerator.WaitForInternetFileName}\">> \"%LOGFILE%\" 2>&1");
    }

    /// <summary>
    /// Instala os drivers do fabricante no 1º logon (reforço ao offlineServicing) e limpa a pasta.
    /// Os .inf estão em C:\Drivers (copiados via sources\$OEM$\$1\Drivers).
    /// </summary>
    internal static void AppendDrivers(StringBuilder sb, BuildConfig c)
    {
        if (string.IsNullOrWhiteSpace(c.DriverPackPath)) return;
        sb.AppendLine("if exist \"C:\\Drivers\" (");
        sb.AppendLine("  echo [Drivers] instalando drivers do fabricante...>> \"%LOGFILE%\"");
        sb.AppendLine("  echo Instalando drivers do fabricante...");
        sb.AppendLine("  pnputil /add-driver C:\\Drivers\\*.inf /subdirs /install>> \"%LOGFILE%\" 2>&1");
        sb.AppendLine("  >>\"%LOGFILE%\" echo   codigo de saida: %errorlevel%");
        sb.AppendLine("  rmdir /s /q C:\\Drivers");   // ja estao no DriverStore; libera o espaco
        sb.AppendLine(")");
        sb.AppendLine();
    }

    /// <summary>Conecta automaticamente a uma rede Wi-Fi (perfil WLAN + netsh) no início do 1º logon.</summary>
    internal static void AppendWifi(StringBuilder sb, BuildConfig c)
    {
        if (!ExtraScriptsGenerator.HasWifi(c)) return;
        var ssid = c.WifiSsid.Replace("\"", "").Trim();
        sb.AppendLine("echo [Wi-Fi] conectando automaticamente...>> \"%LOGFILE%\"");
        sb.AppendLine($"echo Conectando a rede Wi-Fi \"{ssid}\"...");
        sb.AppendLine($"netsh wlan add profile filename=\"{SetupDirOnDisk}\\{ExtraScriptsGenerator.WifiProfileFileName}\" user=all>> \"%LOGFILE%\" 2>&1");
        sb.AppendLine($"netsh wlan connect name=\"{ssid}\" ssid=\"{ssid}\">> \"%LOGFILE%\" 2>&1");
        // Aguarda associar + DHCP antes de seguir (o WaitForInternet cobre o resto).
        sb.AppendLine("ping -n 10 127.0.0.1 >nul");
        sb.AppendLine();
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

    /// <summary>Otimização/debloat (chama o Debloat.ps1 em C:\Setup).</summary>
    internal static void AppendDebloat(StringBuilder sb, BuildConfig c)
    {
        if (!DebloatGenerator.Has(c)) return;
        sb.AppendLine("echo [Otimizacao/debloat]>> \"%LOGFILE%\"");
        sb.AppendLine("echo Otimizando o Windows (debloat)...");
        sb.AppendLine($"powershell -NoProfile -ExecutionPolicy Bypass -File \"{SetupDirOnDisk}\\{DebloatGenerator.FileName}\">> \"%LOGFILE%\" 2>&1");
        sb.AppendLine();
    }

    /// <summary>Relatório de provisionamento (chama o Report.ps1 em C:\Setup).</summary>
    internal static void AppendReport(StringBuilder sb, BuildConfig c)
    {
        if (!c.GenerateReport) return;
        sb.AppendLine("echo [Relatorio de provisionamento]>> \"%LOGFILE%\"");
        sb.AppendLine($"powershell -NoProfile -ExecutionPolicy Bypass -File \"{SetupDirOnDisk}\\{ReportGenerator.FileName}\">> \"%LOGFILE%\" 2>&1");
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
        sb.AppendLine($">>\"%LOGFILE%\" echo   codigo de saida: %errorlevel%");
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
