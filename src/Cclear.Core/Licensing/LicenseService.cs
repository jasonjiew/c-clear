using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Cclear.Core.Licensing;

/// <summary>
/// 离线授权码机制（V2 F6，技术底座——本期不做功能墙、不接支付、无联网 DRM）。
/// license.dat = {"payload":{...},"signature":base64(Ed25519(canonical payload JSON))}；
/// 规范字节 = 载荷紧凑 JSON（UTF8），公钥内嵌、私钥永不出现在仓库中。
/// 合规红线：不降级现有免费功能、不加入推广弹窗。
/// </summary>
public static class LicenseService
{
    public const string ProductId = "c-clear-pro";

    /// <summary>Pro 功能标识（V3 P4-P8 起 HasFeature 生效；免费核心永不缩水）。</summary>
    public const string FeatureAutoClean = "autoclean";
    public const string FeatureCloudRulesFast = "cloud-rules-fast";
    public const string FeatureSmartAdvisor = "smart-advisor";
    public const string FeatureLeftoverScan = "leftover-scan";
    public const string FeatureTrendForecast = "trend-forecast";
    public const string FeatureAdvancedTriggers = "advanced-triggers";
    public const string FeatureDownloadDuplicates = "download-duplicates";

    public static string[] ProFeatures { get; } =
    {
        FeatureAutoClean, FeatureCloudRulesFast, FeatureSmartAdvisor, FeatureLeftoverScan,
        FeatureTrendForecast, FeatureAdvancedTriggers, FeatureDownloadDuplicates,
    };

    /// <summary>当前许可证是否启用指定功能（未装/无效/过期 = false）。</summary>
    public static bool HasFeature(string featureId)
    {
        var license = GetCurrent();
        return license is not null && license.HasFeature(featureId);
    }

    /// <summary>
    /// Ed25519 公钥（raw 32 字节，hex）。开发期密钥对由 tools/LicenseTool keygen 生成；
    /// 正式发布时由项目方重新生成并替换此处（私钥线下保管，不入库）。
    /// </summary>
    public const string PublicKeyHex =
        "25870ff0bca63197c6cbb58b16bd691ebb772e8cd2f122eff7721691e21f7d93";

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string LicensePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "C-Clear", "license.dat");

    /// <summary>生成 license.dat 内容（canonical payload 紧凑 JSON + Ed25519 签名）。</summary>
    public static string BuildLicenseFile(LicensePayload payload, byte[] privateKeySeed)
    {
        var payloadJson = JsonSerializer.Serialize(payload, CompactOptions);
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        var signature = Ed25519Rfc8032.Sign(privateKeySeed, payloadBytes);
        return JsonSerializer.Serialize(
            new LicenseFileDto(payload, Convert.ToBase64String(signature)), CompactOptions);
    }

    /// <summary>验证并解析许可证文件（publicKeyHex 为空时使用内嵌公钥）。</summary>
    public static LicenseValidationResult ValidateFile(string? path = null, string? publicKeyHex = null)
    {
        path ??= LicensePath;
        if (!File.Exists(path))
        {
            return LicenseValidationResult.NoLicense;
        }
        try
        {
            var dto = JsonSerializer.Deserialize<LicenseFileDto>(File.ReadAllText(path), CompactOptions);
            if (dto?.Payload is null || string.IsNullOrWhiteSpace(dto.Signature))
            {
                return new LicenseValidationResult(false, null, "许可证文件格式无效");
            }
            var payloadBytes = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(dto.Payload, CompactOptions));
            var publicKey = Convert.FromHexString((publicKeyHex ?? PublicKeyHex).Trim());
            var signature = Convert.FromBase64String(dto.Signature);
            if (!Ed25519Rfc8032.Verify(publicKey, signature, payloadBytes))
            {
                return new LicenseValidationResult(false, null, "签名校验失败（许可证被篡改或非本产品签发）");
            }
            if (dto.Payload.ProductId != ProductId)
            {
                return new LicenseValidationResult(false, null, $"产品不匹配：{dto.Payload.ProductId}");
            }
            var expires = string.IsNullOrEmpty(dto.Payload.ExpiresAtUtc)
                ? null
                : (DateTime?)DateTime.Parse(dto.Payload.ExpiresAtUtc, styles: System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
            return new LicenseValidationResult(true,
                new LicenseInfo(dto.Payload.Name, dto.Payload.LicenseId, dto.Payload.ProductId,
                    dto.Payload.Features, expires),
                null);
        }
        catch (Exception ex)
        {
            return new LicenseValidationResult(false, null, "许可证解析失败：" + ex.Message);
        }
    }

    /// <summary>校验并安装许可证到 %APPDATA%\C-Clear\license.dat（验证失败抛异常）。</summary>
    public static LicenseInfo Install(string sourceLicensePath, string? publicKeyHex = null)
    {
        var result = ValidateFile(sourceLicensePath, publicKeyHex);
        if (!result.IsValid || result.License is null)
        {
            throw new InvalidOperationException(result.Error ?? "许可证无效");
        }
        var target = LicensePath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(sourceLicensePath, target, overwrite: true);
        return result.License;
    }

    public static void RemoveLicense()
    {
        if (File.Exists(LicensePath))
        {
            File.Delete(LicensePath);
        }
    }

    /// <summary>读取当前许可证状态（未装/无效返回 null）。</summary>
    public static LicenseInfo? GetCurrent()
    {
        var result = ValidateFile();
        return result.IsValid ? result.License : null;
    }

    // Ed25519：.NET 8 无内置实现（.NET 10 才有），使用零依赖的 RFC 8032 精简实现
    //（见 Ed25519Rfc8032，正确性由 RFC 官方测试向量保证）。

    /// <summary>导出公钥 raw hex（由私钥种子推导）。</summary>
    public static string DerivePublicKeyHex(byte[] privateKeySeed)
    {
        return Convert.ToHexString(Ed25519Rfc8032.PublicKeyFromSeed(privateKeySeed)).ToLowerInvariant();
    }
}
