namespace RecursiveAI.OneArchive.Internal;

/// <summary>
/// The synchronous entry points run the same code as the asynchronous ones with the "async" flag off, in which every
/// await is on an operation that has already completed. These helpers read such a result and fail loudly if that
/// ever stops being true.
/// </summary>
internal static class Sync
{
    public static T Result<T>(ValueTask<T> task)
    {
        if (!task.IsCompleted)
        {
            throw new InvalidOperationException("A synchronous archive operation did not complete synchronously.");
        }

        return task.GetAwaiter().GetResult();
    }

    public static void Result(ValueTask task)
    {
        if (!task.IsCompleted)
        {
            throw new InvalidOperationException("A synchronous archive operation did not complete synchronously.");
        }

        task.GetAwaiter().GetResult();
    }
}
