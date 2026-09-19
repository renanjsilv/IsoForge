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
                return JsonSerializer.Deserialize<BuildConfig>(
                    ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser));
            // Migração do formato antigo (texto puro): lê e o próximo Save já grava cifrado.
            if (File.Exists(LegacyJsonPath))
                return JsonSerializer.Deserialize<BuildConfig>(File.ReadAllText(LegacyJsonPath));
        }
        catch { /* arquivo corrompido / de outra máquina / versão antiga: usa padrão */ }
        return null;
    }

    // ------------------------------------------------------------------
    // Perfis nomeados (ex.: "Matriz", "Cliente X") — cada um cifrado em profiles\<nome>.dat
    // ------------------------------------------------------------------
    static string ProfilesDir => Path.Combine(Dir, "profiles");
    static string ProfilePath(string name) => Path.Combine(ProfilesDir, Sanitize(name) + ".dat");
    static string Sanitize(string name) => string.Join("_", name.Trim().Split(Path.GetInvalidFileNameChars()));

    public static string[] ListProfiles()
    {
        try
        {
            if (Directory.Exists(ProfilesDir))
                return Directory.GetFiles(ProfilesDir, "*.dat")
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToArray()!;
        }
        catch { }
        return Array.Empty<string>();
    }

    public static void SaveProfile(string name, BuildConfig c)
    {
        try
        {
            Directory.CreateDirectory(ProfilesDir);
            var json = JsonSerializer.SerializeToUtf8Bytes(c, Opts);
            File.WriteAllBytes(ProfilePath(name), ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
        }
        catch { }
    }

    public static BuildConfig? LoadProfile(string name)
    {
        try
        {
            var p = ProfilePath(name);
            if (File.Exists(p))
                return JsonSerializer.Deserialize<BuildConfig>(
                    ProtectedData.Unprotect(File.ReadAllBytes(p), null, DataProtectionScope.CurrentUser));
        }
        catch { }
        return null;
    }

    public static void DeleteProfile(string name)
    {
        try { var p = ProfilePath(name); if (File.Exists(p)) File.Delete(p); }
        catch { }
    }
}
