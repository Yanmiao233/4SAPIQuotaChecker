using System.Text.Json.Serialization;

namespace QuotaChecker.Models;

/// <summary>
/// 本地数据文件的源生成序列化上下文。
/// 使用源生成而非反射，既能在裁剪（trimming）后可靠工作，也避免了运行时反射开销。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppDataFile))]
[JsonSerializable(typeof(StoredKey))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}
