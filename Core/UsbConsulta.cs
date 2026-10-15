using System.Management;

namespace IsoForge.Core;

/// <summary>
/// De onde veio a informação do disco. A listagem e a conferência TÊM de usar a mesma
/// fonte: comparar nome e tamanho vindos de provedores diferentes recusaria toda gravação.
/// </summary>
public enum FonteDisco { Desconhecida, MsftDisk }

/// <summary>Por que a consulta não respondeu. Cada valor vira uma frase diferente na tela.</summary>
public enum FalhaListagem
{
    /// <summary>Perguntei e o Windows respondeu. Zero discos aqui significa zero discos mesmo.</summary>
    Nenhuma,
    /// <summary>O namespace de armazenamento não respondeu (serviço parado, provedor quebrado).</summary>
    ProvedorIndisponivel,
    PermissaoNegada,
    /// <summary>Falta um componente do Windows para o System.Management funcionar.</summary>
    BibliotecaAusente,
    Timeout,
    Desconhecida,
}

/// <summary>
/// Um disco que o Windows mostrou e que NÃO pode ser gravado, com o motivo.
///
/// É um tipo DIFERENTE de <see cref="UsbDisco"/> de propósito. Um recusado não tem como,
/// nem por acidente, ser passado para a gravação: quem impede é o compilador, não um if
/// que alguém pode remover numa refatoração distraída.
/// </summary>
public sealed record DiscoRecusado(uint? Numero, string Nome, ulong? Bytes, string Motivo)
{
    public string Descricao =>
        $"Disco {(Numero?.ToString() ?? "?")} — {Nome}"
        + (Bytes is > 0 ? $" ({Bytes!.Value / 1024.0 / 1024 / 1024:F1} GB)" : "")
        + $" — {Motivo}";
}

/// <summary>
/// O que a consulta encontrou. Distingue as três situações que antes eram a mesma coisa:
/// "não há pendrive", "há discos mas nenhum serve" e "não consegui perguntar".
/// </summary>
public sealed record ResultadoListagem(
    IReadOnlyList<UsbDisco> Discos,
    IReadOnlyList<DiscoRecusado> Recusados,
    FalhaListagem Falha,
    string? Motivo,
    string? Detalhe)
{
    public static ResultadoListagem Ok(List<UsbDisco> discos, List<DiscoRecusado> recusados) =>
        new(discos, recusados, FalhaListagem.Nenhuma, null, null);

    public static ResultadoListagem Erro(FalhaListagem falha, string motivo, string? detalhe,
                                         List<DiscoRecusado>? vistos = null) =>
        new(Array.Empty<UsbDisco>(), vistos ?? new List<DiscoRecusado>(), falha, motivo, detalhe);
}

/// <summary>
/// A única coisa que decide quem pode aparecer na lista de graváveis.
///
/// Não toca em WMI nem em disco: recebe os valores crus e devolve o motivo da recusa.
/// Isso existe para poder ser testada em tabela — um disco de sistema, um de arranque, um
/// NVMe interno, um campo faltando — sem nenhum disco de verdade por perto.
/// </summary>
public static class TravaDisco
{
    // Valores do ValueMap de MSFT_Disk.BusType. É UInt16 NUMÉRICO: a string "USB" que o
    // Get-Disk mostra é um enum que o cmdlet projeta por cima do CIM; em WMI cru vem 7.
    public const ushort BusUsb = 7;
    public const ushort BusSd = 12;

