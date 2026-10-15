using System.IO;

namespace IsoForge.Core;

/// <summary>
/// Limpa os arquivos temporários/cache do IsoForge ao fechar o app: os drivers baixados
/// (packs e avulsos, que podem ocupar GBs) e as pastas de trabalho das gerações. A ISO
/// gerada NÃO é tocada — ela é salva no caminho escolhido pelo usuário, fora dessas pastas.
/// Os instaladores de apps (Office, AnyDesk, etc.) são preservados para não precisar
/// rebaixar a cada uso.
///
/// Os drivers que a configuração salva (ou um perfil) USA também ficam. Antes a pasta
/// Drivers ia inteira, a configuração continuava lembrando o modelo e o caminho, e na
/// próxima geração o caminho apontava para o vazio: a mídia saía sem drivers, sem aviso.
/// </summary>
public static class CacheCleaner
{
    public static string DriversRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IsoForge", "Drivers");

    /// <param name="manterDrivers">Pastas de drivers em uso, que sobrevivem à limpeza.</param>
    public static void Clean(IEnumerable<string?>? manterDrivers = null)
    {
        // Cache de drivers (Dell/Lenovo/HP): packs e avulsos baixados, menos os em uso.
        LimparMenos(DriversRoot, manterDrivers);
        // Pastas de trabalho da geração de ISO, teste no Sandbox, Golden e oscdimg extraído.
        DeleteDir(Path.Combine(Path.GetTempPath(), "IsoForge"));
    }

    /// <summary>
    /// Apaga <paramref name="raiz"/> inteira, menos as pastas de <paramref name="manter"/> que
    /// estão dentro dela (e as pastas que levam até elas). Caminho fora da raiz é ignorado:
    /// esta função só decide o que NÃO apagar, nunca amplia o que apaga.
    /// </summary>
    internal static void LimparMenos(string raiz, IEnumerable<string?>? manter)
    {
        if (!Directory.Exists(raiz)) return;
        var r = Normalizar(raiz);
        var alvos = (manter ?? Array.Empty<string?>())
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => Normalizar(m!))
            .Where(m => m.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(m, r, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (alvos.Count == 0) { DeleteDir(raiz); return; }
        if (alvos.Any(a => string.Equals(a, r, StringComparison.OrdinalIgnoreCase))) return;
        Podar(raiz, alvos);
    }

    static void Podar(string pasta, List<string> alvos)
    {
        IEnumerable<string> entradas;
        try { entradas = Directory.EnumerateFileSystemEntries(pasta).ToList(); }
        catch { return; }

        foreach (var entrada in entradas)
        {
            var e = Normalizar(entrada);
            if (alvos.Any(a => string.Equals(a, e, StringComparison.OrdinalIgnoreCase)))
                continue;                                   // a pasta em uso: fica inteira
            if (alvos.Any(a => a.StartsWith(e + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                Podar(entrada, alvos);                       // caminho até ela: desce
                continue;
            }
            if (Directory.Exists(entrada)) DeleteDir(entrada);
            else { try { File.Delete(entrada); } catch { } }
        }
    }

    static string Normalizar(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
    }

    static void DeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* melhor esforço: se algo estiver em uso, ignora */ }
    }
}
