using System.IO;
using System.Text.Json;
using HipHipParquet.Services;

namespace HipHipParquet.Tests;

public class UserFacingErrorTests
{
    [Fact]
    public void Describe_NullException_ReturnsGenericWording()
    {
        Assert.Equal("An unknown error occurred.", UserFacingError.Describe(null));
    }

    [Fact]
    public void Describe_UnknownExceptionType_PreservesItsOwnMessage()
    {
        var ex = new InvalidOperationException("Something very specific went wrong.");

        Assert.Equal("Something very specific went wrong.", UserFacingError.Describe(ex));
    }

    [Fact]
    public void Describe_ExceptionWithBlankMessage_FallsBackToWording()
    {
        var ex = new InvalidOperationException("   ");

        Assert.Equal("An unexpected error occurred.", UserFacingError.Describe(ex));
    }

    [Fact]
    public void Describe_AccessDenied_ExplainsWhy()
    {
        var text = UserFacingError.Describe(new UnauthorizedAccessException("Access to the path is denied."));

        Assert.Contains("denied access", text);
        Assert.Contains("read-only", text);
    }

    [Fact]
    public void Describe_MissingFile_SaysItIsGone()
    {
        var text = UserFacingError.Describe(new FileNotFoundException("Could not find file."));

        Assert.Contains("no longer exists", text);
    }

    [Fact]
    public void Describe_OutOfMemory_SuggestsARowLimitOrQuery()
    {
        var text = UserFacingError.Describe(new OutOfMemoryException());

        Assert.Contains("too large", text);
        Assert.Contains("Query Hub", text);
    }

    [Fact]
    public void Describe_SharingViolation_TellsTheUserToCloseTheOtherProgram()
    {
        var ex = new IOException("The process cannot access the file", unchecked((int)0x80070020));

        Assert.Contains("open in another program", UserFacingError.Describe(ex));
    }

    [Fact]
    public void Describe_DiskFull_SaysThereIsNoSpace()
    {
        var ex = new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));

        Assert.Contains("not enough free space", UserFacingError.Describe(ex));
    }

    [Fact]
    public void Describe_UnrecognisedIoError_KeepsTheOriginalDetail()
    {
        var ex = new IOException("A very particular I/O failure.");

        Assert.Equal("A very particular I/O failure.", UserFacingError.Describe(ex));
    }

    [Fact]
    public void Describe_Cancellation_ReadsAsANormalOutcome()
    {
        Assert.Equal("The operation was cancelled.", UserFacingError.Describe(new OperationCanceledException()));
    }

    [Fact]
    public void Describe_InvalidJson_SaysSo()
    {
        Exception ex;
        try
        {
            JsonSerializer.Deserialize<Dictionary<string, string>>("{ not json");
            throw new InvalidOperationException("expected a JsonException");
        }
        catch (JsonException json)
        {
            ex = json;
        }

        Assert.Contains("not valid JSON", UserFacingError.Describe(ex));
    }

    [Fact]
    public void Describe_SingleInnerAggregate_UnwrapsToTheRealCause()
    {
        var aggregate = new AggregateException(new UnauthorizedAccessException("denied"));

        Assert.Contains("denied access", UserFacingError.Describe(aggregate));
    }

    [Fact]
    public void Describe_MultiInnerAggregate_DoesNotUnwrap()
    {
        var aggregate = new AggregateException(
            new UnauthorizedAccessException("a"),
            new FileNotFoundException("b"));

        // More than one cause: keep the aggregate's own summary rather than picking one.
        Assert.DoesNotContain("denied access", UserFacingError.Describe(aggregate));
    }
}
