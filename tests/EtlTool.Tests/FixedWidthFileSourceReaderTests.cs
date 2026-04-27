using System.Text.Json;
using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

/// <summary>
/// 固定欄寬讀取：模擬 mainframe 風格 — 每行同寬度，欄位以 1-based 起點 + 長度切。
/// </summary>
public class FixedWidthFileSourceReaderTests : IDisposable
{
    private readonly string _tempDir;

    public FixedWidthFileSourceReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "etltool-fw-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private FileSourceConfig CfgWithLayout(params FixedWidthColumn[] cols) => new()
    {
        Format = FileSourceFormat.FixedWidth,
        FixedWidthLayoutJson = JsonSerializer.Serialize(cols),
        Encoding = "utf-8",
    };

    [Fact]
    public async Task Reads_single_row_with_three_columns()
    {
        var path = Path.Combine(_tempDir, "rec.txt");
        // ID(5) NAME(10) AMOUNT(8)
        File.WriteAllText(path, "00001Alice     00000100\n");

        var cfg = CfgWithLayout(
            new FixedWidthColumn { Name = "ID", Start = 1, Length = 5 },
            new FixedWidthColumn { Name = "NAME", Start = 6, Length = 10 },
            new FixedWidthColumn { Name = "AMOUNT", Start = 16, Length = 8 });
        var result = await new FixedWidthFileSourceReader().OpenAsync(path, cfg, default);
        Assert.Equal(new[] { "ID", "NAME", "AMOUNT" }, result.ColumnNames);

        using var dr = result.Reader;
        Assert.True(dr.Read());
        Assert.Equal("00001", dr.GetValue(0));
        Assert.Equal("Alice", dr.GetValue(1));      // trim 預設 on
        Assert.Equal("00000100", dr.GetValue(2));
        Assert.False(dr.Read());
    }

    [Fact]
    public async Task Trim_disabled_preserves_padding()
    {
        var path = Path.Combine(_tempDir, "padded.txt");
        File.WriteAllText(path, "Alice     \n");

        var cfg = CfgWithLayout(new FixedWidthColumn { Name = "NAME", Start = 1, Length = 10, TrimWhitespace = false });
        var result = await new FixedWidthFileSourceReader().OpenAsync(path, cfg, default);
        using var dr = result.Reader;
        Assert.True(dr.Read());
        Assert.Equal("Alice     ", dr.GetValue(0));
    }

    [Fact]
    public async Task Multiple_rows_streamed()
    {
        var path = Path.Combine(_tempDir, "multi.txt");
        File.WriteAllText(path,
            "00001Alice     \n" +
            "00002Bob       \n" +
            "00003Carol     \n");

        var cfg = CfgWithLayout(
            new FixedWidthColumn { Name = "ID", Start = 1, Length = 5 },
            new FixedWidthColumn { Name = "NAME", Start = 6, Length = 10 });
        var result = await new FixedWidthFileSourceReader().OpenAsync(path, cfg, default);

        using var dr = result.Reader;
        var names = new List<string>();
        while (dr.Read()) names.Add(dr.GetString(1));
        Assert.Equal(new[] { "Alice", "Bob", "Carol" }, names);
    }

    [Fact]
    public async Task Blank_lines_skipped()
    {
        var path = Path.Combine(_tempDir, "withblanks.txt");
        File.WriteAllText(path,
            "00001Alice     \n" +
            "\n" +                  // 空白行
            "   \n" +               // 全空白
            "00002Bob       \n");

        var cfg = CfgWithLayout(
            new FixedWidthColumn { Name = "ID", Start = 1, Length = 5 },
            new FixedWidthColumn { Name = "NAME", Start = 6, Length = 10 });
        var result = await new FixedWidthFileSourceReader().OpenAsync(path, cfg, default);

        using var dr = result.Reader;
        int rows = 0;
        while (dr.Read()) rows++;
        Assert.Equal(2, rows);
    }

    [Fact]
    public async Task Short_line_returns_null_for_overhanging_columns()
    {
        var path = Path.Combine(_tempDir, "short.txt");
        File.WriteAllText(path, "00001\n");  // 行只有 5 字元，但 layout 期待 16

        var cfg = CfgWithLayout(
            new FixedWidthColumn { Name = "ID", Start = 1, Length = 5 },
            new FixedWidthColumn { Name = "NAME", Start = 6, Length = 10 });
        var result = await new FixedWidthFileSourceReader().OpenAsync(path, cfg, default);

        using var dr = result.Reader;
        Assert.True(dr.Read());
        Assert.Equal("00001", dr.GetValue(0));
        Assert.True(dr.IsDBNull(1));
    }

    [Fact]
    public async Task Big5_encoding_works()
    {
        var big5 = System.Text.Encoding.GetEncoding("big5");
        var path = Path.Combine(_tempDir, "zh.txt");
        // 注意：固定欄寬 + 多 byte 編碼是個棘手議題。我們的實作以「字元」為單位
        // 切（不是 byte），所以 Length 5 = 5 個 Unicode 字元。對純 ASCII 主機檔
        // 是常見場景；對 Big5 中文檔需要使用者明白此差異。
        File.WriteAllText(path, "00001測試\n", big5);

        var cfg = CfgWithLayout(
            new FixedWidthColumn { Name = "ID", Start = 1, Length = 5 },
            new FixedWidthColumn { Name = "NAME", Start = 6, Length = 2 });
        cfg.Encoding = "big5";
        var result = await new FixedWidthFileSourceReader().OpenAsync(path, cfg, default);
        using var dr = result.Reader;
        Assert.True(dr.Read());
        Assert.Equal("00001", dr.GetValue(0));
        Assert.Equal("測試", dr.GetValue(1));
    }

    [Fact]
    public async Task Empty_layout_throws()
    {
        var path = Path.Combine(_tempDir, "any.txt");
        File.WriteAllText(path, "data");
        var cfg = new FileSourceConfig { Format = FileSourceFormat.FixedWidth, FixedWidthLayoutJson = "[]" };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FixedWidthFileSourceReader().OpenAsync(path, cfg, default));
    }

    [Fact]
    public async Task Invalid_layout_throws()
    {
        var path = Path.Combine(_tempDir, "any.txt");
        File.WriteAllText(path, "data");
        var cfg = CfgWithLayout(new FixedWidthColumn { Name = "ID", Start = 0, Length = 5 });  // Start < 1
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FixedWidthFileSourceReader().OpenAsync(path, cfg, default));
    }
}
