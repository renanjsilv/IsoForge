using System.IO;

namespace IsoForge.Core;

/// <summary>
/// O pouco que o núcleo precisa saber sobre o sistema onde o IsoForge está rodando.
///
/// O IsoForge nasceu só para Windows e quase tudo aqui dentro é geração de texto — os arquivos
/// de resposta, os scripts, os XML —, que não depende de sistema nenhum. O que depende está
/// concentrado em meia dúzia de pontos (extrair a ISO, recompilá-la, cifrar a configuração), e
/// é esta classe que esses pontos consultam. Não há <c>#if</c> em lugar algum: o mesmo binário
/// serve aos dois lados, e a decisão é em tempo de execução.
/// </summary>
public static class Plataforma
{
    public static bool EhWindows => OperatingSystem.IsWindows();
    public static bool EhLinux => OperatingSystem.IsLinux();

    /// <summary>
    /// Caminho de um executável do PATH, ou null. É o <c>which</c>/<c>where</c> feito à mão
    /// porque chamar o de verdade custa um processo e, no Windows, uma janela.
    /// </summary>
    public static string? NoCaminho(params string[] nomes)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var nome in nomes)
            {
                try
                {
                    var completo = Path.Combine(dir.Trim(), nome);
                    if (File.Exists(completo)) return completo;
                    if (EhWindows && File.Exists(completo + ".exe")) return completo + ".exe";
                }
                catch { /* entrada inválida no PATH — segue */ }
            }
        }
        return null;
    }

    /// <summary>O primeiro dos caminhos absolutos que existir, ou null.</summary>
    public static string? PrimeiroQueExiste(params string[] caminhos)
    {
        foreach (var c in caminhos)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }
        return null;
    }
}
