using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noted.Services;

namespace Noted.Tests.Services;

[TestClass]
public sealed class ExceptionDiagnosticsSetup
{
    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        ExceptionDiagnostics.LogPath = Path.Combine(
            context.TestRunDirectory!, "Noted", "logs", "exceptions.log");
    }
}
