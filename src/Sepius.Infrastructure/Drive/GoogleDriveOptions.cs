namespace Sepius.Infrastructure.Drive;

public sealed class GoogleDriveOptions
{
    public const string SectionName = "GoogleDrive";

    /// <summary>Si es false, el pipeline se salta Drive y sube directo a YouTube.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Si se deja vacío se reutiliza el ClientId de YouTube (mismo proyecto de Google Cloud).</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Si se deja vacío se reutiliza el ClientSecret de YouTube.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>Refresh token con el scope drive.file (se obtiene con scripts/get-google-drive-token.ps1).</summary>
    public string RefreshToken { get; set; } = "";

    /// <summary>Carpeta raíz en Mi unidad. Dentro se crea una subcarpeta por canal.</summary>
    public string RootFolderName { get; set; } = "Sepius";
}
