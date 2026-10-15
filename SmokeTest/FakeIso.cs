using System.Text;

namespace IsoForge.SmokeTest;

/// <summary>
/// Monta imagens ISO 9660 mínimas em memória para os testes: descritor primário de volume,
/// diretório raiz e, opcionalmente, um catálogo El Torito com imagens de boot BIOS e UEFI.
/// Permite testar a identificação da ISO e a extração das imagens de boot sem baixar
/// gigabytes de ISOs reais.
/// </summary>
public static class FakeIso
{
    const int Sector = 2048;

    /// <summary>ISO de dados: PVD com o rótulo informado e as entradas indicadas na raiz.</summary>
    public static byte[] Build(string label, params string[] rootEntries)
    {
        const int rootLba = 18;
        var iso = new byte[(rootLba + 1) * Sector];

        WritePvd(iso, label, rootLba);
        // Setor 17: terminador do conjunto de descritores.
        iso[17 * Sector] = 255;
        Encoding.ASCII.GetBytes("CD001").CopyTo(iso, 17 * Sector + 1);
        iso[17 * Sector + 6] = 1;

        WriteRootDirectory(iso, rootLba, rootEntries);
        return iso;
    }

    /// <summary>
    /// ISO bootável: além do PVD, traz o descritor de boot El Torito apontando para um catálogo
    /// com duas entradas — BIOS (4 setores virtuais) e UEFI (tamanho subdeclarado no catálogo,
    /// como acontece nas ISOs reais, com o tamanho verdadeiro no BPB do FAT).
    /// </summary>
    public static byte[] BuildBootable(string label)
    {
        const int catalogLba = 19;
        const int biosLba = 20;
        const int efiLba = 21;
        const int rootLba = 22;
        var iso = new byte[(rootLba + 1) * Sector];

        WritePvd(iso, label, rootLba);

        // Setor 17: descritor de boot (tipo 0) com a assinatura do El Torito.
        int br = 17 * Sector;
        iso[br] = 0;
        Encoding.ASCII.GetBytes("CD001").CopyTo(iso, br + 1);
        iso[br + 6] = 1;
        Encoding.ASCII.GetBytes("EL TORITO SPECIFICATION").CopyTo(iso, br + 7);
        BitConverter.GetBytes((uint)catalogLba).CopyTo(iso, br + 71);

        // Setor 18: terminador.
        iso[18 * Sector] = 255;
        Encoding.ASCII.GetBytes("CD001").CopyTo(iso, 18 * Sector + 1);

        // Setor 19: catálogo de boot.
        int cat = catalogLba * Sector;
        // Entrada de validação (plataforma 0x00 = 80x86).
        iso[cat] = 0x01;
        iso[cat + 1] = 0x00;
        iso[cat + 30] = 0x55;
        iso[cat + 31] = 0xAA;
        // Entrada padrão: BIOS, sem emulação, 4 setores virtuais de 512 bytes.
        iso[cat + 32] = 0x88;
        BitConverter.GetBytes((ushort)4).CopyTo(iso, cat + 32 + 6);
        BitConverter.GetBytes((uint)biosLba).CopyTo(iso, cat + 32 + 8);
        // Cabeçalho de seção final para a plataforma UEFI (0xEF), com 1 entrada.
        iso[cat + 64] = 0x91;
        iso[cat + 65] = 0xEF;
        BitConverter.GetBytes((ushort)1).CopyTo(iso, cat + 66);
        // Entrada UEFI: declara só 1 setor virtual (o tamanho real vem do FAT).
        iso[cat + 96] = 0x88;
        BitConverter.GetBytes((ushort)1).CopyTo(iso, cat + 96 + 6);
        BitConverter.GetBytes((uint)efiLba).CopyTo(iso, cat + 96 + 8);

        // Setor 20: imagem BIOS (conteúdo qualquer).
        for (int i = 0; i < 4 * 512; i++) iso[biosLba * Sector + i] = 0xB1;

        // Setor 21: imagem UEFI com um BPB de FAT válido — 8 setores de 512 bytes = 4096.
        int efi = efiLba * Sector;
        BitConverter.GetBytes((ushort)512).CopyTo(iso, efi + 11);  // bytes por setor
        BitConverter.GetBytes((ushort)8).CopyTo(iso, efi + 19);    // total de setores
        iso[efi + 510] = 0x55;
        iso[efi + 511] = 0xAA;

        WriteRootDirectory(iso, rootLba, Array.Empty<string>());
        return iso;
    }

    // ------------------------------------------------------------------
    static void WritePvd(byte[] iso, string label, int rootLba)
    {
        int pvd = 16 * Sector;
        iso[pvd] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(iso, pvd + 1);
        iso[pvd + 6] = 1;

        Pad(iso, pvd + 8, 32, "ISOFORGE-TESTE");   // identificador do sistema
        Pad(iso, pvd + 40, 32, label);             // identificador do volume
        Pad(iso, pvd + 318, 128, "");              // editor
        Pad(iso, pvd + 574, 128, "");              // aplicativo

        // Registro do diretório raiz embutido no PVD (34 bytes a partir do offset 156).
        var root = DirRecord("\0", (uint)rootLba, Sector, isDir: true);
        root.CopyTo(iso, pvd + 156);
    }

    static void WriteRootDirectory(byte[] iso, int rootLba, string[] entries)
    {
        int pos = rootLba * Sector;
        void Add(byte[] rec) { rec.CopyTo(iso, pos); pos += rec.Length; }

        Add(DirRecord("\0", (uint)rootLba, Sector, true));      // "."
        Add(DirRecord("", (uint)rootLba, Sector, true));  // ".."
        foreach (var name in entries)
            Add(DirRecord(name, (uint)(rootLba + 1), Sector, !name.Contains('.')));
    }

    /// <summary>Registro de diretório ISO 9660 (comprimento par, extensão e tamanho em both-endian).</summary>
    static byte[] DirRecord(string name, uint extent, uint size, bool isDir)
    {
        int nameLen = name.Length;
        int len = 33 + nameLen;
        if (len % 2 != 0) len++;

        var rec = new byte[len];
        rec[0] = (byte)len;
        WriteBothEndian(rec, 2, extent);
        WriteBothEndian(rec, 10, size);
        rec[25] = (byte)(isDir ? 0x02 : 0x00);
        rec[32] = (byte)nameLen;
        Encoding.ASCII.GetBytes(name).CopyTo(rec, 33);
        return rec;
    }

    /// <summary>Grava o valor em little-endian e, na sequência, em big-endian (formato do ISO 9660).</summary>
    static void WriteBothEndian(byte[] buffer, int offset, uint value)
    {
        BitConverter.GetBytes(value).CopyTo(buffer, offset);
        var be = BitConverter.GetBytes(value);
        Array.Reverse(be);
        be.CopyTo(buffer, offset + 4);
    }

    static void Pad(byte[] buffer, int offset, int length, string text)
    {
        for (int i = 0; i < length; i++) buffer[offset + i] = (byte)' ';
        var bytes = Encoding.ASCII.GetBytes(text.Length > length ? text[..length] : text);
        bytes.CopyTo(buffer, offset);
    }
}
