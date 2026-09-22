using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace IsoForge.Core;

/// <summary>Um disco candidato a virar pendrive de instalação.</summary>
/// <param name="Numero">Número do disco (o mesmo do diskpart / Get-Disk).</param>
/// <param name="Nome">Modelo, como o Windows informa.</param>
/// <param name="Bytes">Tamanho total.</param>
/// <param name="Barramento">USB, SD...</param>
/// <param name="Letras">Letras já montadas, para a pessoa reconhecer o pendrive.</param>
/// <param name="Fonte">
/// De onde a informação veio. A gravação recusa qualquer disco que não tenha saído da
/// consulta ao Windows — isso fecha o caminho de alguém fabricar um alvo à mão.
/// </param>
public sealed record UsbDisco(int Numero, string Nome, long Bytes, string Barramento, string Letras,
                              FonteDisco Fonte = FonteDisco.Desconhecida)
{
    public double Gb => Bytes / 1024.0 / 1024.0 / 1024.0;

    /// <summary>Como aparece na tela de escolha. É o que a pessoa lê antes de apagar o disco.</summary>
    public string Rotulo =>
        $"Disco {Numero} — {Nome} ({Gb:F1} GB{(string.IsNullOrWhiteSpace(Letras) ? "" : ", " + Letras)})";
}

/// <summary>
/// Grava o conteúdo já preparado pelo pipeline direto num pendinho USB, deixando-o
/// inicializável — o mesmo serviço do Rufus, sem sair do IsoForge.
///
/// POR QUE NÃO É "GERAR A ISO E DEPOIS GRAVAR": o pipeline monta a árvore de arquivos
/// completa (<c>staging</c>) e só no último passo o oscdimg a transforma em ISO. Para
/// o pendrive, a ISO é um intermediário inútil: dá para copiar a árvore direto. Poupa
/// o tempo do oscdimg e os gigabytes do arquivo .iso em disco.
///
/// DECISÕES E SEUS PORQUÊS:
///
/// • <b>GPT + FAT32, arranque só por UEFI.</b> É o que o Windows 11 exige de qualquer
///   jeito (ele não instala em BIOS legado), e é o formato que toda placa UEFI lê sem
///   ajuda. Assim não é preciso bootsect, nem MBR híbrido, nem atalho de terceiros.
///
/// • <b>O install.wim é partido em .swm.</b> FAT32 não guarda arquivo maior que 4 GiB, e
///   o install.wim do Windows 11 passa disso. O próprio Setup do Windows lê
///   <c>install*.swm</c> nativamente, então partir é a saída limpa — é o mesmo caminho
///   que o Rufus toma por padrão.
///
/// • <b>A partição de arranque tem no máximo 32 GB.</b> O formatador do Windows recusa
///   FAT32 acima disso. O que sobra do pendrive vira uma segunda partição NTFS de
///   dados, em vez de espaço perdido.
///
/// • <b>Nada é gravado sem passar por <see cref="Conferir"/>.</b> Escrever no disco
///   errado destrói dados de verdade, então a checagem é repetida imediatamente antes
///   da formatação, com o disco relido do sistema — não vale o objeto que a tela tinha
///   em mãos, que pode estar velho se o pendrive foi trocado no meio.
/// </summary>
public sealed class UsbWriter
{
    readonly Action<string> _log;
    readonly Action<int>? _pct;

    public UsbWriter(Action<string> log, Action<int>? pct = null)
    {
        _log = log;
        _pct = pct;
    }

    /// <summary>Tamanho máximo de cada pedaço do install.swm, em MB. Abaixo do limite de 4 GiB do FAT32.</summary>
    const int PedacoSwmMb = 3800;

    /// <summary>Teto do formatador FAT32 do Windows.</summary>
    const long MaxFat32Bytes = 32L * 1024 * 1024 * 1024;

    // ------------------------------------------------------------------ listagem