    /// <summary>
    /// Null quando o disco pode entrar na lista; caso contrário, o motivo da recusa,
    /// escrito para a pessoa ler.
    ///
    /// REGRA QUE NÃO SE NEGOCIA: todo desconhecido é recusa. "Não consegui verificar" não
    /// pode ser codificado como "verifiquei e está tudo bem" — aqui o custo do erro é o
    /// disco de alguém.
    ///
    /// E é LISTA BRANCA, nunca lista negra: só 7 e 12 entram. Uma lista negra deixaria
    /// passar o barramento 0 (desconhecido), o 14 (virtual), o 16 (Storage Spaces) e
    /// qualquer valor que a Microsoft venha a acrescentar depois.
    /// </summary>
    public static string? MotivoDeRecusa(ushort? barramento, bool? ehSistema, bool? ehArranque,
                                         bool? arrancaPorEle, uint? numero, ulong? tamanho)
    {
        if (numero is null) return "o Windows não informou o número do disco";
        if (numero == 0) return "é o disco 0 (o disco do sistema nesta máquina)";

        if (ehSistema is null) return "o Windows não informou se este é o disco de SISTEMA";
        if (ehSistema == true) return "é o disco de SISTEMA (onde ficam os arquivos de arranque)";
        if (ehArranque is null) return "o Windows não informou se este é o disco de ARRANQUE";
        if (ehArranque == true) return "é o disco de ARRANQUE (onde fica o \\Windows)";
        // BootFromDisk é reforço, não trava: só recusa quando vem TRUE. Ausente não recusa
        // porque IsSystem e IsBoot, que são as travas de verdade, já responderam.
        if (arrancaPorEle == true) return "a máquina está configurada para arrancar por ele";

        if (barramento is null) return "o Windows não informou o barramento";
        if (barramento != BusUsb && barramento != BusSd)
            return $"barramento {NomeBarramento(barramento.Value)} — só USB e SD entram na lista";

        if (tamanho is null) return "o Windows não informou o tamanho";
        if (tamanho.Value == 0) return "tamanho 0 (leitor de cartão vazio?)";

        return null;
    }

    /// <summary>
    /// Nome do barramento a partir do número. NUNCA rotule como "USB" o que simplesmente
    /// passou pelo filtro: a tela é a última leitura humana antes de apagar o disco, e se
    /// o filtro um dia afrouxar ela estaria mentindo exatamente no pior momento.
    /// </summary>
    public static string NomeBarramento(ushort bus) => bus switch
    {
        0 => "desconhecido", 1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394",
        5 => "SSA", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID", 9 => "iSCSI",
        10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "virtual",
        15 => "virtual em arquivo", 16 => "Storage Spaces", 17 => "NVMe",
        _ => $"código {bus}",
    };
}

/// <summary>
/// Pergunta ao Windows quais discos existem, DENTRO do próprio processo.
///
/// POR QUE NÃO É MAIS POWERSHELL: a versão anterior montava um script e o executava com
/// <c>powershell.exe -ExecutionPolicy Bypass -EncodedCommand &lt;base64&gt;</c>. Essa linha de
/// comando é assinatura conhecida de código malicioso sem arquivo e está nas regras
/// padrão de praticamente todo EDR — num parque gerenciado ela gera alerta e pode ser
/// bloqueada. Pior: a saída de erro era concatenada à saída normal, então qualquer linha
/// em stderr corrompia o JSON, a desserialização falhava, e um <c>catch</c> vazio
/// transformava isso em "nenhum pendrive encontrado". A pessoa não tinha como distinguir
/// "não há pendrive" de "não consegui perguntar".
///
/// Consultar o WMI em processo resolve os dois: não nasce processo nenhum, e o erro chega
/// como exceção tipada em vez de texto misturado.
/// </summary>
public static class UsbConsulta
{
    const string EscopoStorage = @"\\.\root\Microsoft\Windows\Storage";

    /// <summary>
    /// O WMI exige apartamento MTA e a thread da interface do WPF é STA — chamar direto
    /// faz o System.Management desviar por uma thread interna, que é onde nasce o
    /// travamento clássico. Task.Run cai no pool, que já é MTA.
    /// </summary>
    public static Task<ResultadoListagem> ListarAsync(CancellationToken ct = default) =>
        Task.Run(Listar, ct);

