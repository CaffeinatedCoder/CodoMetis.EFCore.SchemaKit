namespace CodoMetis.EFCore.SchemaKit.Tests;

/// <summary>
/// Never for main. One failing unit test in the first project of the workflow's <c>Test</c> step,
/// to see that step red (#150). It leaves the branch before the pull request is ready.
/// </summary>
[TestClass]
public sealed class Issue150FailsOnPurpose
{
    [TestMethod]
    public void A_failing_unit_test_fails_the_Test_step()
        => Assert.Fail("On purpose, for #150: the Test step has to be red with this test in it.");
}
