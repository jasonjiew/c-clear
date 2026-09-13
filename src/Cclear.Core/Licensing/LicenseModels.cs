using System;
using System.Text.Json.Serialization;

namespace Cclear.Core.Licensing;

/// <summary>许可证载荷（签名与验证的规范字节即此对象的紧凑 JSON）。</summary>
public sealed record LicensePayload(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("licenseId")] string LicenseId,
    [property: JsonPropertyName("productId")] string ProductId,
    [property: JsonPropertyName("features")] string[] Features,
    [property: JsonPropertyName("issuedAtUtc")] string IssuedAtUtc,
    [property: JsonPropertyName("expiresAtUtc")] string? ExpiresAtUtc);

/// <summary>已验证的许可证信息。</summary>
public sealed record LicenseInfo(
    string Name,
    string LicenseId,
    string ProductId,
    string[] Features,
    DateTime? ExpiresAtUtc)
{
    public bool IsExpired => ExpiresAtUtc is not null && ExpiresAtUtc.Value < DateTime.UtcNow;

    public bool HasFeature(string feature)
    {
        return !IsExpired && Array.IndexOf(Features, feature) >= 0;
    }
}

/// <summary>许可证验证结果。</summary>
public sealed record LicenseValidationResult(bool IsValid, LicenseInfo? License, string? Error)
{
    public static readonly LicenseValidationResult NoLicense = new(false, null, "未安装许可证");
}

/// <summary>license.dat 磁盘格式（载荷 + Ed25519 签名）。</summary>
public sealed record LicenseFileDto(
    [property: JsonPropertyName("payload")] LicensePayload Payload,
    [property: JsonPropertyName("signature")] string Signature);