    static ResultadoListagem Listar()
    {
        var discos = new List<UsbDisco>();
        var recusados = new List<DiscoRecusado>();

        try
        {
            var escopo = new ManagementScope(EscopoStorage);
            escopo.Connect();   // falha aqui se o namespace não existir ou faltar permissão

            // SELECT * de propósito: quero TODOS os discos para poder dizer "vi 3 e nenhum
            // serve". Sem WHERE porque o filtro precisa rodar em C#, onde um campo nulo
            // vira recusa — numa cláusula WHERE ele sumiria em silêncio. O disco do sistema
            // chegar à memória não é risco: ele vira DiscoRecusado, que é outro tipo, e a
            // gravação só aceita UsbDisco.
            var opcoes = new System.Management.EnumerationOptions
            {
                ReturnImmediately = true,
                Rewindable = false,
                Timeout = TimeSpan.FromSeconds(20),
            };

            using var busca = new ManagementObjectSearcher(
                escopo, new ObjectQuery("SELECT * FROM MSFT_Disk"), opcoes);

            foreach (ManagementBaseObject d in busca.Get())
                using (d)
                {
                    var numero = LerUInt(d, "Number");
                    var nome = LerString(d, "FriendlyName") ?? "(sem nome)";
                    var bytes = LerULong(d, "Size");
                    var bus = LerUShort(d, "BusType");

                    var motivo = TravaDisco.MotivoDeRecusa(
                        bus, LerBool(d, "IsSystem"), LerBool(d, "IsBoot"),
                        LerBool(d, "BootFromDisk"), numero, bytes);

                    if (motivo != null)
                    {
                        recusados.Add(new DiscoRecusado(numero, nome, bytes, motivo));
                        continue;
                    }

                    discos.Add(new UsbDisco(
                        Numero: (int)numero!.Value,
                        Nome: nome,
                        Bytes: (long)bytes!.Value,
                        Barramento: TravaDisco.NomeBarramento(bus!.Value),
                        Letras: LetrasDoDisco(escopo, numero.Value),
                        Fonte: FonteDisco.MsftDisk));
                }

            return ResultadoListagem.Ok(discos, recusados);
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return ComDiagnostico(ResultadoListagem.Erro(FalhaListagem.PermissaoNegada,
                "O Windows negou acesso às informações de disco para este usuário. "
                + "Isto não é falta de pendrive.", Detalhar(ex), recusados));
        }
        catch (UnauthorizedAccessException ex)
        {
            return ComDiagnostico(ResultadoListagem.Erro(FalhaListagem.PermissaoNegada,
                "O Windows negou acesso às informações de disco para este usuário. "
                + "Isto não é falta de pendrive.", Detalhar(ex), recusados));
        }
        catch (ManagementException ex)
        {
            var ehTimeout = ex.ErrorCode.ToString().Contains("imedout", StringComparison.OrdinalIgnoreCase);
            return ComDiagnostico(ResultadoListagem.Erro(
                ehTimeout ? FalhaListagem.Timeout : FalhaListagem.ProvedorIndisponivel,
                ehTimeout
                    ? "O Windows não respondeu a tempo sobre os discos. Isso costuma acontecer "
                      + "com leitor de cartão vazio ou disco travado."
                    : "O Windows não conseguiu responder sobre os discos. Isto é um problema do "
                      + "Windows nesta máquina, não do IsoForge.",
                Detalhar(ex), recusados));
        }
        catch (Exception ex) when (ex is TypeInitializationException
                                      or PlatformNotSupportedException
                                      or System.ComponentModel.Win32Exception
                                      or DllNotFoundException)
        {
            // O System.Management carrega um binário nativo de dentro da pasta do .NET
            // Framework do Windows. Quando ele falta, a exceção NÃO se parece com uma falha
            // de WMI — por isso este catch separado, com uma frase que faz sentido.
            return ComDiagnostico(ResultadoListagem.Erro(FalhaListagem.BibliotecaAusente,
                "Falta um componente do Windows para consultar os discos nesta máquina.",
                Detalhar(ex), recusados));
        }
        catch (Exception ex)
        {
            return ComDiagnostico(ResultadoListagem.Erro(FalhaListagem.Desconhecida,
                "Não consegui perguntar ao Windows quais discos existem.", Detalhar(ex), recusados));
        }
    }

    // ------------------------------------------------------------------ conferência

