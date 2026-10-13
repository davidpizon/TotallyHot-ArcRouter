namespace TotallyHot.ArcRouter.Tests.Sessions.Fixtures;

/// <summary>
/// Thrown when a conversation zip cannot be opened or does not match schema version 1. The message
/// names the failure so a test assertion can pin the refusal path without parsing an enum.
/// </summary>
internal sealed class ConversationZipException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ConversationZipException"/> class.</summary>
    /// <param name="message">Why the zip was refused.</param>
    public ConversationZipException(string message)
        : base(message)
    {
    }
}
