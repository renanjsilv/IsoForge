using System.Diagnostics;
using System.IO;
using System.Text;

namespace IsoForge.Core;

/// <summary>
/// Baixa a fonte offline do Office usando o Office Deployment Tool
/// (setup.exe /download), gerando a pasta Office\Data que depois é embutida na ISO.
/// </summary>
public static class OfficeDownloader
{
    /// <summary>
    /// Informa se ja existe um download do Office na pasta, e o que ha nele. O
    /// chamador usa isto para PERGUNTAR antes de recomecar: retomar o download
    /// parcial de outra tentativa e a causa conhecida de o ODT entrar em loop.
    /// </summary>
    public static (bool existe, double gb, DateTime maisNovo) Inspecionar(string targetFolder)
    {
        var data = Path.Combine(targetFolder, "Office", "Data");
        if (!Directory.Exists(data)) return (false, 0, default);

        var arqs = new DirectoryInfo(data).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        if (arqs.Count == 0) return (false, 0, default);

        return (true, arqs.Sum(f => f.Length) / 1024.0 / 1024.0 / 1024.0, arqs.Max(f => f.LastWriteTime));
    }

    public static async Task DownloadAsync(string odtSetupExe, string officeConfigXml, string targetFolder,
        IProgress<string> log, CancellationToken ct, IProgress<string>? headline = null,
        bool limparDestino = false)
    {
        if (!File.Exists(odtSetupExe))
            throw new InvalidOperationException("setup.exe do Office Deployment Tool não encontrado. Adicione o Office na seção 4 primeiro.");

        Directory.CreateDirectory(targetFolder);

        // Apaga SO a subpasta Office que o proprio ODT cria — nunca a pasta escolhida
        // pelo usuario, que costuma ser Downloads ou Documentos.
        //
        // POR QUE: um download interrompido deixa o stream.x64.x-none.dat parcial ao
        // lado de manifestos (v64.cab, s640.cab) da tentativa anterior. Na tentativa
        // seguinte o ODT valida o stream contra manifestos de outra versao, nao fecha
        // e fica repetindo — o sintoma e "trava em X GB e nunca termina". Foi
        // diagnosticado numa pasta com arquivos de dois dias diferentes misturados.
        if (limparDestino)
        {
            var officeSub = Path.Combine(targetFolder, "Office");
            if (Directory.Exists(officeSub))
            {
                log.Report("Limpando o download anterior antes de recomecar...");
                try { Directory.Delete(officeSub, recursive: true); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"""
                        Nao foi possivel apagar {officeSub}:
                        {ex.Message}

                        Feche qualquer setup.exe ou Office em execucao e tente de novo.
                        """);
                }
            }
        }

        // Config EXCLUSIVO de download: so <Add> + <Logging>. Ver o comentario em
        // OfficeConfig.ForDownload — reusar o XML de instalacao aqui fazia o ODT
        // entrar no caminho de instalacao e falhar com ERROR_SHARING_VIOLATION em
        // toda maquina que ja tem Office instalado.
        var downloadConfig = OfficeConfig.ForDownload(officeConfigXml, targetFolder);
        var configPath = Path.Combine(targetFolder, "DownloadConfig.xml");
        File.WriteAllText(configPath, downloadConfig, new UTF8Encoding(false));

        // Copia o setup.exe para a pasta (necessário para a instalação offline depois).
        File.Copy(odtSetupExe, Path.Combine(targetFolder, "setup.exe"), overwrite: true);

        log.Report($"Baixando Office para {targetFolder} (~3,5 GB, pode demorar)...");

        var psi = new ProcessStartInfo(odtSetupExe, $"/download \"{configPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = targetFolder
        };
        using var p = new Process { StartInfo = psi };
        p.Start();

        // O ODT não reporta progresso; medimos a pasta Office\Data crescendo e mostramos
        // o total baixado (GB) no log e no cabeçalho — sem porcentagem estimada que "prende".
        var officeDir = Path.Combine(targetFolder, "Office");
        double lastLoggedGb = 0;
        long lastSize = 0;

