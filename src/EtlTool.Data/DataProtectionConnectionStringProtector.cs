using EtlTool.Core.Connectors;
using Microsoft.AspNetCore.DataProtection;

namespace EtlTool.Data;

/// <summary>用 ASP.NET Core Data Protection API 加解密連線字串。Key ring 配置由 DI 提供。</summary>
public sealed class DataProtectionConnectionStringProtector : IConnectionStringProtector
{
    private const string Purpose = "EtlTool.ConnectionString.v1";
    private readonly IDataProtector _protector;

    public DataProtectionConnectionStringProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);
    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}
