namespace PrivacyProxy.Api.Tests;

public class UnitTest1
{
    [Fact]
    public void Test1()
    {
        Assert.True(true);
    }

    [Fact]
    public void TestSampleService()
    {
        Assert.Equal(2, new SampleService().Increment(1));
    }
}
