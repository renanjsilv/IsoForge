namespace IsoForge.Core;

/// <summary>Um driver pack de um modelo (Windows 11 x64), de qualquer fabricante.</summary>
public record DriverPackModel(string Label, string Url, string HashMd5, long SizeBytes)
{
    public string SizeText => SizeBytes > 0 ? $"{SizeBytes / 1024.0 / 1024.0:0} MB" : "";
}

/// <summary>
/// Catálogo de driver packs por modelo (Dell, Lenovo, …). Cada fabricante implementa a busca
/// dos modelos e o download+extração do pack para uma pasta com os .inf (para injeção na ISO).
/// </summary>
public interface IDriverPackCatalog
{
    Task<List<DriverPackModel>> FetchModelsAsync(IProgress<string> log, CancellationToken ct);
    Task<string> DownloadAndExtractAsync(DriverPackModel model, IProgress<string> log, IProgress<double>? pct, CancellationToken ct);
}
