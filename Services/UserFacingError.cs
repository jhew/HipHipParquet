using System.IO;
using System.Text.Json;

namespace HipHipParquet.Services;

/// <summary>
/// Turns exceptions into wording someone working with data files can act on.
/// Anything unrecognised falls through to the exception's own message, so detail
/// is never lost — it is only reworded where we can genuinely do better.
/// </summary>
public static class UserFacingError
{
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);
    private const int ErrorDiskFull = unchecked((int)0x80070070);
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);

    public static string Describe(Exception? ex)
    {
        if (ex is null)
            return "An unknown error occurred.";

        // Async call stacks often surface the real cause wrapped one level down.
        if (ex is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
            return Describe(aggregate.InnerExceptions[0]);

        return ex switch
        {
            OperationCanceledException =>
                "The operation was cancelled.",

            UnauthorizedAccessException =>
                "Windows denied access to that file. It may be read-only, in a protected folder, "
                + "or owned by another user account.",

            FileNotFoundException =>
                "That file no longer exists. It may have been moved, renamed or deleted since it was last opened.",

            DirectoryNotFoundException =>
                "That folder no longer exists. It may have been moved, renamed or deleted.",

            PathTooLongException =>
                "The full path to that file is too long for Windows to open. Try moving it to a shorter path.",

            OutOfMemoryException =>
                "The file is too large to fit in available memory. Try opening it with a smaller row limit, "
                + "or query it from the Query Hub instead of loading every row.",

            JsonException =>
                "The file is not valid JSON, so it could not be read.",

            IOException io => DescribeIoError(io),

            NotSupportedException =>
                "That file or operation is not supported.",

            FormatException =>
                "A value in the file was not in the expected format.",

            _ => string.IsNullOrWhiteSpace(ex.Message)
                ? "An unexpected error occurred."
                : ex.Message
        };
    }

    private static string DescribeIoError(IOException io) => io.HResult switch
    {
        ErrorSharingViolation or ErrorLockViolation =>
            "The file is open in another program. Close it there and try again.",

        ErrorDiskFull or ErrorHandleDiskFull =>
            "There is not enough free space on the drive to finish writing the file.",

        _ => string.IsNullOrWhiteSpace(io.Message)
            ? "The file could not be read or written."
            : io.Message
    };
}
