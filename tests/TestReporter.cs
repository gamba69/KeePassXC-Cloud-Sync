// Shared result formatting for all executable regression suites.
// The build script validates the exit code separately; text alone is never proof of success.
internal sealed class ReportedTestFailureException : Exception
{
    internal ReportedTestFailureException(string name, Exception inner)
        : base(name + ": " + inner.Message, inner) { }
}

internal static class TestReporter
{
    private static string Clean(string value) => value.Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal).Trim();

    internal static void Pass(string suite, string name)
    {
        Console.WriteLine("[PASS] " + Clean(suite) + " / " + Clean(name));
        Console.Out.Flush();
    }

    private static void Fail(string suite, string name, Exception error)
    {
        Console.Error.WriteLine("[FAIL] " + Clean(suite) + " / " + Clean(name)
            + " : " + error.GetType().Name + ": " + Clean(error.Message));
        Console.Error.Flush();
    }

    internal static void Check(string suite, string name, bool passed)
    {
        if (passed) { Pass(suite, name); return; }
        var failure = new InvalidOperationException("Assertion failed");
        Fail(suite, name, failure);
        throw new ReportedTestFailureException(name, failure);
    }

    internal static void Run(string suite, string name, Action test)
    {
        try { test(); Pass(suite, name); }
        catch (Exception exception)
        {
            if (exception is not ReportedTestFailureException) Fail(suite, name, exception);
            throw new ReportedTestFailureException(name, exception);
        }
    }

    internal static async Task RunAsync(string suite, string name, Func<Task> test)
    {
        try { await test(); Pass(suite, name); }
        catch (Exception exception)
        {
            if (exception is not ReportedTestFailureException) Fail(suite, name, exception);
            throw new ReportedTestFailureException(name, exception);
        }
    }

    internal static void Unhandled(string suite, Exception exception)
    {
        if (exception is not ReportedTestFailureException)
            Fail(suite, "unexpected failure", exception);
    }
}
