using System.IO;
using System.Net;
using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
[DoNotParallelize]
public sealed class UpdateServiceTests
{
    [TestMethod]
    [DataRow(HttpStatusCode.NotFound, false)]
    [DataRow(HttpStatusCode.InternalServerError, true)]
    public async Task MissingReleaseIsQuietWhileOtherHttpFailuresAreLogged(HttpStatusCode statusCode, bool shouldLog)
    {
        using var directory = new TemporaryTestDirectory();
        var previousPath = ExceptionDiagnostics.LogPath;
        var logPath = directory.File("exceptions.log");
        ExceptionDiagnostics.LogPath = logPath;
        try
        {
            using var client = new HttpClient(new StatusCodeHandler(statusCode));

            await UpdateService.CheckForUpdatesAsync(client);

            Assert.AreEqual(shouldLog, File.Exists(logPath));
            if (shouldLog)
                StringAssert.Contains(File.ReadAllText(logPath), nameof(UpdateService.CheckForUpdatesAsync));
        }
        finally
        {
            ExceptionDiagnostics.LogPath = previousPath;
        }
    }

    private sealed class StatusCodeHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }
}
