using System.Diagnostics;
using System.Runtime.InteropServices;

namespace IsoForge.Core;

/// <summary>
/// Acompanha quanto um processo já escreveu, e traduz isso em porcentagem.
///
/// POR QUE ASSIM E NÃO DE OUTRO JEITO:
///
/// A pergunta "quanto já foi copiado para o pendrive?" tem três respostas possíveis e duas
/// delas mentem.
///
/// Ler a saída do robocopy não serve: ela é traduzida (muda com o idioma do Windows) e as
/// opções que deixam o log limpo — /NFL /NDL /NP — justamente suprimem o que daria para ler.
///
/// Medir o espaço livre no destino também mente, e feio. O FAT32 não tem arquivo esparso:
/// quando o destino recebe um <c>.swm</c> de 3,8 GB, a cadeia de clusters inteira pode ser
/// alocada de uma vez. A barra daria um salto e congelaria de novo — a mesma queixa, só que
/// num número mais alto.
///
/// O contador de E/S do processo não tem nenhum dos dois problemas: é monotônico por
/// construção, conta bytes de dados (não clusters), não depende de idioma e não muda com o
/// sistema de arquivos do destino.
/// </summary>
sealed class MedidorDeEscrita : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct ContadoresIo
    {
        public ulong LeiturasQtd, EscritasQtd, OutrasQtd;
        public ulong LidoBytes, EscritoBytes, OutrosBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetProcessIoCounters(IntPtr processo, out ContadoresIo contadores);

    volatile bool _parar;
    readonly Thread _thread;

    /// <param name="processo">O processo a observar. Já iniciado.</param>
    /// <param name="totalBytes">Quanto se espera que ele escreva. Zero desliga a medição.</param>
    /// <param name="de">Porcentagem no começo desta etapa.</param>
    /// <param name="ate">Porcentagem no fim. Nunca é atingida aqui — quem a reporta é quem terminou.</param>
    /// <param name="reportar">Recebe a porcentagem, só quando o inteiro muda.</param>
    public MedidorDeEscrita(Process processo, long totalBytes, int de, int ate, Action<int> reportar)
    {
        _thread = new Thread(() => Observar(processo, totalBytes, de, ate, reportar))
        {
            IsBackground = true,
            Name = "IsoForge.MedidorDeEscrita",
        };
        if (totalBytes > 0) _thread.Start();
    }

    void Observar(Process processo, long total, int de, int ate, Action<int> reportar)
    {
        // O corpo INTEIRO dentro de um try: exceção não tratada em thread de fundo derruba
        // o processo, e perder uma gravação de vinte minutos por causa do medidor seria o
        // pior desfecho possível. O medidor é conforto; a gravação é o trabalho.
        try
        {
            var ultimo = de;
            while (!_parar)
            {
                long escrito;
                try
                {
                    if (processo.HasExited) return;
                    if (!GetProcessIoCounters(processo.Handle, out var c)) { Dormir(1000); continue; }
                    escrito = (long)c.EscritoBytes;
                }
                catch { return; }   // processo já foi embora entre o HasExited e o Handle

                // Trava um ponto antes do fim: o passo só é dado por encerrado por quem o
                // executou, não por uma estimativa que chegou lá primeiro.
                var pct = de + (int)Math.Min(ate - de - 1, (ate - de) * escrito / Math.Max(1, total));
                if (pct > ultimo) { ultimo = pct; try { reportar(pct); } catch { } }

                Dormir(1000);
            }
        }
        catch { /* nunca derrubar o trabalho por causa da medição */ }
    }

    /// <summary>
    /// Dorme em fatias, conferindo a bandeira. Assim o Dispose não espera um segundo
    /// inteiro, e não é preciso um objeto cancelável que possa ser descartado embaixo
    /// desta thread.
    /// </summary>
    void Dormir(int ms)
    {
        for (var i = 0; i < ms / 100 && !_parar; i++) Thread.Sleep(100);
    }

    public void Dispose()
    {
        _parar = true;
        try { if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2)); } catch { }
    }
}
