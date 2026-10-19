# 发布方式

仓库：[FFailing/copyflash](https://github.com/FFailing/copyflash)。发布入口为该仓库的 Releases。

## 构建与核验

1. 在 Windows x64 上运行 `build.ps1`。
2. 运行 `dist/CopyFlash.exe --self-test` 并检查退出码。
3. 运行实验窗口，核对复制、原文反馈、粘贴、暂停与退出。兼容性记录在 `VALIDATION.md`，未测试的项目要保留为未测试。
4. 将 dist 内文件压缩成便携包，核对其中 `SHA256SUMS.txt` 与 exe 一致。
5. 在 GitHub Releases 新建预发布，选择明确的版本标签，附 `RELEASE.md` 说明，上传便携包及校验文件。实验版本必须标为 Pre-release。
6. 打开发布页核对附件可用，再分享真实仓库和下载地址。

## CI 与首版附件

Windows preview 工作流在 main 提交时执行源码构建、自检和打包；发布 Release 时，只有构建和自检成功后，发布任务才会将便携包与 ZIP 的 SHA256 清单附加到该 Release。附件由对应标签的公开源码构建。云端运行结果以 Actions 的实际回执为准；本地自检不等于云端 CI 成功。

## 对外描述

Windows 托盘实验工具。目标软件提供选区位置时尝试原文反色；取不到位置时回退为提示或跳过。不得将未完成的全应用、多屏、缩放或性能测试写成已验证。交互示意动画不标为原生软件录屏。

发布源码时不加入账户凭据、运营材料、聊天记录、本机运行日志或 dist 目录。便携包只放进 Release 附件。

