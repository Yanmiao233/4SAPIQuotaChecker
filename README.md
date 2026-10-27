# 配额统计 QuotaChecker

Windows 桌面工具（WinUI 3）：管理多个中转站 API Key，查询配额消耗并换算人民币。

## 功能

- 多密钥管理，支持逐个命名与自定义接口地址
- 查询令牌配额：`GET /api/usage/token/`
- 配额换算人民币：`配额 ÷ 500000 × 站点倍率`
- 展示累计消耗、剩余额度、总额度
- 各模型消耗明细（`GET /api/log/token`，展开时加载）
- 定时自动刷新：关闭 / 1 / 5 / 15 / 30 / 60 分钟
- 自动检查更新：关闭 / 6 小时 / 每天 / 每周
- 应用内更新：检查、下载、重启安装
- API Key 以 Windows DPAPI 加密存储
- 浅色 / 深色主题自适应，Mica 背景

## 下载

[Releases](https://github.com/Yanmiao233/4SAPIQuotaChecker/releases) → `QuotaChecker-portable-win-x64-v<版本>.zip`

```
QuotaChecker-portable-win-x64\
├── QuotaChecker.exe
├── 使用说明.txt
└── app\
```

解压后双击 `QuotaChecker.exe`。免安装、免 .NET 运行时、免 Windows App SDK。

## 换算规则

| 项目 | 值 |
| --- | --- |
| 配额基数 | 500000 配额 = 官方计价 1 美元 |
| 站点倍率 | 官方 1 美元对应的人民币金额，默认 1 |
| 金额 | 配额 ÷ 500000 × 倍率 |

## 数据存储

默认位置：

```
%LOCALAPPDATA%\QuotaChecker\
├── keys.json          密钥与设置（API Key 为 DPAPI 密文）
├── keys.json.bak      上一次成功保存的备份
└── crash.log          未处理异常记录
```

`%LOCALAPPDATA%` 不可写时，按顺序自动降级到：

1. `%APPDATA%\QuotaChecker\`
2. 程序目录下 `data\`（便携）
3. `%TEMP%\QuotaChecker\`

- 优先使用「已有数据且可写」的目录
- 损坏的文件隔离为 `keys.json.corrupt-<时间戳>`，不直接覆盖
- 主文件无法解析时自动从 `.bak` 恢复
- 保存失败在界面提示并显示新位置，不静默丢弃

## 接口

```
GET {站点地址}/api/usage/token/     令牌配额
GET {站点地址}/api/log/token        最近调用日志（按模型聚合）
```

站点地址可只填域名，程序自动补全路径。

## 构建

环境：Windows 10 1809+ / .NET 8 SDK（无需 Visual Studio）。

```powershell
dotnet build src\QuotaChecker\QuotaChecker.csproj -c Debug
dotnet build src\QuotaChecker\QuotaChecker.csproj -c Debug -p:Platform=ARM64
```

## 打包

```powershell
# 便携版压缩包（含裁剪、清单、启动器）
powershell -ExecutionPolicy Bypass -File tools\build-portable.ps1

# 更新清单（发布 Release 时与 zip 一起上传）
powershell -ExecutionPolicy Bypass -File tools\make-update-manifest.ps1 `
    -ZipPath dist\QuotaChecker-portable-win-x64-v1.5.2.zip `
    -NotesFile notes.md
```

| 参数 | 说明 |
| --- | --- |
| `-Platform ARM64` | 生成 ARM64 包 |
| `-NoTrim` | 关闭裁剪 |
| `-KeepAllLocales` | 保留全部语言资源 |
| `-Version x.y.z` | 覆盖版本号 |

## 更新机制

检查更新读取 `releases/latest/download/update.json`（普通下载，不使用 GitHub API）。

## 项目结构

```
├─ src/QuotaChecker/           程序
├─ tools/
│  ├─ build-portable.ps1       打包
│  ├─ make-update-manifest.ps1 生成 update.json
│  ├─ make-launcher.ps1        生成外层启动器
│  └─ make-icon.ps1            生成图标
├─ packaging/                  包根目录素材
└─ CHANGELOG.md
```

## 已知限制

- `user_quota` 仅在令牌开启「允许查询用户额度」时返回
- 汇率/倍率需手动填写
- 自动更新仅支持 win-x64 便携版布局
