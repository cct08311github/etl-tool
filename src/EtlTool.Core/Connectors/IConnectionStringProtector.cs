namespace EtlTool.Core.Connectors;

/// <summary>連線字串加解密抽象。實作於 EtlTool.Data（用 ASP.NET Core Data Protection）。</summary>
public interface IConnectionStringProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}
