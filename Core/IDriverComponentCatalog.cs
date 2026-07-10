namespace IsoForge.Core;

/// <summary>Um driver individual (pacote avulso) aplicável a um modelo, de qualquer fabricante.</summary>
public record DriverComponent(string Name, string Category, string Url, string HashMd5, long SizeBytes)
{
    public string SizeText => SizeBytes >= 1024L * 1024 ? $"{SizeBytes / 1024.0 / 1024.0:0} MB" : $"{SizeBytes / 1024.0:0} KB";
}

/// <summary>Referência a um modelo (nome + id do sistema/MTM) para filtrar componentes.</summary>
public record DriverModelRef(string Name, string SystemId);

/// <summary>
/// Catálogo de drivers INDIVIDUAIS por modelo (Dell, Lenovo, …): lista os drivers avulsos do
/// modelo e baixa/extrai só os escolhidos, para injeção na ISO.
/// </summary>
public interface IDriverComponentCatalog
{
    Task EnsureLoadedAsync(IProgress<string> log, CancellationToken ct);
    List<DriverModelRef> Models();
    Task<List<DriverComponent>> DriversForModelAsync(DriverModelRef model, IProgress<string> log, CancellationToken ct);
    Task<string> DownloadAndExtractAsync(IReadOnlyList<DriverComponent> selected, string modelLabel,
        IProgress<string> log, IProgress<double>? pct, CancellationToken ct);
}