    /// <summary>
    /// Discos que PODEM ser gravados, e o motivo de cada um que não pode.
    ///
    /// Quem pergunta ao Windows é o <see cref="UsbConsulta"/>, dentro do próprio processo.
    /// Antes daqui saía um script PowerShell executado com <c>-EncodedCommand</c>, que é
    /// assinatura conhecida de código malicioso sem arquivo: num parque com EDR isso gera
    /// alerta e pode ser bloqueado, e o bloqueio se disfarçava de "nenhum pendrive
    /// encontrado" porque qualquer falha virava lista vazia.
    /// </summary>
    public static Task<ResultadoListagem> ListarAsync(CancellationToken ct = default) =>
        UsbConsulta.ListarAsync(ct);

    // ------------------------------------------------------------------ conferência

    /// <summary>
    /// Relê o disco do sistema e diz se ele ainda pode ser gravado. Devolve o motivo
    /// quando não pode.
    ///
    /// Relê de propósito: entre a pessoa escolher na tela e a gravação começar, o
    /// pendrive pode ter sido trocado por outro — e o número do disco é reaproveitado.
    /// Gravar com base no objeto antigo é como formatar por um retrato.
    /// </summary>
    public static Task<(bool ok, string? motivo)> Conferir(UsbDisco alvo, CancellationToken ct = default) =>
        UsbConsulta.ConferirAsync(alvo, ct);

    // ------------------------------------------------------------------ gravação

    /// <summary>
    /// Formata o pendrive e copia a árvore de <paramref name="staging"/> para ele.
    /// APAGA TUDO no disco indicado. Quem chama já confirmou com a pessoa.
    /// </summary>
    public Task GravarAsync(UsbDisco alvo, string staging, string rotulo, CancellationToken ct) =>
        GravarDeAsync(alvo, staging, rotulo, ct);

    /// <summary>
    /// Formata o pendrive e copia para ele a arvore de <paramref name="origem"/>.
    /// APAGA TUDO no disco indicado. Quem chama ja confirmou com a pessoa.
    ///
    /// A origem tanto pode ser a pasta de trabalho do pipeline quanto a raiz de uma ISO
    /// ja montada: nos dois casos e a arvore final do instalador, e o trabalho aqui e o
    /// mesmo.
    /// </summary>
    public async Task GravarDeAsync(UsbDisco alvo, string origem, string rotulo, CancellationToken ct)
    {
        if (!EhAdministrador())
            throw new InvalidOperationException(
                "Gravar no pendrive exige Administrador (particionar e formatar são operações "
                + "privilegiadas). Volte à tela anterior e clique em Gravar de novo — o IsoForge "
                + "pede a elevação sozinho.");

        var (ok, motivo) = await Conferir(alvo, ct);
        if (!ok) throw new InvalidOperationException("Gravação cancelada: " + motivo);

        var necessarioGb = TamanhoPastaGb(origem);
        _log($"Conteúdo a gravar: {necessarioGb:F2} GB.");
        if (necessarioGb + 0.5 > alvo.Gb)
            throw new InvalidOperationException(
                $"O pendrive tem {alvo.Gb:F1} GB e o conteúdo ocupa {necessarioGb:F2} GB. Use um maior.");

        _log($"APAGANDO E FORMATANDO: {alvo.Rotulo}");
        var letra = await PrepararDiscoAsync(alvo, rotulo, necessarioGb, ct);
        _pct?.Invoke(78);
        _log($"Pendrive preparado como {letra}: (GPT + FAT32, arranque por UEFI).");

        // Partir o install.wim ANTES de copiar: copiar um arquivo de 5 GB para FAT32
        // falha no meio, e aí já se perdeu o tempo da cópia.
        await PartirInstallWimAsync(origem, ct);
        _pct?.Invoke(82);

        _log("Copiando os arquivos para o pendrive...");
        await CopiarAsync(origem, letra + ":\\", ct);
        _pct?.Invoke(98);
    }

    // ------------------------------------------------------------------ passos

