# winget 清单提交说明

本目录包含提交到 winget-pkgs 的清单模板（`manifests/c/ClearContributors/C-Clear/0.1.0/`）。

## 提交前需要完成

1. 在 GitHub 建立公开仓库并推送代码（含 LICENSE 与 README）。
2. 通过 Release 工作流（推 `v0.1.0` 标签）发布，得到 `Cclear.App.exe` 的 Release 下载地址。
3. 将三个 YAML 中的 `<RELEASE-URL>` 替换为真实下载地址，并核对 InstallerSha256：
   ```powershell
   Get-FileHash .\Cclear.App.exe -Algorithm SHA256
   ```
4. Fork [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs)，按
   `manifests/c/ClearContributors/C-Clear/0.1.0/` 路径放入三个文件并发 PR；
   PR 描述中附 `winget validate` 与本地安装截图。
5. 提交账号需签署 CLA（首次 PR 时机器人会引导）。

## 本地校验

```powershell
winget install --manifest .\manifests\c\ClearContributors\C-Clear\0.1.0\
winget validate .\manifests\c\ClearContributors\C-Clear\0.1.0\
```

注意：本应用未购买代码签名证书（beta 阶段），SmartScreen 会提示"未知发布者"，
安装器类型使用 portable（单 exe 复制到 LocalAppData 并加入 PATH）。
