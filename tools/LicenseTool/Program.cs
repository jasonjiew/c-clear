using System.Security.Cryptography;
using System.Text.Json;
using Cclear.Core.Licensing;

// 授权码签发工具（V2 F6）：
//   keygen <私钥文件>                 生成 Ed25519 密钥对：私钥种子 hex（线下保管，绝不入库）+ 打印公钥 hex
//   sign <license.json> <私钥文件> <out.dat>   签发许可证
//   verify <license.dat>              用内嵌公钥校验
// license.json 格式：
//   { "name": "客户名", "licenseId": "CC-XXXX", "features": ["autoclean","cloud-rules-fast"],
//     "expiresAtUtc": null }   // null = 永久
// product 自动填 c-clear-pro；issuedAtUtc 自动填当前 UTC。

if (args.Length == 0)
{
    Console.WriteLine("用法: LicenseTool keygen <私钥hex文件> | sign <license.json> <私钥hex文件> <out.dat> | verify <license.dat>");
    return 1;
}

switch (args[0].ToLowerInvariant())
{
    case "keygen":
    {
        if (args.Length < 2)
        {
            Console.WriteLine("keygen 需要 <私钥hex文件> 输出路径");
            return 1;
        }
        var seed = RandomNumberGenerator.GetBytes(32);
        File.WriteAllText(args[1], Convert.ToHexString(seed).ToLowerInvariant());
        Console.WriteLine("私钥种子（hex，已写入 " + args[1] + "，务必线下保管、绝不提交仓库）：");
        Console.WriteLine(Convert.ToHexString(seed).ToLowerInvariant());
        Console.WriteLine("对应公钥（hex，填入 Cclear.Core/Licensing/LicenseService.cs 的 PublicKeyHex）：");
        Console.WriteLine(LicenseService.DerivePublicKeyHex(seed));
        return 0;
    }
    case "sign":
    {
        if (args.Length < 4)
        {
            Console.WriteLine("sign 需要 <license.json> <私钥hex文件> <out.dat>");
            return 1;
        }
        var request = JsonDocument.Parse(File.ReadAllText(args[1])).RootElement;
        var payload = new LicensePayload(
            request.GetProperty("name").GetString() ?? "",
            request.GetProperty("licenseId").GetString() ?? "",
            LicenseService.ProductId,
            request.TryGetProperty("features", out var features)
                ? features.EnumerateArray().Select(f => f.GetString() ?? "").Where(s => s.Length > 0).ToArray()
                : LicenseService.ProFeatures,
            DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            request.TryGetProperty("expiresAtUtc", out var expires) && expires.ValueKind == JsonValueKind.String
                ? expires.GetString()
                : null);
        var seed = Convert.FromHexString(File.ReadAllText(args[2]).Trim());
        var content = LicenseService.BuildLicenseFile(payload, seed);
        File.WriteAllText(args[3], content);
        Console.WriteLine("已签发 " + args[3]);
        Console.WriteLine("验证结果: " + (LicenseService.ValidateFile(args[3], LicenseService.DerivePublicKeyHex(seed)).IsValid ? "通过" : "失败"));
        return 0;
    }
    case "verify":
    {
        if (args.Length < 2)
        {
            Console.WriteLine("verify 需要 <license.dat>");
            return 1;
        }
        var result = LicenseService.ValidateFile(args[1]);
        Console.WriteLine(result.IsValid
            ? $"有效：{result.License!.Name}（{result.License.LicenseId}）功能：{string.Join(", ", result.License.Features)}"
                + (result.License.ExpiresAtUtc is null ? "，永久" : $"，到期 {result.License.ExpiresAtUtc:yyyy-MM-dd}")
            : "无效：" + result.Error);
        return result.IsValid ? 0 : 2;
    }
    default:
        Console.WriteLine("未知命令：" + args[0]);
        return 1;
}