    /// <summary>
    /// Limpa, inicializa em GPT e cria a partição de arranque em FAT32. Devolve a letra.
    /// Sobrando espaço, cria uma segunda partição NTFS de dados em vez de desperdiçá-lo.
    /// </summary>
    async Task<char> PrepararDiscoAsync(UsbDisco alvo, string rotulo, double necessarioGb, CancellationToken ct)
    {
        // A partição de arranque: o que o conteúdo pede com folga, limitada ao teto do
        // FAT32. Se o pendrive for menor que o teto, usa ele inteiro.
        var bootBytes = Math.Min(alvo.Bytes, MaxFat32Bytes);
        var precisaBytes = (long)((necessarioGb + 1.0) * 1024 * 1024 * 1024);
        if (precisaBytes < bootBytes) bootBytes = Math.Max(precisaBytes, 8L * 1024 * 1024 * 1024);
        if (bootBytes > alvo.Bytes) bootBytes = alvo.Bytes;

        var usaDiscoTodo = bootBytes >= alvo.Bytes - (64L * 1024 * 1024);
        var rotuloSeguro = new string((rotulo ?? "WIN11").Where(char.IsLetterOrDigit).Take(11).ToArray());
        if (rotuloSeguro.Length == 0) rotuloSeguro = "WIN11";

        var ps = $$"""
            $ErrorActionPreference = 'Stop'
            $n = {{alvo.Numero}}
            $d = Get-Disk -Number $n
            if ($d.IsSystem -or $d.IsBoot -or $d.BusType -notin @('USB','SD')) {
                throw "RECUSADO: o disco $n nao e um removivel comum."
            }
            Clear-Disk -Number $n -RemoveData -RemoveOEM -Confirm:$false
            Initialize-Disk -Number $n -PartitionStyle GPT -ErrorAction SilentlyContinue
            $p = {{(usaDiscoTodo
                    ? "New-Partition -DiskNumber $n -UseMaximumSize -AssignDriveLetter"
                    : $"New-Partition -DiskNumber $n -Size {bootBytes} -AssignDriveLetter")}}
            Format-Volume -Partition $p -FileSystem FAT32 -NewFileSystemLabel '{{rotuloSeguro}}' -Confirm:$false | Out-Null
            {{(usaDiscoTodo ? "" : """
            # O que sobrou vira partição de dados em vez de espaço perdido.
            try {
                $r = New-Partition -DiskNumber $n -UseMaximumSize -AssignDriveLetter
                Format-Volume -Partition $r -FileSystem NTFS -NewFileSystemLabel 'DADOS' -Confirm:$false | Out-Null
            } catch { }
            """)}}
            'LETRA:' + $p.DriveLetter
            """;

        var saida = await PowerShellAsync(ps, ct, linha => _log("  " + linha));
        var m = System.Text.RegularExpressions.Regex.Match(saida, @"LETRA:([A-Za-z])");
        if (!m.Success)
            throw new InvalidOperationException(
                "Não consegui preparar o pendrive (a partição não recebeu letra). Saída:\n" + saida);
        return char.ToUpperInvariant(m.Groups[1].Value[0]);
    }

    /// <summary>
    /// Parte o install.wim em install.swm quando ele não cabe em FAT32.
    /// O Setup do Windows lê install*.swm sozinho — não é gambiarra, é o formato que a
    /// Microsoft criou justamente para mídia FAT32.
    /// </summary>
    async Task PartirInstallWimAsync(string staging, CancellationToken ct)
    {
        var wim = Path.Combine(staging, "sources", "install.wim");
        if (!File.Exists(wim)) return;                      // pode ser install.esd, que já é menor

        var tamanho = new FileInfo(wim).Length;
        const long limiteFat32 = 4L * 1024 * 1024 * 1024 - 1;
        if (tamanho <= limiteFat32)
        {
            _log($"install.wim tem {tamanho / 1024.0 / 1024 / 1024:F2} GB e cabe em FAT32; não precisa partir.");
            return;
        }

        _log($"install.wim tem {tamanho / 1024.0 / 1024 / 1024:F2} GB — acima do limite do FAT32. Partindo em .swm...");
        var swm = Path.Combine(staging, "sources", "install.swm");
        var exit = await RodarAsync("dism.exe",
            $"/English /Split-Image /ImageFile:\"{wim}\" /SWMFile:\"{swm}\" /FileSize:{PedacoSwmMb}", ct);
        if (exit != 0)
            throw new InvalidOperationException(
                $"Não consegui partir o install.wim (DISM devolveu {exit}). Sem isso o arquivo não cabe no pendrive.");

        File.Delete(wim);
        var pedacos = Directory.GetFiles(Path.Combine(staging, "sources"), "install*.swm").Length;
        _log($"install.wim partido em {pedacos} arquivo(s) .swm. O Setup do Windows lê esse formato nativamente.");
    }

