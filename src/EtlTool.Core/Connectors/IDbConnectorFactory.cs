using EtlTool.Core.Models;

namespace EtlTool.Core.Connectors;

public interface IDbConnectorFactory
{
    IDbConnector Create(DbProviderType providerType, string connectionString);

    /// <summary>從已存在的 ConnectionDefinition（內含加密的連線字串）建立 connector。</summary>
    IDbConnector Create(ConnectionDefinition definition);
}
