using EtlTool.Core.Engine;

namespace EtlTool.Tests;

/// <summary>
/// Verify the error classifier categorises real-world DB errors correctly.
/// 用 Exception(message) 模擬不同 provider 的訊息字串；同時也測 inner-exception
/// chain 走最內層那筆。
/// </summary>
public class EngineErrorClassifierTests
{
    [Fact]
    public void Null_returns_unknown()
    {
        var c = EngineErrorClassifier.Classify(null);
        Assert.Equal(EngineErrorClassifier.EngineErrorClass.Unknown, c.Class);
    }

    [Theory]
    [InlineData("Transaction (Process ID 53) was deadlocked on lock resources")]
    [InlineData("Msg 1205, deadlock victim. Rerun the transaction")]
    [InlineData("ORA-00060: deadlock detected while waiting for resource")]
    public void Deadlock_messages_classify_as_transient_deadlock(string msg)
    {
        var c = EngineErrorClassifier.Classify(new InvalidOperationException(msg));
        Assert.Equal(EngineErrorClassifier.EngineErrorClass.Transient, c.Class);
        Assert.Equal(EngineErrorClassifier.EngineErrorSubkind.TransientDeadlock, c.Subkind);
    }

    [Theory]
    [InlineData("Msg 1222, lock request time out period exceeded")]
    [InlineData("LOCK_TIMEOUT exceeded waiting for resource")]
    public void Lock_timeout_classifies_as_transient(string msg)
    {
        var c = EngineErrorClassifier.Classify(new Exception(msg));
        Assert.Equal(EngineErrorClassifier.EngineErrorSubkind.TransientLockTimeout, c.Subkind);
    }

    [Theory]
    [InlineData("A network-related or instance-specific error occurred")]
    [InlineData("Connection refused (Connection refused)")]
    [InlineData("ORA-12541: TNS:no listener")]
    [InlineData("ORA-03113: end-of-file on communication channel")]
    [InlineData("An existing connection was forcibly closed by the remote host")]
    public void Network_errors_classify_as_transient_network(string msg)
    {
        var c = EngineErrorClassifier.Classify(new Exception(msg));
        Assert.Equal(EngineErrorClassifier.EngineErrorClass.Transient, c.Class);
        Assert.Equal(EngineErrorClassifier.EngineErrorSubkind.TransientNetwork, c.Subkind);
    }

    [Theory]
    [InlineData("Login failed for user 'etl_svc'. (Msg 18456)")]
    [InlineData("ORA-01017: invalid username/password; logon denied")]
    public void Auth_errors_classify_as_permanent_auth(string msg)
    {
        var c = EngineErrorClassifier.Classify(new Exception(msg));
        Assert.Equal(EngineErrorClassifier.EngineErrorClass.Permanent, c.Class);
        Assert.Equal(EngineErrorClassifier.EngineErrorSubkind.PermanentAuth, c.Subkind);
    }

    [Theory]
    [InlineData("Invalid object name 'dbo.NoSuchTable' (Msg 208)")]
    [InlineData("ORA-00942: table or view does not exist")]
    [InlineData("ORA-00904: \"BAD_COL\": invalid identifier")]
    public void Schema_missing_classifies_as_permanent(string msg)
    {
        var c = EngineErrorClassifier.Classify(new Exception(msg));
        Assert.Equal(EngineErrorClassifier.EngineErrorClass.Permanent, c.Class);
        // ORA-00942 在規則內排在 SchemaMissing 前 → 該歸 SchemaMissing
        Assert.True(c.Subkind == EngineErrorClassifier.EngineErrorSubkind.PermanentSchemaMissing
                 || c.Subkind == EngineErrorClassifier.EngineErrorSubkind.PermanentPermissionDenied);
    }

    [Theory]
    [InlineData("Violation of PRIMARY KEY constraint 'PK_Orders'")]
    [InlineData("Cannot insert duplicate key in object 'dbo.Customers' (Msg 2627)")]
    [InlineData("ORA-00001: unique constraint (HR.PK_EMP) violated")]
    [InlineData("Violation of FOREIGN KEY constraint 'FK_Orders_Customers' (Msg 547)")]
    [InlineData("ORA-02291: integrity constraint violated - parent key not found")]
    public void Pk_fk_violations_classify_as_data_integrity(string msg)
    {
        var c = EngineErrorClassifier.Classify(new Exception(msg));
        Assert.Equal(EngineErrorClassifier.EngineErrorSubkind.PermanentDataIntegrity, c.Subkind);
    }

    [Theory]
    [InlineData("Incorrect syntax near 'WHERE' (Msg 102)")]
    [InlineData("ORA-00936: missing expression")]
    [InlineData("ORA-00911: invalid character")]
    public void Syntax_errors_classify_as_permanent(string msg)
    {
        var c = EngineErrorClassifier.Classify(new Exception(msg));
        Assert.Equal(EngineErrorClassifier.EngineErrorSubkind.PermanentSyntax, c.Subkind);
    }

    [Fact]
    public void Walks_inner_exception_chain()
    {
        // 真實場景：連線抖斷被 EF / ADO.NET 包成多層 InvalidOperationException → SqlException
        var inner = new Exception("ORA-12541: TNS:no listener");
        var middle = new InvalidOperationException("Could not establish session", inner);
        var outer = new InvalidOperationException("ETL run failed", middle);
        var c = EngineErrorClassifier.Classify(outer);
        Assert.Equal(EngineErrorClassifier.EngineErrorSubkind.TransientNetwork, c.Subkind);
    }

    [Fact]
    public void Unknown_message_falls_back_to_unknown()
    {
        var c = EngineErrorClassifier.Classify(new Exception("something weird happened in production"));
        Assert.Equal(EngineErrorClassifier.EngineErrorClass.Unknown, c.Class);
    }
}
