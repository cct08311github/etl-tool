using EtlTool.Core.Engine;
using EtlTool.Core.Models;

namespace EtlTool.Tests;

/// <summary>
/// 純字串解析 + advisory 列表，給 ConnectionEdit 存檔時跑。
/// </summary>
public class ConnectionStringInspectorTests
{
    private static List<ConnectionStringInspector.Advisory> Inspect(DbProviderType p, string cs) =>
        ConnectionStringInspector.Inspect(p, cs).ToList();

    [Fact]
    public void Empty_string_warning()
    {
        var r = Inspect(DbProviderType.SqlServer, "");
        Assert.Contains(r, a => a.Code == "EMPTY");
    }

    [Fact]
    public void TrustServerCertificate_true_flagged()
    {
        var r = Inspect(DbProviderType.SqlServer,
            "Server=db;Database=etl;User ID=svc;Password=ComplexP4ss!2026;TrustServerCertificate=true");
        Assert.Contains(r, a => a.Code == "TRUST_SERVER_CERT" && a.Severity == ConnectionStringInspector.AdvisorySeverity.Warning);
    }

    [Fact]
    public void Encrypt_false_flagged()
    {
        var r = Inspect(DbProviderType.SqlServer,
            "Server=db;User ID=svc;Password=ComplexP4ss!2026;Encrypt=false");
        Assert.Contains(r, a => a.Code == "ENCRYPT_FALSE");
    }

    [Fact]
    public void Short_password_flagged()
    {
        var r = Inspect(DbProviderType.SqlServer,
            "Server=db;User ID=svc;Password=abc12");
        Assert.Contains(r, a => a.Code == "SHORT_PASSWORD");
    }

    [Fact]
    public void All_digits_password_suggestion()
    {
        var r = Inspect(DbProviderType.SqlServer,
            "Server=db;User ID=svc;Password=123456789012");
        Assert.Contains(r, a => a.Code == "MONO_CHARSET");
    }

    [Fact]
    public void Common_weak_password_warning()
    {
        var r = Inspect(DbProviderType.SqlServer,
            "Server=db;User ID=svc;Password=password");
        Assert.Contains(r, a => a.Code == "COMMON_PASSWORD" || a.Code == "SHORT_PASSWORD");
    }

    [Fact]
    public void Integrated_security_yields_suggestion_not_warning()
    {
        var r = Inspect(DbProviderType.SqlServer,
            "Server=db;Integrated Security=SSPI;Database=etl");
        Assert.Contains(r, a => a.Code == "INTEGRATED_SECURITY");
        Assert.DoesNotContain(r, a => a.Code == "NO_AUTH");
    }

    [Fact]
    public void Missing_password_no_integrated_yields_no_auth()
    {
        var r = Inspect(DbProviderType.SqlServer,
            "Server=db;Database=etl;User ID=svc");
        Assert.Contains(r, a => a.Code == "NO_AUTH");
    }

    [Fact]
    public void Strong_password_no_warnings_for_password()
    {
        var r = Inspect(DbProviderType.SqlServer,
            "Server=db;Database=etl;User ID=svc;Password=Banking_Sv0_2026!@#");
        Assert.DoesNotContain(r, a => a.Code is "SHORT_PASSWORD" or "MONO_CHARSET" or "COMMON_PASSWORD");
    }

    [Theory]
    [InlineData("sys")]
    [InlineData("SYSTEM")]
    public void Oracle_privileged_user_warning(string user)
    {
        var r = Inspect(DbProviderType.Oracle,
            $"Data Source=//ora:1521/XEPDB1;User Id={user};Password=ComplexPass2026!");
        Assert.Contains(r, a => a.Code == "PRIV_USER");
    }

    [Fact]
    public void Oracle_normal_user_no_priv_warning()
    {
        var r = Inspect(DbProviderType.Oracle,
            "Data Source=//ora:1521/XEPDB1;User Id=etl_svc;Password=ComplexPass2026!");
        Assert.DoesNotContain(r, a => a.Code == "PRIV_USER");
    }

    [Fact]
    public void Quoted_password_value_handled()
    {
        // 一般密碼帶引號（無 ; 嵌入）— inspector 應移除引號後判斷強度
        var r = Inspect(DbProviderType.SqlServer,
            "Server=db;User ID=svc;Password=\"ComplexPass2026!\";Encrypt=true");
        Assert.DoesNotContain(r, a => a.Code == "EMPTY_PASSWORD");
        Assert.DoesNotContain(r, a => a.Code == "SHORT_PASSWORD");
    }

    [Fact]
    public void Short_connection_timeout_suggestion()
    {
        var r = Inspect(DbProviderType.SqlServer,
            "Server=db;User ID=svc;Password=ComplexPass2026!;Connection Timeout=2");
        Assert.Contains(r, a => a.Code == "SHORT_CONN_TIMEOUT");
    }
}
