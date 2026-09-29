using AppPlatform.Api;
using AppPlatform.Platform.Features.Errors;
using AppPlatform.Platform.Services;
using Moq;

namespace AppPlatform.Platform.Tests;

public class GetErrorFeedTests
{
    [Theory]
    [InlineData(null, GetErrorFeedFeature.DefaultLimit)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(50, 50)]
    [InlineData(100_000, GetErrorFeedFeature.MaxLimit)]
    public async Task The_limit_is_clamped_rather_than_refused(int? asked, int used)
    {
        var feed = new Mock<IErrorFeedService>();
        feed.Setup(f => f.RecentAsync(It.IsAny<int>(), default)).ReturnsAsync([]);

        var result = await new GetErrorFeedFeature.GetErrorFeedQueryHandler(feed.Object).Handle(asked);

        Assert.Equal(CommandOutcome.Succeeded, result.Outcome);
        feed.Verify(f => f.RecentAsync(used, default), Times.Once);
    }
}
