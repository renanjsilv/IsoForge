using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace IsoForge.Core.Linux;

/// <summary>
/// Implementa o <c>SHA-512 crypt</c> (formato <c>$6$salt$hash</c>) usado em /etc/shadow por
/// praticamente toda distro Linux. Os arquivos de resposta (autoinstall, preseed, kickstart,
/// AutoYaST) recebem a senha já cifrada — nunca em texto puro.
///
/// Especificação de Ulrich Drepper (https://www.akkadia.org/drepper/SHA-crypt.txt); o vetor de
/// teste oficial é validado pelo SmokeTest.
/// </summary>
public static class UnixCrypt
{
    const string B64 = "./0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    const int DefaultRounds = 5000;
    const int SaltLength = 16;

    /// <summary>Gera um sal aleatório válido (16 caracteres do alfabeto do crypt).</summary>
    public static string NewSalt()
    {
        var bytes = RandomNumberGenerator.GetBytes(SaltLength);
        var sb = new StringBuilder(SaltLength);
        foreach (var b in bytes) sb.Append(B64[b % B64.Length]);
        return sb.ToString();
    }

    /// <summary>Cifra a senha em SHA-512 crypt com sal aleatório. Senha vazia devolve "" (sem senha).</summary>
    public static string Sha512(string password)
        => string.IsNullOrEmpty(password) ? "" : Sha512(password, NewSalt());

    /// <summary>Cifra a senha com um sal específico (usado nos testes com vetor conhecido).</summary>
    public static string Sha512(string password, string salt, int rounds = DefaultRounds)
    {
        var pw = Encoding.UTF8.GetBytes(password);

        // O sal aceito é de no máximo 16 caracteres; um prefixo "rounds=N$" opcional o antecede.
        bool customRounds = false;
        if (salt.StartsWith("rounds=", StringComparison.Ordinal))
        {
            var end = salt.IndexOf('$');
            if (end > 0 && int.TryParse(salt.AsSpan(7, end - 7), out var r))
            {
                rounds = Math.Clamp(r, 1000, 999_999_999);
                salt = salt[(end + 1)..];
                customRounds = true;
            }
        }
        if (salt.Length > SaltLength) salt = salt[..SaltLength];
        var saltBytes = Encoding.UTF8.GetBytes(salt);

        // Passo 4-8: B = SHA512(senha + sal + senha)
        var b = Hash(pw, saltBytes, pw);

        // Passo 9-12: contexto A = senha + sal + (len(senha) bytes de B)
        var a = new MemoryStream();
        a.Write(pw); a.Write(saltBytes);
        AddRepeated(a, b, pw.Length);

        // Passo 13: para cada bit de len(senha), do menos significativo ao mais alto 1:
        // bit 1 -> adiciona B; bit 0 -> adiciona a senha.
        for (int i = pw.Length; i > 0; i >>= 1)
        {
            if ((i & 1) != 0) a.Write(b);
            else a.Write(pw);
        }
        var aDigest = Sha512Of(a.ToArray());

        // Passo 15-16: DP = SHA512(senha repetida len(senha) vezes); P = DP repetido ate len(senha).
        var dp = new MemoryStream();
        for (int i = 0; i < pw.Length; i++) dp.Write(pw);
        var p = Sequence(Sha512Of(dp.ToArray()), pw.Length);

        // Passo 17-19: DS = SHA512(sal repetido 16 + A[0] vezes); S = DS repetido ate len(sal).
        var ds = new MemoryStream();
        int saltRepeats = 16 + aDigest[0];
        for (int i = 0; i < saltRepeats; i++) ds.Write(saltBytes);
        var s = Sequence(Sha512Of(ds.ToArray()), saltBytes.Length);

        // Passo 21: as rodadas de alongamento.
        var c = aDigest;
        for (int i = 0; i < rounds; i++)
        {
            var ms = new MemoryStream();
            if ((i & 1) != 0) ms.Write(p); else ms.Write(c);
            if (i % 3 != 0) ms.Write(s);
            if (i % 7 != 0) ms.Write(p);
            if ((i & 1) != 0) ms.Write(c); else ms.Write(p);
            c = Sha512Of(ms.ToArray());
        }

        var prefix = customRounds || rounds != DefaultRounds ? $"$6$rounds={rounds}${salt}$" : $"$6${salt}$";
        return prefix + Encode(c);
    }

    // ------------------------------------------------------------------
    static byte[] Sha512Of(byte[] data) => SHA512.HashData(data);

    static byte[] Hash(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var part in parts) ms.Write(part);
        return Sha512Of(ms.ToArray());
    }

    /// <summary>Escreve <paramref name="count"/> bytes de <paramref name="source"/>, repetindo-o se preciso.</summary>
    static void AddRepeated(MemoryStream ms, byte[] source, int count)
    {
        int written = 0;
        while (written + source.Length <= count) { ms.Write(source); written += source.Length; }
        ms.Write(source, 0, count - written);
    }

    /// <summary>Sequência de <paramref name="count"/> bytes formada repetindo o digest.</summary>
    static byte[] Sequence(byte[] digest, int count)
    {
        var ms = new MemoryStream();
        AddRepeated(ms, digest, count);
        return ms.ToArray();
    }

    /// <summary>
    /// Codificação base64 do crypt: grupos de 3 bytes em ordem embaralhada específica do SHA-512,
    /// 6 bits por caractere, do menos significativo para o mais significativo.
    /// </summary>
    static string Encode(byte[] h)
    {
        var sb = new StringBuilder(86);
        void Group(int b2, int b1, int b0, int chars)
        {
            uint w = ((uint)h[b2] << 16) | ((uint)h[b1] << 8) | h[b0];
            for (int i = 0; i < chars; i++) { sb.Append(B64[(int)(w & 0x3f)]); w >>= 6; }
        }

        Group(0, 21, 42, 4); Group(22, 43, 1, 4); Group(44, 2, 23, 4); Group(3, 24, 45, 4);
        Group(25, 46, 4, 4); Group(47, 5, 26, 4); Group(6, 27, 48, 4); Group(28, 49, 7, 4);
        Group(50, 8, 29, 4); Group(9, 30, 51, 4); Group(31, 52, 10, 4); Group(53, 11, 32, 4);
        Group(12, 33, 54, 4); Group(34, 55, 13, 4); Group(56, 14, 35, 4); Group(15, 36, 57, 4);
        Group(37, 58, 16, 4); Group(59, 17, 38, 4); Group(18, 39, 60, 4); Group(40, 61, 19, 4);
        Group(62, 20, 41, 4);

        // Último grupo: só o byte 63, em 2 caracteres.
        uint last = h[63];
        for (int i = 0; i < 2; i++) { sb.Append(B64[(int)(last & 0x3f)]); last >>= 6; }
        return sb.ToString();
    }
}
