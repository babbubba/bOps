namespace bOps.Packages.Scheduler.Windows.Tests;

internal sealed class TaskSchedulerIntegrationFactAttribute : FactAttribute
{
    public TaskSchedulerIntegrationFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BOPS_RUN_REAL_SCHEDULER_TESTS"), "1", StringComparison.Ordinal))
            Skip = "Requires Task Scheduler registration permission; set BOPS_RUN_REAL_SCHEDULER_TESTS=1 on Windows CI or an explicitly provisioned host.";
    }
}
