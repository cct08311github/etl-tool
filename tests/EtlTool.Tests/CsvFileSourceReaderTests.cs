using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

/// <summary>
/// 用 tmp 檔測 CsvFileSourceReader：header / no-header / 中英混合 / 自訂分隔字元 / Big5 編碼。
/// 不需要 DB；純檔案 IO + IDataReader 行為。
/// </summary>
public class CsvFileSourceReaderTests : IDisposable
{
    private readonly string _tempDir;

    public CsvFileSourceReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "etltool-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string WriteFile(string name, string content, System.Text.Encoding? enc = null)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content, enc ?? System.Text.Encoding.UTF8);
        return path;
    }

    [Fact]
    public async Task Reads_csv_with_header_correctly()
    {
        var path = WriteFile("orders.csv",
            "id,name,amount\n" +
            "1,Alice,100\n" +
            "2,Bob,250\n");

        var config = new FileSourceConfig { Format = FileSourceFormat.Csv, HasHeader = true, Delimiter = "," };
        var reader = new CsvFileSourceReader();
        var result = await reader.OpenAsync(path, config, default);

        Assert.Equal(new[] { "id", "name", "amount" }, result.ColumnNames);

        using var dr = result.Reader;
        Assert.True(dr.Read());
        Assert.Equal("1", dr.GetValue(0));
        Assert.Equal("Alice", dr.GetValue(1));
        Assert.Equal("100", dr.GetValue(2));
        Assert.True(dr.Read());
        Assert.Equal("Bob", dr.GetValue(1));
        Assert.False(dr.Read());
    }

    [Fact]
    public async Task Reads_csv_without_header_generates_col_names()
    {
        var path = WriteFile("nohead.csv",
            "1,foo\n" +
            "2,bar\n");

        var config = new FileSourceConfig { Format = FileSourceFormat.Csv, HasHeader = false, Delimiter = "," };
        var reader = new CsvFileSourceReader();
        var result = await reader.OpenAsync(path, config, default);

        Assert.Equal(new[] { "col0", "col1" }, result.ColumnNames);
        using var dr = result.Reader;
        Assert.True(dr.Read());
        Assert.Equal("1", dr.GetValue(0));
        Assert.Equal("foo", dr.GetValue(1));
    }

    [Fact]
    public async Task Custom_delimiter_works()
    {
        var path = WriteFile("pipe.csv", "a|b|c\n1|2|3\n");
        var config = new FileSourceConfig
        {
            Format = FileSourceFormat.Csv,
            HasHeader = true,
            Delimiter = "|",
        };
        var reader = new CsvFileSourceReader();
        var result = await reader.OpenAsync(path, config, default);
        Assert.Equal(new[] { "a", "b", "c" }, result.ColumnNames);
        using var dr = result.Reader;
        Assert.True(dr.Read());
        Assert.Equal("2", dr.GetValue(1));
    }

    [Fact]
    public async Task Tab_delimiter_via_escape_works()
    {
        var path = WriteFile("tab.txt", "a\tb\n1\t2\n");
        var config = new FileSourceConfig
        {
            Format = FileSourceFormat.DelimitedText,
            HasHeader = true,
            Delimiter = "\\t",
        };
        var reader = new DelimitedTextFileSourceReader();
        var result = await reader.OpenAsync(path, config, default);
        Assert.Equal(new[] { "a", "b" }, result.ColumnNames);
    }

    [Fact]
    public async Task Big5_encoding_decodes_chinese()
    {
        var big5 = System.Text.Encoding.GetEncoding("big5");
        var path = WriteFile("zh.csv", "編號,姓名\n1,測試\n", big5);

        var config = new FileSourceConfig { Format = FileSourceFormat.Csv, HasHeader = true, Encoding = "big5" };
        var reader = new CsvFileSourceReader();
        var result = await reader.OpenAsync(path, config, default);

        Assert.Equal(new[] { "編號", "姓名" }, result.ColumnNames);
        using var dr = result.Reader;
        dr.Read();
        Assert.Equal("測試", dr.GetValue(1));
    }

    [Fact]
    public async Task Quoted_value_with_comma_handled()
    {
        var path = WriteFile("quoted.csv",
            "id,name,note\n" +
            "1,Alice,\"daily, critical\"\n");
        var config = new FileSourceConfig { Format = FileSourceFormat.Csv, HasHeader = true, Delimiter = "," };
        var reader = new CsvFileSourceReader();
        var result = await reader.OpenAsync(path, config, default);
        using var dr = result.Reader;
        dr.Read();
        Assert.Equal("daily, critical", dr.GetValue(2));
    }

    [Fact]
    public async Task Throws_for_missing_file()
    {
        var reader = new CsvFileSourceReader();
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            reader.OpenAsync(Path.Combine(_tempDir, "nope.csv"), new FileSourceConfig(), default));
    }

    [Fact]
    public void Encoding_resolver_handles_aliases()
    {
        // .NET's Encoding.WebName returns lowercase identifiers
        Assert.Equal("big5", CsvFileSourceReader.ResolveEncoding("big5").WebName);
        Assert.Equal("big5", CsvFileSourceReader.ResolveEncoding("CP950").WebName);
        Assert.Equal("big5", CsvFileSourceReader.ResolveEncoding("Windows-950").WebName);
        Assert.Equal("utf-8", CsvFileSourceReader.ResolveEncoding("utf-8").WebName);
        Assert.Equal("utf-8", CsvFileSourceReader.ResolveEncoding("").WebName);
        Assert.Equal("gb18030", CsvFileSourceReader.ResolveEncoding("gb18030").WebName);
    }

    [Fact]
    public void Delimiter_resolver_unescapes_tab()
    {
        Assert.Equal("\t", CsvFileSourceReader.ResolveDelimiter("\\t"));
        Assert.Equal(",", CsvFileSourceReader.ResolveDelimiter(""));
        Assert.Equal("|", CsvFileSourceReader.ResolveDelimiter("|"));
    }
}