    async Task CopiarAsync(string origem, string destino, CancellationToken ct)
    {
        // Mesmos parâmetros da cópia do pipeline: /MT acelera muito em pendrive rápido,
        // e os /N* tiram a listagem arquivo a arquivo do log.
        var exit = await RodarAsync("robocopy.exe",
            $"\"{origem.TrimEnd('\\')}\" \"{destino.TrimEnd('\\')}\" /E /MT:8 /R:2 /W:2 /NFL /NDL /NJH /NP", ct);
        // robocopy usa códigos de bits: < 8 é sucesso (0 = nada a copiar, 1 = copiou...).
        if (exit >= 8)
            throw new InvalidOperationException($"A cópia para o pendrive falhou (robocopy devolveu {exit}).");
    }

    // ------------------------------------------------------------------ utilidades

    public static bool EhAdministrador()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    static double TamanhoPastaGb(string pasta) =>
        new DirectoryInfo(pasta).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
        / 1024.0 / 1024.0 / 1024.0;

    /// <summary>
    /// Roda um script PowerShell gravando-o num arquivo e passando o caminho com
    /// <c>-File</c>.
    ///
    /// NÃO É -EncodedCommand, e a diferença importa. Base64 na linha de comando resolvia o
    /// escape de aspas e quebras de linha, mas
    /// <c>powershell -ExecutionPolicy Bypass -EncodedCommand &lt;b64&gt;</c> é a assinatura
    /// catalogada de código malicioso sem arquivo: num parque com EDR isso gera alerta e
    /// pode ser bloqueado — e este caminho roda ELEVADO, que é justamente onde um bloqueio
    /// dói mais. Com um arquivo, quem for investigar lê o que o programa mandou executar.
    ///
    /// O caminho do arquivo vai para o log, de propósito: se o antivírus reclamar, a pessoa
    /// tem o que mostrar para a TI.
    /// </summary>
    static async Task<string> PowerShellAsync(string script, CancellationToken ct, Action<string>? aoVivo = null)
    {
        var pasta = Path.Combine(Path.GetTempPath(), "IsoForge");
        Directory.CreateDirectory(pasta);
        var arquivo = Path.Combine(pasta, $"pendrive-{DateTime.Now:yyyyMMdd-HHmmss}.ps1");

        // BOM em UTF-8: sem ele o PowerShell 5.1 lê o arquivo como ANSI e qualquer acento
        // dentro do script vira outro caractere.
        await File.WriteAllTextAsync(arquivo, script, new UTF8Encoding(true), ct);
        aoVivo?.Invoke($"script: {arquivo}");

        try
        {
            return await CapturarAsync("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{arquivo}\"", ct, aoVivo);
        }
        finally
        {
            try { File.Delete(arquivo); } catch { /* melhor esforço */ }
        }
    }

    static async Task<string> CapturarAsync(string exe, string args, CancellationToken ct, Action<string>? aoVivo)
    {
        var sb = new StringBuilder();
        await RodarCoreAsync(exe, args, ct, linha => { sb.AppendLine(linha); aoVivo?.Invoke(linha); });
        return sb.ToString();
    }

    Task<int> RodarAsync(string exe, string args, CancellationToken ct) =>
        RodarCoreAsync(exe, args, ct, linha => _log("  " + linha));

    static async Task<int> RodarCoreAsync(string exe, string args, CancellationToken ct, Action<string> aoVivo)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) aoVivo(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) aoVivo(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }
}
