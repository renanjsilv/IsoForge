using System.IO;
using System.Text.Json;
using IsoForge.Models;

namespace IsoForge.Core;

/// <summary>
/// Salva/lê a configuração do usuário em %APPDATA%\IsoForge\settings.json.
/// Assim os dados preenchidos (usuário, túneis VPN, unidades, caminhos, etc.) ficam
/// SEMPRE locais na máquina e sobrevivem a atualizações do programa — nada sensível
/// vai para o código/GitHub.
/// </summary>
public static class SettingsStore
{
    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IsoForge");

    public static string FilePath => Path.Combine(Dir, "settings.json");

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static void Save(BuildConfig c)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(c, Opts));
        }
        catch { /* melhor esforço: não travar o app por causa de settings */ }
    }

    public static BuildConfig? Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<BuildConfig>(File.ReadAllText(FilePath));
        }
        catch { /* arquivo corrompido/versão antiga: ignora e usa padrão */ }
        return null;
    }
}
