using System.IO;
using System.Text;

namespace IsoForge.Core.Linux;

/// <summary>Uma imagem de boot encontrada no catálogo El Torito de uma ISO.</summary>
/// <param name="IsEfi">true = entrada UEFI (plataforma 0xEF); false = BIOS/x86 (plataforma 0x00).</param>
/// <param name="Lba">Setor (2048 bytes) onde a imagem começa.</param>
/// <param name="Length">Tamanho da imagem em bytes.</param>
public record BootImage(bool IsEfi, uint Lba, long Length);

/// <summary>
/// Lê o catálogo El Torito de uma ISO e extrai as imagens de boot (BIOS e UEFI).
///
/// Nas ISOs do Linux a imagem de boot UEFI quase sempre existe SÓ dentro do catálogo El Torito —
/// não como arquivo no sistema de arquivos. Sem extraí-la é impossível reconstruir uma ISO
/// bootável com o oscdimg, que exige a imagem em disco.
/// </summary>
public static class ElTorito
{
    const int SectorSize = 2048;
    const long BootRecordOffset = 17 * SectorSize; // descritor de boot (setor 17)

    /// <summary>Imagens de boot declaradas na ISO. Lista vazia se a ISO não for bootável.</summary>
    public static List<BootImage> Read(string isoPath)
    {
        var images = new List<BootImage>();
        try
        {
            using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < BootRecordOffset + SectorSize) return images;

            var br = ReadSector(fs, BootRecordOffset);
            // Descritor de boot: tipo 0, "CD001", identificador "EL TORITO SPECIFICATION".
            if (br[0] != 0 || Encoding.ASCII.GetString(br, 1, 5) != "CD001") return images;
            if (!Encoding.ASCII.GetString(br, 7, 23).StartsWith("EL TORITO", StringComparison.Ordinal)) return images;

            uint catalogLba = BitConverter.ToUInt32(br, 71);
            if (catalogLba == 0 || (long)catalogLba * SectorSize + SectorSize > fs.Length) return images;

            var catalog = ReadSector(fs, (long)catalogLba * SectorSize);

            // Entrada 0: validação (header 0x01, plataforma em [1], assinatura 0x55AA em [30..31]).
            if (catalog[0] != 0x01 || catalog[30] != 0x55 || catalog[31] != 0xAA) return images;
            byte platform = catalog[1];

            // Entrada 1: entrada inicial/padrão (indicador 0x88 = bootável).
            AddEntry(fs, images, catalog, 32, platform);

            // Entradas seguintes: cabeçalhos de seção (0x90 continua, 0x91 é a última).
            int pos = 64;
            while (pos + 32 <= catalog.Length)
            {
                byte headerId = catalog[pos];
                if (headerId is 0x90 or 0x91)
                {
                    byte sectionPlatform = catalog[pos + 1];
                    int count = BitConverter.ToUInt16(catalog, pos + 2);
                    pos += 32;
                    for (int i = 0; i < count && pos + 32 <= catalog.Length; i++, pos += 32)
                        AddEntry(fs, images, catalog, pos, sectionPlatform);
                    if (headerId == 0x91) break;
                }
                else break;
            }
        }
        catch { /* ISO sem El Torito legível: o chamador cai no caminho alternativo */ }
        return images;
    }

    /// <summary>
    /// Extrai as imagens de boot para <paramref name="destFolder"/> e devolve os caminhos
    /// (BIOS e/ou UEFI). Arquivos ausentes voltam como null.
    /// </summary>
    public static (string? Bios, string? Efi) Extract(string isoPath, string destFolder)
    {
        var images = Read(isoPath);
        if (images.Count == 0) return (null, null);

        Directory.CreateDirectory(destFolder);
        string? bios = null, efi = null;

        using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        foreach (var img in images)
        {
            // Só a primeira imagem de cada plataforma interessa (é a que o firmware usa).
            if (img.IsEfi && efi != null) continue;
            if (!img.IsEfi && bios != null) continue;

            var name = img.IsEfi ? "eltorito-efi.img" : "eltorito-bios.img";
            var dest = Path.Combine(destFolder, name);
            if (!Dump(fs, img, dest)) continue;

            if (img.IsEfi) efi = dest; else bios = dest;
        }
        return (bios, efi);
    }

    // ------------------------------------------------------------------
    static void AddEntry(FileStream fs, List<BootImage> images, byte[] catalog, int offset, byte platform)
    {
        if (offset + 32 > catalog.Length) return;
        if (catalog[offset] != 0x88) return; // não bootável

        uint lba = BitConverter.ToUInt32(catalog, offset + 8);
        int virtualSectors = BitConverter.ToUInt16(catalog, offset + 6);
        if (lba == 0) return;

        long length = (long)virtualSectors * 512;
        bool isEfi = platform == 0xEF;

        // O contador de setores da entrada UEFI costuma vir subdimensionado: o tamanho real
        // está no BPB do sistema FAT da própria imagem.
        if (isEfi)
        {
            var fat = FatImageLength(fs, (long)lba * SectorSize);
            if (fat > length) length = fat;
        }

        if (length <= 0) return;
        if ((long)lba * SectorSize + length > fs.Length) length = fs.Length - (long)lba * SectorSize;
        if (length <= 0) return;

        images.Add(new BootImage(isEfi, lba, length));
    }

    /// <summary>Tamanho real de uma imagem FAT lendo o BPB (0 se não parecer FAT).</summary>
    static long FatImageLength(FileStream fs, long offset)
    {
        try
        {
            var boot = new byte[512];
            fs.Seek(offset, SeekOrigin.Begin);
            if (fs.Read(boot, 0, 512) != 512) return 0;
            // Assinatura 0x55AA no fim do setor de boot.
            if (boot[510] != 0x55 || boot[511] != 0xAA) return 0;

            int bytesPerSector = BitConverter.ToUInt16(boot, 11);
            if (bytesPerSector is not (512 or 1024 or 2048 or 4096)) return 0;

            long totalSectors = BitConverter.ToUInt16(boot, 19);
            if (totalSectors == 0) totalSectors = BitConverter.ToUInt32(boot, 32);
            if (totalSectors <= 0) return 0;

            return totalSectors * bytesPerSector;
        }
        catch { return 0; }
    }

    static bool Dump(FileStream fs, BootImage img, string dest)
    {
        try
        {
            fs.Seek((long)img.Lba * SectorSize, SeekOrigin.Begin);
            using var outFs = File.Create(dest);
            var buffer = new byte[64 * 1024];
            long remaining = img.Length;
            while (remaining > 0)
            {
                int want = (int)Math.Min(buffer.Length, remaining);
                int n = fs.Read(buffer, 0, want);
                if (n <= 0) break;
                outFs.Write(buffer, 0, n);
                remaining -= n;
            }
            return remaining == 0;
        }
        catch { return false; }
    }

    static byte[] ReadSector(FileStream fs, long offset)
    {
        var buf = new byte[SectorSize];
        fs.Seek(offset, SeekOrigin.Begin);
        int read = 0;
        while (read < SectorSize)
        {
            int n = fs.Read(buf, read, SectorSize - read);
            if (n <= 0) break;
            read += n;
        }
        return buf;
    }
}
