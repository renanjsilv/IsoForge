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
    /// <summary>
    /// Pasta da configuração. Normalmente %APPDATA%\IsoForge no Windows e ~/.config/IsoForge
    /// no Linux; a variável ISOFORGE_CONFIG desvia para outra pasta, o que permite manter
    /// perfis separados na mesma máquina e gerar as capturas de tela do projeto sem tocar na
    /// configuração de quem está usando o programa. A CHAVE continua no perfil do usuário,
    /// de propósito: uma configuração levada para outra máquina não abre lá, e é isso mesmo
    /// que se espera de um arquivo que guarda senhas.
    /// </summary>
    static string Dir
    {
        get
        {
            var escolhida = Environment.GetEnvironmentVariable("ISOFORGE_CONFIG");
            return string.IsNullOrWhiteSpace(escolhida)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IsoForge")
                : escolhida;
        }
    }

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
            var enc = Cofre.Cifrar(json);
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
                    Cofre.Decifrar(File.ReadAllBytes(FilePath)));
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
            File.WriteAllBytes(ProfilePath(name), Cofre.Cifrar(json));
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
                    Cofre.Decifrar(File.ReadAllBytes(p)));
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