    /// <summary>
    /// Relê um disco pelo número e diz se ele ainda pode ser gravado. Mesma trava da
    /// listagem, mesma fonte — e o modelo e o tamanho precisam bater com o que a pessoa
    /// escolheu na tela, porque entre escolher e gravar o pendrive pode ter sido trocado
    /// por outro e o número de disco é reaproveitado.
    /// </summary>
    public static Task<(bool ok, string? motivo)> ConferirAsync(UsbDisco alvo, CancellationToken ct = default) =>
        Task.Run<(bool, string?)>(() =>
        {
            // Um UsbDisco que não veio da consulta desta classe não passa. Isso fecha o
            // caminho de alguém fabricar um alvo à mão e levá-lo à gravação.
            if (alvo.Fonte != FonteDisco.MsftDisk)
                return (false, "O disco escolhido não veio da consulta ao Windows. Atualize a lista e escolha de novo.");
            if (alvo.Numero == 0)
                return (false, "Disco 0 é o disco do sistema nesta máquina. Recusado.");

            try
            {
                var escopo = new ManagementScope(EscopoStorage);
                escopo.Connect();

                // Com o mesmo prazo da listagem, e pelo mesmo motivo: sem Timeout, um
                // provedor de armazenamento engasgado bloqueia sem prazo, e o
                // CancellationToken do Task.Run não interrompe um Get() já em curso.
                var opcoes = new System.Management.EnumerationOptions
                {
                    ReturnImmediately = true,
                    Rewindable = false,
                    Timeout = TimeSpan.FromSeconds(20),
                };

                using var busca = new ManagementObjectSearcher(escopo,
                    new ObjectQuery($"SELECT * FROM MSFT_Disk WHERE Number = {alvo.Numero}"), opcoes);

                foreach (ManagementBaseObject d in busca.Get())
                    using (d)
                    {
                        var motivo = TravaDisco.MotivoDeRecusa(
                            LerUShort(d, "BusType"), LerBool(d, "IsSystem"), LerBool(d, "IsBoot"),
                            LerBool(d, "BootFromDisk"), LerUInt(d, "Number"), LerULong(d, "Size"));
                        if (motivo != null)
                            return (false, $"O disco {alvo.Numero} não pode ser gravado: {motivo}.");

                        var nome = LerString(d, "FriendlyName") ?? "(sem nome)";
                        var bytes = LerULong(d, "Size");
                        if (!string.Equals(nome, alvo.Nome, StringComparison.OrdinalIgnoreCase)
                            || bytes is null || (long)bytes.Value != alvo.Bytes)
                            return (false, $"O disco {alvo.Numero} não é mais o mesmo que você escolheu "
                                         + $"(agora: {nome}). Escolha de novo.");

                        return (true, null);
                    }

                return (false, $"O disco {alvo.Numero} não está mais presente. Reconecte o pendrive e escolha de novo.");
            }
            catch (Exception ex)
            {
                // Falhou a conferência? Então NÃO grava. Um erro aqui nunca pode virar "pode".
                return (false, $"Não consegui confirmar o disco {alvo.Numero} ({Detalhar(ex)}). Nada foi gravado.");
            }
        }, ct);

    // ------------------------------------------------------------------ texto da tela

    /// <summary>
    /// A frase que a tela mostra. Mora aqui, no núcleo, e não no code-behind, para a suíte
    /// poder afirmar sobre ela sem precisar de WPF nem de disco.
    ///
    /// "Nenhum pendrive encontrado" só existe NESTA função e só na condição em que ela é
    /// verdade: perguntei, o Windows respondeu, e não havia disco nenhum.
    /// </summary>
    public static (string titulo, string? detalhe, bool ehErro) Mensagem(ResultadoListagem r)
    {
        if (r.Falha == FalhaListagem.Nenhuma)
        {
            // Achou pendrive: a frase não fala dos outros discos, e o detalhe vem vazio.
            // Contar "outros 3 não podem ser usados" só levava quem está escolhendo um
            // pendrive a ler sobre o HD interno da própria máquina.
            if (r.Discos.Count > 0)
                return ($"{r.Discos.Count} disco(s) removível(is) encontrado(s).", null, false);

            if (r.Recusados.Count == 0)
                return ("Nenhum pendrive encontrado. Conecte um e clique em Atualizar lista.", null, false);

            // Vi discos e nenhum serve. Este é EXATAMENTE o caso que antes era
            // indistinguível de "não tem pendrive".
            return ($"Vi {r.Recusados.Count} disco(s), e nenhum pode ser usado para gravação.",
                    TextoRecusados(r), false);
        }

        var partes = new[] { r.Detalhe, TextoRecusados(r) }.Where(s => !string.IsNullOrWhiteSpace(s));
        return (r.Motivo ?? "Não consegui perguntar ao Windows quais discos existem.",
                string.Join("\n\n", partes), true);
    }

    static string? TextoRecusados(ResultadoListagem r) =>
        r.Recusados.Count == 0
            ? null
            : "Discos que NÃO podem ser gravados:\n"
              + string.Join("\n", r.Recusados.Select(d => "  • " + d.Descricao));

    // ------------------------------------------------------------------ diagnóstico

