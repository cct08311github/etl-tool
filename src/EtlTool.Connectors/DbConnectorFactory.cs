using EtlTool.Connectors.Oracle;
using EtlTool.Connectors.SqlServer;
using EtlTool.Core.Connectors;
using EtlTool.Core.Models;

namespace EtlTool.Connectors;

public sealed class DbConnectorFactory : IDbConnectorFactory
{
    private readonly IConnectionStringProtector _protector;

    public DbConnectorFactory(IConnectionStringProtector protector)
    {
        _protector = protector;
    }

    public IDbConnector Create(DbProviderType providerType, string connectionString) => providerType switch
    {
        DbProviderType.Oracle => new OracleConnector(connectionString),
        DbProviderType.SqlServer => new SqlServerConnector(connectionString),
        _ => throw new ArgumentOutOfRangeException(nameof(providerType), providerType, null),
    };

    public IDbConnector Create(ConnectionDefinition definition)
    {
        var plain = _protector.Unprotect(definition.EncryptedConnectionString);
        return Create(definition.ProviderType, plain);
    }
}
