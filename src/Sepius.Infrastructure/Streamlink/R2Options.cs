namespace Sepius.Infrastructure.Streamlink;

public sealed class R2Options
{
    public const string SectionName = "CloudflareR2";

    /// <summary>Cloudflare Account ID.</summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>R2 Access Key ID (desde Manage R2 API Tokens).</summary>
    public string AccessKeyId { get; set; } = string.Empty;

    /// <summary>R2 Secret Access Key.</summary>
    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>Nombre del bucket R2.</summary>
    public string BucketName { get; set; } = "sepius-db";

    /// <summary>Public base URL del bucket (ej: https://pub-xxxxx.r2.dev).</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>Si está habilitado, los HLS se sirven desde R2.</summary>
    public bool Enabled => !string.IsNullOrEmpty(AccessKeyId) && !string.IsNullOrEmpty(SecretAccessKey);

    /// <summary>Endpoint S3 de R2.</summary>
    public string EndpointUrl => $"https://{AccountId}.r2.cloudflarestorage.com";
}
