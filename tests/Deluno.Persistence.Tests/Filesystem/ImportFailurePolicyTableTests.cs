using Deluno.Contracts;

namespace Deluno.Persistence.Tests.Filesystem;

/// <summary>
/// The import pipeline decides on its own where a recovery case is raised (#1329, #1436). The policy's old
/// RaisesRecoveryCase table was used by nothing and was removed; this keeps a second, unused answer from coming back.
/// </summary>
public sealed class ImportFailurePolicyTableTests
{
    [Fact]
    public void The_policy_keeps_no_unused_table_of_which_reasons_ask_a_person_to_look()
    {
        Assert.Null(typeof(ImportFailurePolicy).GetMethod("RaisesRecoveryCase"));
    }
}