        // "Travou" e medido pela ULTIMA ESCRITA na arvore, nao pelo tamanho total.
        // Tamanho e um sinal ruim aqui: num download retomado o stream.x64.x-none.dat
        // ja existe quase inteiro, entao o total fica parado por longos trechos
        // enquanto o BITS ainda escreve DENTRO dele — e o detector matava um download
        // vivo. Hora de escrita responde "alguem mexeu nos arquivos?", que e a
        // pergunta certa.
        var lastGrowth = DateTime.UtcNow;
        DateTime lastWrite = default;
        // 15 minutos, nao 4. MEDIDO nesta maquina: o Click-to-Run entra num laco de
        // Office.Identity.ConfigService (111 chamadas seguidas no log) e nao escreve
        // nada por vários minutos — mas DEPOIS retoma: uma execucao chegou a 2,97 GB.
        // Ou seja, o limite de 4 min estava matando download que ainda ia andar. Numa
        // maquina com M365 instalado e politica corporativa essas pausas sao normais.
        var stallLimit = TimeSpan.FromMinutes(15);
        log.Report("Office: iniciando download...");
        while (!p.HasExited)
        {
            await Task.Delay(2000, ct);
            long size = 0;
            var escrita = lastWrite;
            try
            {
                if (Directory.Exists(officeDir))
                {
                    var arqs = new DirectoryInfo(officeDir).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
                    size = arqs.Sum(f => f.Length);
                    if (arqs.Count > 0) escrita = arqs.Max(f => f.LastWriteTimeUtc);
                }
            }
            catch { /* arquivos em uso durante o download */ }
            var gb = size / 1024.0 / 1024.0 / 1024.0;
            // Mostra a ESPERA junto do total: sem isso uma pausa legitima de 6 minutos
            // e indistinguivel de um travamento, e o usuario mata o processo por conta.
            var paradoMin = (int)(DateTime.UtcNow - lastGrowth).TotalMinutes;
            headline?.Report(paradoMin >= 1
                ? $"{gb:F2} GB baixados — aguardando o Office ha {paradoMin} min"
                : $"{gb:F2} GB baixados");

            // Cresceu OU alguem escreveu: nos dois casos ha trabalho acontecendo.
            if (size > lastSize || escrita > lastWrite)
            {
                lastSize = Math.Max(lastSize, size);
                lastWrite = escrita;
                lastGrowth = DateTime.UtcNow;
                if (gb - lastLoggedGb >= 0.2)   // registra no log a cada ~200 MB
                {
                    log.Report($"  Office: {gb:F2} GB baixados...");
                    lastLoggedGb = gb;
                }
            }
            else if (DateTime.UtcNow - lastGrowth > stallLimit)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException("""
                    O download do Office travou: nenhum arquivo foi escrito por 15 minutos.

                    O que o log do Click-to-Run costuma mostrar nesse ponto e um laco de
                    Office.Identity.ConfigService: o C2R desta maquina nao fecha a
                    resolucao de identidade/configuracao e para de baixar. Isso e da
                    pilha do Office da maquina, nao do IsoForge — tipicamente politica
                    corporativa, proxy ou antivirus/EDR no caminho do config service.

                    O que costuma resolver, em ordem:
                      1. Rodar com a maquina fora da VPN corporativa.
                      2. Fazer o download em outra maquina/rede e apontar a pasta em
                         "Procurar..." (a pasta pronta funciona em qualquer maquina).
                      3. Usar o Office no modo ONLINE, que baixa no primeiro logon de
                         cada maquina instalada.

                    O log fica em %TEMP%, no arquivo .log com o nome da maquina.
                    """);
            }
        }
        headline?.Report("finalizando...");

        if (p.ExitCode != 0)
            throw new InvalidOperationException(Explicar(p.ExitCode, targetFolder));

        var dataDir = Path.Combine(targetFolder, "Office", "Data");
        if (!Directory.Exists(dataDir))
            throw new InvalidOperationException("Download terminou mas a pasta Office\\Data não foi criada. Revise o Configuration.xml.");

        var finalSize = new DirectoryInfo(dataDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        log.Report($"Office baixado: {finalSize / 1024.0 / 1024.0 / 1024.0:F2} GB em {dataDir}");
    }

    /// <summary>
    /// Traduz o codigo de saida do ODT. O setup.exe nao imprime nada util no
    /// console: o motivo real fica no log do Click-to-Run, que agora escrevemos na
    /// PROPRIA pasta de destino (ver OfficeConfig.ForDownload) em vez de no %TEMP%.
    /// </summary>
    static string Explicar(int codigo, string pasta)
    {
        var log = $"O log do Click-to-Run esta em {pasta} (arquivo .log com o nome da maquina).";

        // 32 = ERROR_SHARING_VIOLATION. Era o sintoma do bug do config: o ODT entrava
        // no caminho de INSTALACAO e batia no stream.x64.x-none.dat, que o servico
        // OfficeClickToRun da maquina mantem aberto.
        if (codigo == 32)
            return $"""
                O download do Office falhou: um arquivo estava em uso por outro processo
                (ERROR_SHARING_VIOLATION).

                Causas conhecidas, nesta ordem:
                  1. Uma tentativa anterior deixou o setup.exe ou o Click-to-Run rodando.
                     Feche o IsoForge, confira no Gerenciador de Tarefas se ha "setup" ou
                     "OfficeClickToRun" ativo, e tente de novo.
                  2. Antivirus ou EDR corporativo varrendo o arquivo enquanto ele e escrito.
                  3. A pasta de destino ja tinha um download parcial de outra versao.
                     Escolha uma pasta VAZIA.

                {log}
                """;

        if (codigo == 17002)
            return $"""
                O download do Office falhou: o ODT nao conseguiu concluir o streaming.
                Costuma ser rede instavel ou proxy corporativo bloqueando o CDN do Office.

                {log}
                """;

        return $"""
            O download do Office falhou (codigo {codigo}).

            {log}
            """;
    }
}