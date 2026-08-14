namespace MHServerEmu.WebFrontend.Tests
{
    public class WebFrontendServiceTests
    {
        [Fact]
        public void ValidateRateLimitSettings_RejectsInvalidEnabledLimiterSettings()
        {
            Assert.Throws<InvalidOperationException>(() => WebFrontendService.ValidateRateLimitSettings(true, 0, 1, true, 1, 1, 1));
            Assert.Throws<InvalidOperationException>(() => WebFrontendService.ValidateRateLimitSettings(true, 1, 0, true, 1, 1, 1));
            Assert.Throws<InvalidOperationException>(() => WebFrontendService.ValidateRateLimitSettings(true, 1, 1, true, 0, 1, 1));
            Assert.Throws<InvalidOperationException>(() => WebFrontendService.ValidateRateLimitSettings(true, 1, 1, true, 1, 0, 1));
            Assert.Throws<InvalidOperationException>(() => WebFrontendService.ValidateRateLimitSettings(true, 1, 1, true, 1, 1, 0));
        }
    }
}