    /// <summary>
    /// Quando a consulta boa falha, olha pelo caminho antigo (Win32_DiskDrive) só para
    /// poder DIZER o que existe.
    ///
    /// Nada daqui vira opção de gravação, e não é descuido: o Win32_DiskDrive não tem
    /// IsSystem nem IsBoot, e não há equivalente. Sobram InterfaceType (cinco valores
    /// legados, sem SATA, sem NVMe, sem SD) e MediaType (que reflete um bit escolhido pelo
    /// fabricante — pendrive grande costuma se declarar fixo). Aceitar "SCSI" para não
    /// perder pendrive USB 3 seria aceitar quase todo disco interno, numa classe que não
    /// sabe dizer qual é o do sistema. Um recuo sem as travas é pior que recuo nenhum.
    /// </summary>
    static ResultadoListagem ComDiagnostico(ResultadoListagem falha)
    {
        if (falha.Falha == FalhaListagem.Nenhuma) return falha;

        var vistos = new List<DiscoRecusado>(falha.Recusados);
        try
        {
            using var busca = new ManagementObjectSearcher(@"\\.\root\cimv2",
                "SELECT Index, Model, Size, InterfaceType FROM Win32_DiskDrive");

            foreach (ManagementBaseObject d in busca.Get())
                using (d)
                {
                    var indice = LerUInt(d, "Index");
                    if (indice == 0xFFFFFFFF) indice = null;   // não mapeia para disco físico
                    vistos.Add(new DiscoRecusado(
                        indice,
                        LerString(d, "Model") ?? "(sem nome)",
                        LerULong(d, "Size"),
                        $"visto pelo caminho alternativo ({LerString(d, "InterfaceType") ?? "?"}) — "
                        + "sem o serviço de armazenamento do Windows não há como garantir que não é "
                        + "o disco do sistema, então nada é oferecido para gravação"));
                }
        }
        catch { /* o diagnóstico é um extra; a falha principal já está dita */ }

        return falha with { Recusados = vistos };
    }

    // ------------------------------------------------------------------ leitura crua

    static string Detalhar(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    // Ler uma propriedade que não existe LANÇA. Estes leitores devolvem null nesse caso, e
    // null é recusa na trava — a falha é sempre para o lado seguro.
    static object? Bruto(ManagementBaseObject o, string nome)
    { try { return o[nome]; } catch { return null; } }

    static ushort? LerUShort(ManagementBaseObject o, string n)
    { var v = Bruto(o, n); try { return v is null ? null : Convert.ToUInt16(v); } catch { return null; } }

    static uint? LerUInt(ManagementBaseObject o, string n)
    { var v = Bruto(o, n); try { return v is null ? null : Convert.ToUInt32(v); } catch { return null; } }

    static ulong? LerULong(ManagementBaseObject o, string n)
    { var v = Bruto(o, n); try { return v is null ? null : Convert.ToUInt64(v); } catch { return null; } }

    static bool? LerBool(ManagementBaseObject o, string n)
    { var v = Bruto(o, n); try { return v is null ? null : Convert.ToBoolean(v); } catch { return null; } }

    static string? LerString(ManagementBaseObject o, string n)
    { var v = Bruto(o, n) as string; return string.IsNullOrWhiteSpace(v) ? null : v.Trim(); }

    /// <summary>
    /// Letras montadas, só para a pessoa reconhecer o pendrive na lista. É cosmético: se
    /// falhar, devolve vazio e a listagem continua.
    ///
    /// Prefere AccessPaths a DriveLetter porque DriveLetter é Char16 e, quando não há letra,
    /// não vem string vazia — vem o caractere zero.
    /// </summary>
    static string LetrasDoDisco(ManagementScope escopo, uint disco)
    {
        try
        {
            using var busca = new ManagementObjectSearcher(escopo, new ObjectQuery(
                $"SELECT DriveLetter, AccessPaths FROM MSFT_Partition WHERE DiskNumber = {disco}"));

            var letras = new List<string>();
            foreach (ManagementBaseObject p in busca.Get())
                using (p)
                {
                    if (Bruto(p, "AccessPaths") is string[] caminhos)
                        letras.AddRange(caminhos
                            .Where(c => c.Length == 3 && char.IsLetter(c[0]) && c[1] == ':')
                            .Select(c => c[..2].ToUpperInvariant()));
                    else if (Bruto(p, "DriveLetter") is { } dl)
                    {
                        try
                        {
                            var c = Convert.ToChar(dl);
                            if (c != '\0') letras.Add(char.ToUpperInvariant(c) + ":");
                        }
                        catch { }
                    }
                }
            return string.Join(" ", letras.Distinct());
        }
        catch { return ""; }
    }
}
