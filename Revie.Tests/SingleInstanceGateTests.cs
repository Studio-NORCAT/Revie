using DoodleCapture.App.Services;

namespace Revie.Tests;

public sealed class SingleInstanceGateTests
{
    [Fact]
    public void SecondGate_WithSameName_IsNotPrimaryUntilFirstIsDisposed()
    {
        var mutexName = $@"Local\Revie.Tests.{Guid.NewGuid():N}";

        {
            using var first = new SingleInstanceGate(mutexName);
            Assert.True(first.IsPrimaryInstance);

            using var second = new SingleInstanceGate(mutexName);
            Assert.False(second.IsPrimaryInstance);
        }

        using var third = new SingleInstanceGate(mutexName);
        Assert.True(third.IsPrimaryInstance);
    }
}
