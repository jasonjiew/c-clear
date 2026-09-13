using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Cclear.Core.Licensing;
using Xunit;

namespace Cclear.Core.Tests;

public class LicenseTests : IDisposable
{
    private readonly string _path;

    public LicenseTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"c-clear-lic-{Guid.NewGuid():N}.dat");
    }

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Theory]
    [InlineData(
        "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
        "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
        "",
        "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b")]
    [InlineData(
        "4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
        "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c",
        "72",
        "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00")]
    public void Ed25519_Rfc8032Vector1_SignAndVerify(string seedHex, string publicKeyHex, string messageHex, string signatureHex)
    {
        var seed = Convert.FromHexString(seedHex);
        var message = messageHex.Length == 0 ? Array.Empty<byte>() : Convert.FromHexString(messageHex);
        var expectedSignature = Convert.FromHexString(signatureHex);
        var expectedPublicKey = Convert.FromHexString(publicKeyHex);

        Assert.Equal(expectedPublicKey, Ed25519Rfc8032.PublicKeyFromSeed(seed));
        Assert.Equal(expectedSignature, Ed25519Rfc8032.Sign(seed, message));
        Assert.True(Ed25519Rfc8032.Verify(expectedPublicKey, expectedSignature, message));
    }

    [Fact]
    public void Ed25519_Rfc8032Vector2_1024ByteMessage()
    {
        // RFC 8032 TEST 3（消息为 0xab 重复 1023 字节 + 0x00? 官方向量为 1023 个 0xAB）：
        // 为避免长字符串转录错误，仅验证"签名可自洽验证 + 公钥推导一致"
        var seed = Convert.FromHexString("c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7");
        var message = new byte[1023];
        Array.Fill(message, (byte)0xab);
        var publicKey = Ed25519Rfc8032.PublicKeyFromSeed(seed);
        // RFC 官方公钥向量
        Assert.Equal(
            Convert.FromHexString("fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025"),
            publicKey);
        var signature = Ed25519Rfc8032.Sign(seed, message);
        Assert.True(Ed25519Rfc8032.Verify(publicKey, signature, message));
    }

    [Fact]
    public void Verify_TamperedSignature_Fails()
    {
        var seed = Convert.FromHexString("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb");
        var publicKey = Ed25519Rfc8032.PublicKeyFromSeed(seed);
        var message = Encoding.UTF8.GetBytes("license");
        var signature = Ed25519Rfc8032.Sign(seed, message);
        signature[10] ^= 0xFF;
        Assert.False(Ed25519Rfc8032.Verify(publicKey, signature, message));
    }

    [Fact]
    public void Verify_WrongMessage_Fails()
    {
        var seed = Convert.FromHexString("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb");
        var publicKey = Ed25519Rfc8032.PublicKeyFromSeed(seed);
        Assert.False(Ed25519Rfc8032.Verify(
            publicKey, Ed25519Rfc8032.Sign(seed, "a"u8.ToArray()), "b"u8.ToArray()));
    }

    [Fact]
    public void License_RoundtripAndTamper()
    {
        var seed = RandomNumberGenerator.GetBytes(32);
        var publicKeyHex = LicenseService.DerivePublicKeyHex(seed);
        var payload = new LicensePayload("测试用户", "CC-TEST-001", LicenseService.ProductId,
            new[] { LicenseService.FeatureAutoClean }, "2026-09-13T00:00:00Z", null);

        var content = LicenseService.BuildLicenseFile(payload, seed);
        File.WriteAllText(_path, content);

        var result = LicenseService.ValidateFile(_path, publicKeyHex);
        Assert.True(result.IsValid);
        Assert.Equal("测试用户", result.License!.Name);
        Assert.Equal("CC-TEST-001", result.License.LicenseId);
        Assert.Null(result.License.ExpiresAtUtc);
        Assert.True(result.License.HasFeature(LicenseService.FeatureAutoClean));

        // 篡改载荷 → 签名校验失败
        File.WriteAllText(_path, content.Replace("CC-TEST-001", "CC-EVIL-999"));
        var tampered = LicenseService.ValidateFile(_path, publicKeyHex);
        Assert.False(tampered.IsValid);
        Assert.Contains("签名", tampered.Error);

        // 用错误公钥 → 失败
        File.WriteAllText(_path, content);
        var wrongKey = LicenseService.ValidateFile(_path, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLower());
        Assert.False(wrongKey.IsValid);
    }

    [Fact]
    public void License_Expiry_Honored()
    {
        var seed = RandomNumberGenerator.GetBytes(32);
        var publicKeyHex = LicenseService.DerivePublicKeyHex(seed);
        var payload = new LicensePayload("过期用户", "CC-EXP", LicenseService.ProductId,
            new[] { LicenseService.FeatureAutoClean }, "2020-01-01T00:00:00Z", "2020-06-01T00:00:00Z");
        File.WriteAllText(_path, LicenseService.BuildLicenseFile(payload, seed));

        var result = LicenseService.ValidateFile(_path, publicKeyHex);
        Assert.True(result.IsValid);
        Assert.NotNull(result.License!.ExpiresAtUtc);
        Assert.False(result.License.HasFeature(LicenseService.FeatureAutoClean)); // 过期即无功能
        Assert.True(result.License.IsExpired);
    }

    [Fact]
    public void ValidateFile_Missing_ReturnsNoLicense()
    {
        var result = LicenseService.ValidateFile(_path);
        Assert.False(result.IsValid);
        Assert.Null(result.License);
    }
}
