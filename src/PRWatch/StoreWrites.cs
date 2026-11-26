namespace PRWatch;

/// <summary>Which exceptions a store write (<see cref="UsageLedger"/>, <see cref="StatusStore"/>) catches: every
/// exception from serialization or the file write is non-fatal and keeps the data in memory with a fixed error
/// code, except the runtime's fatal ones, which always leave the store.</summary>
internal static class StoreWrites
{
    /// <summary>False only for <see cref="OutOfMemoryException"/>, <see cref="InsufficientExecutionStackException"/>,
    /// <see cref="AccessViolationException"/> and <see cref="ThreadInterruptedException"/>; true for every other
    /// exception. The list is complete for .NET: <see cref="StackOverflowException"/> cannot be caught, and
    /// <c>ThreadAbortException</c> is never thrown on .NET Core.</summary>
    public static bool IsNonFatal(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is not (OutOfMemoryException or InsufficientExecutionStackException
            or AccessViolationException or ThreadInterruptedException);
    }
}
