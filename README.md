# 配额统计 QuotaChecker

WinUI 3 风格的 Windows 桌面程序：添加多个 API 密钥，自动查询每个密钥的 **quota 消耗**，换算成**人民币金额**，并汇总全部密钥的总消耗。

数据来源为中转站（4sapi / new-api 系）的 `GET /api/usage/token` 接口。

![界面示意](docs/screenshot.png)

## 下载 / 直接使用

不想自行编译的话，到 [Releases](../../releases) 下载 `QuotaChecker-portable-win-x64.zip`，解压后双击 `QuotaChecker.exe` 即可。

该便携版为 **self-contained**：目标机器无需安装 .NET 运行时，也无需安装 Windows App SDK。

## 功能

- **添加多个密钥**：每个密钥可单独命名、指定接口地址，添加后立即自动查询
- **自动查询 quota**：启动时自动刷新全部启用的密钥，也可手动「刷新全部」或单独刷新某个密钥
- **换算人民币**：`配额 ÷ 500000 × 站点倍率`，界面同步显示配额原值、官方美元价与人民币金额
- **总计消耗金额**：顶部卡片实时汇总所有启用密钥的累计消耗、剩余额度与总额度
- **无限额度 / 过期时间**：正确识别 `unlimited_quota`，无限额度密钥不计入总额度统计
- **密钥安全**：API Key 使用 Windows DPAPI（仅当前用户可解密）加密后存于本机，界面只显示掩码
- **跟随系统主题**：浅色 / 深色自动适配，窗口使用 Mica 背景

## 换算规则

| 项目 | 说明 |
| --- | --- |
| 配额基数 | new-api 体系中 `500000 配额 = 官方计价 1 美元` |
| 站点倍率 | 1 倍率即「1 美元官方价格扣 1 元」，2 倍率即扣 2 元，可在界面调整 |
| 人民币金额 | `配额 ÷ 500000 × 倍率` |
| 备注 | 4sapi 基础汇率固定 1 美元 = 1 人民币；若站点倍率不同（如 6 倍率）直接改界面上的倍率即可 |

示例：`1,250,000 配额` → `$2.5 官方计价` → 1 倍率下 `¥2.5000`

## 接口

```
GET {站点地址}/api/usage/token
Authorization: Bearer sk-xxxxxxxx
```

响应关键字段：

```json
{
  "code": true,
  "data": {
    "name": "令牌名",
    "total_granted": 30000000,
    "total_used": 1250000,
    "total_available": 28750000,
    "unlimited_quota": false,
    "expires_at": 1798761600,
    "user_quota": { "total_available": 50000000, "total_used": 8300000 }
  }
}
```

站点地址可以只填域名（如 `https://4sapi.com`），程序会自动补全 `/api/usage/token`。

## 构建与运行

环境要求：

- Windows 10 1809（17763）或更高版本
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（本项目在 8.0.426 上验证通过）
- **无需安装 Visual Studio**：PRI / XAML 生成所需的 MSBuild 任务由 `Microsoft.Windows.SDK.BuildTools.MSIX` 包提供

clone 并构建：

```powershell
git clone https://github.com/Yanmiao233/4SAPIQuotaChecker.git
cd 4SAPIQuotaChecker

# 默认 win-x64
dotnet build src\QuotaChecker\QuotaChecker.csproj -c Debug

# 指定平台（x64 / ARM64）
dotnet build src\QuotaChecker\QuotaChecker.csproj -c Debug -p:Platform=x64
dotnet build src\QuotaChecker\QuotaChecker.csproj -c Debug -p:Platform=ARM64
```

运行（unpackaged，双击 exe 或命令行启动）：

```powershell
.\src\QuotaChecker\bin\Debug\net8.0-windows10.0.19041.0\win-x64\QuotaChecker.exe
```

生成可分发的便携版目录：

```powershell
dotnet publish src\QuotaChecker\QuotaChecker.csproj -c Release -p:Platform=x64 -o publish
```

打包成压缩包：

```powershell
Compress-Archive -Path publish\* -DestinationPath QuotaChecker-portable-win-x64.zip -Force
```

程序为 **unpackaged + self-contained**：不需要安装 MSIX，也不需要目标机器预装 .NET 运行时或 Windows App SDK。

## 数据存放位置

```
%LOCALAPPDATA%\QuotaChecker\keys.json
```

密钥以 DPAPI 密文（`ProtectedApiKey`）保存，仅同一 Windows 账号可解密；换机器或换账号后需要重新录入。
状态栏右下角会显示该路径。

## 项目结构

```
QuotaChecker.sln
src/QuotaChecker/
├─ App.xaml(.cs)                  应用入口
├─ MainWindow.xaml(.cs)           窗口外壳（Mica 背景、自定义标题栏、尺寸）
├─ Models/AppModels.cs            存储模型、设置、响应解析
├─ Services/
│  ├─ QuotaClient.cs              /api/usage/token 调用与地址规范化
│  └─ KeyStore.cs                 DPAPI 加密的本地存储
├─ Converters/
│  ├─ MoneyMath.cs                配额 → 美元 → 人民币 换算与格式化
│  └─ CommonConverters.cs         XAML 值转换器
├─ ViewModels/
│  ├─ MainViewModel.cs            汇总、刷新编排、持久化
│  └─ ApiKeyViewModel.cs          单个密钥的额度状态
└─ Views/
   ├─ MainPage.xaml(.cs)          主界面
   └─ AddKeyDialog.xaml(.cs)      添加密钥对话框
```

## 已知限制

- `user_quota`（账号余额）仅在令牌开启「允许查询用户额度」时由服务端返回；未返回时界面只展示令牌自身的额度。
- 汇率/倍率为手动填写，程序不会联网获取实时汇率。
