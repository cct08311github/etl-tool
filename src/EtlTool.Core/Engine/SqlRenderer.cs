using System.Globalization;
using System.Text;
using EtlTool.Core.Models;

namespace EtlTool.Core.Engine;

/// <summary>
/// 把參數化 SQL 內嵌參數值，產生「可直接複製到 SSMS / SQL Developer 重跑」的 SQL。
///
/// 設計：
///   - 字串：'value' (內部單引號 escape 成兩個單引號)
///   - 日期：provider 對應的字面值 (Oracle: TO_TIMESTAMP / SqlServer: '...')
///   - 數值：as-is
///   - bool：1/0
///   - byte[]：0x... (hex literal，僅 SqlServer；Oracle 用 HEXTORAW)
///   - null：NULL
///
/// 注意：產出的 SQL **僅供人類閱讀 / 偵錯**，實際執行還是用參數化 cmd。
/// </summary>
public static class SqlRenderer
{
    /// <summary>
    /// 把 SQL 中的 :param 或 @param 占位符替換成實際字面值。
    /// </summary>
    public static string Render(
        string parameterizedSql,
        IReadOnlyList<(string Name, object? Value)> parameters,
        DbProviderType provider,
        string parameterPrefix)
    {
        if (parameters.Count == 0) return parameterizedSql;

        var sb = new StringBuilder(parameterizedSql);

        // 從長到短替換以避免 ":f10" 被 ":f1" 先吃掉
        foreach (var p in parameters.OrderByDescending(p => p.Name.Length))
        {
            var token = parameterPrefix + p.Name;
            var literal = FormatValue(p.Value, provider);
            sb.Replace(token, literal);
        }

        return sb.ToString();
    }

    /// <summary>產生 SQL 字面值字串。</summary>
    public static string FormatValue(object? value, DbProviderType provider)
    {
        if (value is null || value is DBNull) return "NULL";

        return value switch
        {
            string s => $"'{s.Replace("'", "''")}'",
            char c   => c == '\'' ? "''''" : $"'{c}'",
            bool b   => b ? "1" : "0",
            DateTime dt => DateTokenResolver.FormatLiteral(dt, provider),
            DateTimeOffset dto => DateTokenResolver.FormatLiteral(dto.UtcDateTime, provider),
            byte[] bytes => provider == DbProviderType.Oracle
                ? $"HEXTORAW('{Convert.ToHexString(bytes)}')"
                : $"0x{Convert.ToHexString(bytes)}",
            // 數值 (int, long, decimal, float, double 等) — 用 InvariantCulture 確保 "."
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => $"'{value.ToString()?.Replace("'", "''") ?? ""}'",
        };
    }
}
