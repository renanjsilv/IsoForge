using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using IsoForge.Models;

namespace IsoForge.Core;

/// <summary>
/// Salva/lê a configuração do usuário em %APPDATA%\IsoForge\settings.dat, CIFRADA com DPAPI
/// (escopo do usuário atual). Os dados preenchidos (usuário, senha, túneis VPN, PSK, unidades,
/// caminhos...) ficam SEMPRE locais e cifrados — atados a este usuário/máquina —, sobrevivem a
/// atualizações e nada sensível vai para o código/GitHub.
/// </summary>
public static class SettingsStore
{
    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IsoForge");

    // Formato atual (cifrado) e o antigo (texto puro), para migração.
    public static string FilePath => Path.Combine(Dir, "settings.dat");
    static string LegacyJsonPath => Path.Combine(Dir, "settings.json");

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static void Save(BuildConfig c)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.SerializeToUtf8Bytes(c, Opts);
            var enc = ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, enc);
            // Remove qualquer resquício em texto puro de versões anteriores.
            if (File.Exists(LegacyJsonPath)) File.Delete(LegacyJsonPath);
        }
        catch { /* melhor esforço: não travar o app por causa de settings */ }
    }

    public static BuildConfig? Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var enc = File.ReadAllBytes(FilePath);
                var json = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<BuildConfig>(json);
            }
            // Migração do formato antigo (texto puro): lê e o próximo Save já grava cifrado.
            if (File.Exists(LegacyJsonPath))
                return JsonSerializer.Deserialize<BuildConfig>(File.ReadAllText(LegacyJsonPath));
        }
        catch { /* arquivo corrompido / de outra máquina / versão antiga: usa padrão */ }
        return null;
    }
}
