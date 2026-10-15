using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace IsoForge.Core;

/// <summary>
/// Cifra e decifra a configuração local (usuário, senhas, túneis VPN, unidades).
///
/// No Windows é a DPAPI, como sempre foi: a chave é derivada da conta do Windows e fica com o
/// sistema operacional — copiar o settings.dat para outra máquina não adianta nada.
///
/// No Linux não existe DPAPI. A chave vai para um arquivo com permissão 0600 ao lado da
/// configuração, e os dados são cifrados com AES-GCM. Vale dizer sem rodeio o que isso protege:
/// impede que um backup, um compartilhamento ou outro usuário da máquina leiam o arquivo; NÃO
/// protege contra quem já entrou como você, porque a chave está na sua própria pasta — é a
/// mesma garantia que um chaveiro sem senha mestra dá.
/// </summary>
public static class Cofre
{
    /// <summary>Formato do arquivo no Linux: marca + nonce (12) + tag (16) + texto cifrado.</summary>
    static readonly byte[] Marca = { (byte)'I', (byte)'F', (byte)'C', 1 };

    public static byte[] Cifrar(byte[] dados)
    {
        if (OperatingSystem.IsWindows())
            return ProtectedData.Protect(dados, null, DataProtectionScope.CurrentUser);

        var chave = ChaveLocal();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cifrado = new byte[dados.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(chave, tag.Length))
            aes.Encrypt(nonce, dados, cifrado, tag);

        var saida = new byte[Marca.Length + nonce.Length + tag.Length + cifrado.Length];
        Buffer.BlockCopy(Marca, 0, saida, 0, Marca.Length);
        Buffer.BlockCopy(nonce, 0, saida, Marca.Length, nonce.Length);
        Buffer.BlockCopy(tag, 0, saida, Marca.Length + nonce.Length, tag.Length);
        Buffer.BlockCopy(cifrado, 0, saida, Marca.Length + nonce.Length + tag.Length, cifrado.Length);
        return saida;
    }

    public static byte[] Decifrar(byte[] dados)
    {
        if (OperatingSystem.IsWindows())
            return ProtectedData.Unprotect(dados, null, DataProtectionScope.CurrentUser);

        if (dados.Length < Marca.Length + 12 + 16)
            throw new CryptographicException("Arquivo de configuração truncado.");
        for (var i = 0; i < Marca.Length; i++)
            if (dados[i] != Marca[i]) throw new CryptographicException("Arquivo de configuração de outro formato.");

        var nonce = new byte[12];
        var tag = new byte[16];
        Buffer.BlockCopy(dados, Marca.Length, nonce, 0, nonce.Length);
        Buffer.BlockCopy(dados, Marca.Length + nonce.Length, tag, 0, tag.Length);
        var cifrado = new byte[dados.Length - Marca.Length - nonce.Length - tag.Length];
        Buffer.BlockCopy(dados, Marca.Length + nonce.Length + tag.Length, cifrado, 0, cifrado.Length);

        var claro = new byte[cifrado.Length];
        using (var aes = new AesGcm(ChaveLocal(), tag.Length))
            aes.Decrypt(nonce, cifrado, tag, claro);
        return claro;
    }

    // ------------------------------------------------------------------

    static string CaminhoDaChave => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IsoForge", "chave.bin");

    /// <summary>Lê a chave local, criando-a na primeira vez. Sempre com permissão 0600.</summary>
    static byte[] ChaveLocal()
    {
        var caminho = CaminhoDaChave;
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);

        if (File.Exists(caminho))
        {
            var lida = File.ReadAllBytes(caminho);
            if (lida.Length == 32) { Trancar(caminho); return lida; }
            // Tamanho errado: alguém mexeu. Gerar outra apagaria a configuração em silêncio.
            throw new CryptographicException($"A chave em {caminho} está corrompida (tamanho inesperado).");
        }

        var nova = RandomNumberGenerator.GetBytes(32);
        // Cria o arquivo VAZIO e tranca ANTES de escrever a chave: criar já com o conteúdo
        // deixaria a chave legível por todos durante a fração de segundo até o chmod.
        using (File.Create(caminho)) { }
        Trancar(caminho);
        File.WriteAllBytes(caminho, nova);
        return nova;
    }

    static void Trancar(string caminho)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(caminho, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { /* sistema de arquivos sem permissões POSIX (ex.: pendrive FAT) */ }
    }
}
