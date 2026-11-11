namespace KeyPassXCCloudSync;

// A provider can have many individual HTTP requests in one phase. Its total
// phase deadline is independent of all other clouds, and never substitutes
// cancellation by the application for a normal provider failure.
internal static class CloudDeadline
{
    internal static CancellationTokenSource Start(CloudOptions cloud,CancellationToken external,int multiplier=3)
    {
        var budget=CancellationTokenSource.CreateLinkedTokenSource(external);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(
            (long)Math.Max(15,cloud.TimeoutSeconds)*multiplier,15,1800)));
        return budget;
    }
}
