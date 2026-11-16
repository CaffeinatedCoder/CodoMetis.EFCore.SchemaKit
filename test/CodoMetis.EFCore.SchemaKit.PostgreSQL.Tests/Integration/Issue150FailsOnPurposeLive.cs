namespace CodoMetis.EFCore.SchemaKit.PostgreSQL.Tests.Integration;

/// <summary>
/// Never for main. One failing test of the category the integration lanes run, in the second
/// project of their <c>Integration tests</c> step, to see that step red (#150). It asks nothing of
/// the server. It leaves the branch before the pull request is ready.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class Issue150FailsOnPurposeLive
{
    [TestMethod]
    public void A_failing_integration_test_fails_the_Integration_tests_step()
        => Assert.Fail("On purpose, for #150: the Integration tests step has to be red with this test in it.");
}
