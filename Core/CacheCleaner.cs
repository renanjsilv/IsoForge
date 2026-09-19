using System.IO;

namespace IsoForge.Core;

/// <summary>
/// Limpa os arquivos temporários/cache do IsoForge ao fechar o app: os drivers baixados
/// (packs e avulsos, que podem ocupar GBs) e as pastas de trabalho das gerações. A ISO
/// gerada NÃO é tocada — ela é salva no caminho escolhido pelo usuário, fora dessas pastas.
/// Os instaladores de apps (Office, AnyDesk, etc.) são preservados para não precisar
/// rebaixar a cada uso.
/// </summary>
public static class CacheCleaner
{
    public static void Clean()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // Cache de drivers (Dell/Lenovo/HP): packs e avulsos baixados.
        DeleteDir(Path.Combine(local, "IsoForge", "Drivers"));
        // Pastas de trabalho da geração de ISO, teste no Sandbox, Golden e oscdimg extraído.
        DeleteDir(Path.Combine(Path.GetTempPath(), "IsoForge"));
    }

    static void DeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* melhor esforço: se algo estiver em uso, ignora */ }
    }
}
