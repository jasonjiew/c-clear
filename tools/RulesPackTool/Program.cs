using Cclear.Core.Rules;

// 规则包生成工具（V2 F1）：
//   dotnet run --project tools/RulesPackTool -- <packVersion> <输出目录>
// 从 Cclear.Core 内置规则生成 rules-pack-vN.json（含 schemaVersion/packVersion/sha256）
// 与 rules-pack-vN.json.sha256 sidecar（整个包文件的 SHA256，供离线校验）。
// 规则包发布到 GitHub Releases 附件；社区规则参照 winapp2 模式接受 PR。

if (args.Length < 2)
{
    Console.WriteLine("用法: RulesPackTool <packVersion> <输出目录>");
    return 1;
}

if (!int.TryParse(args[0], out var packVersion) || packVersion < 1)
{
    Console.WriteLine("packVersion 必须是正整数");
    return 1;
}

var outputDir = args[1];
Directory.CreateDirectory(outputDir);

var rules = RulePackLoader.LoadEmbedded();
Console.WriteLine($"已加载内置规则 {rules.Count} 条");

var json = RulesPackFile.BuildPackJson(rules, packVersion);
var packPath = Path.Combine(outputDir, $"rules-pack-v{packVersion}.json");
File.WriteAllText(packPath, json + Environment.NewLine);

var fileSha = Convert.ToHexString(
    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(packPath))).ToLowerInvariant();
File.WriteAllText(packPath + ".sha256", fileSha + Environment.NewLine);

// 自校验：生成的包必须能通过 Load 的 SHA256 校验
var pack = RulesPackFile.Load(packPath);
Console.WriteLine($"已生成 {packPath}");
Console.WriteLine($"自校验通过：packVersion={pack.PackVersion}, rules={pack.Rules.Count}, sha256={pack.Sha256[..12]}…");
return 0;
